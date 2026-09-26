using System.Globalization;
using System.Text.Json;
using ChangeMyVoice.Application.Ports;
using ChangeMyVoice.Domain.Jobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChangeMyVoice.Adapters.Inference;

/// <summary>
/// Führt die Konvertierung über die eingerichtete Python-Umgebung aus.
/// </summary>
/// <remarks>
/// Gestartet wird ausschließlich der Interpreter mit dem Wrapper-Skript — kein
/// <c>uv run</c>, kein <c>pip</c>. init.md §30 untersagt es ausdrücklich, die
/// funktionierende Umgebung eigenmächtig zu verändern, und ein Test prüft die
/// zusammengebaute Argumentliste genau darauf.
/// <para>
/// Mit <see cref="InferenceOptions.KeepModelLoadedFor" /> über null bleibt der
/// Prozess samt Modell nach einem Lauf stehen (init.md §22) und wird erst nach
/// dieser Leerlaufzeit, auf Anfrage (<see cref="ReleaseAsync" />) oder nach
/// einem Fehler beendet. Die Läufe finden dann nacheinander statt, auch wenn
/// mehrere gleichzeitig eingestellt sind: Es gibt nur ein geladenes Modell.
/// </para>
/// </remarks>
public sealed class MlxVcConversionEngine(
    IOptions<InferenceOptions> options,
    TimeProvider clock,
    ILogger<MlxVcConversionEngine> logger) : IVoiceConversionEngine, IInferenceModelHost, IAsyncDisposable
{
    private readonly InferenceOptions _options = options.Value;

    // Schützt den geladenen Prozess: Ein Lauf, das Entladen nach Leerlauf und
    // das Entladen auf Anfrage schließen sich gegenseitig aus.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private SeedVcServer? _server;
    private DateTimeOffset? _loadedSince;
    private DateTimeOffset? _unloadAt;
    private ITimer? _idleTimer;
    private bool _disposed;

    /// <summary>
    /// Zeichenfolgen, die in einem Aufruf nichts zu suchen haben, weil sie die
    /// Python-Umgebung verändern würden.
    /// </summary>
    internal static readonly string[] ForbiddenArgumentMarkers =
        ["pip", "uv", "--upgrade", "-U", "sync", "lock", "install"];

    /// <summary>Ob das Modell zwischen den Läufen geladen bleibt.</summary>
    internal bool KeepsModelLoaded => _options.KeepModelLoadedFor > TimeSpan.Zero;

    /// <summary>Baut die Argumentliste für den Dauerbetrieb zusammen.</summary>
    /// <remarks>
    /// Nur die Schalter, die bestimmen, welcher Checkpoint geladen wird; alles
    /// andere kommt je Auftrag über <see cref="BuildServeRequest" />.
    /// </remarks>
    internal IReadOnlyList<string> BuildServeArguments(ModelKey key)
    {
        var arguments = new List<string> { _options.ScriptPath, "--serve", "--backend", _options.Backend };

        if (key.F0Condition)
        {
            arguments.Add("--f0-condition");
        }

        if (!key.Fp16)
        {
            arguments.Add("--no-fp16");
        }

        return arguments;
    }

    /// <summary>Baut die Auftragszeile für den Dauerbetrieb zusammen.</summary>
    internal static string BuildServeRequest(ConversionRequest request) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["source"] = request.Source.Locator,
            ["reference"] = request.Reference.Locator,
            ["output"] = request.Output.Locator,
            ["diffusionSteps"] = request.Options.DiffusionSteps,
            ["inferenceCfgRate"] = request.Options.InferenceCfgRate,
            ["lengthAdjust"] = request.Options.LengthAdjust,
            ["semiToneShift"] = request.Options.SemiToneShift,
            ["autoF0Adjust"] = request.Options.AutoF0Adjust,
        });

    /// <summary>Baut die Argumentliste für einen Lauf zusammen.</summary>
    internal IReadOnlyList<string> BuildArguments(ConversionRequest request)
    {
        var arguments = new List<string>
        {
            _options.ScriptPath,
            "--source", request.Source.Locator,
            "--reference", request.Reference.Locator,
            "--output", request.Output.Locator,
            "--backend", _options.Backend,
            "--diffusion-steps",
            request.Options.DiffusionSteps.ToString(CultureInfo.InvariantCulture),
            "--inference-cfg-rate",
            request.Options.InferenceCfgRate.ToString("0.###", CultureInfo.InvariantCulture),
            "--length-adjust",
            request.Options.LengthAdjust.ToString("0.###", CultureInfo.InvariantCulture),
        };

        if (request.Options.F0Condition)
        {
            arguments.Add("--f0-condition");
        }

        if (request.Options.SemiToneShift != 0)
        {
            arguments.Add("--semi-tone-shift");
            arguments.Add(request.Options.SemiToneShift.ToString(CultureInfo.InvariantCulture));
        }

        if (request.Options.AutoF0Adjust)
        {
            arguments.Add("--auto-f0-adjust");
        }

        if (!request.Options.Fp16)
        {
            arguments.Add("--no-fp16");
        }

        return arguments;
    }

    /// <summary>Stellt die Umgebungsvariablen für den Lauf zusammen.</summary>
    internal IReadOnlyDictionary<string, string> BuildEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);

        // Beides liest seed_vc_infer.py aus der Umgebung. Fest vorgegeben, damit
        // nicht die relativen Standardwerte greifen und alle Läufe dieselben
        // Checkpoints verwenden.
        if (!string.IsNullOrWhiteSpace(_options.SeedVcPath))
        {
            environment["SEED_VC_PATH"] = _options.SeedVcPath;
        }

        if (!string.IsNullOrWhiteSpace(_options.HuggingFaceCachePath))
        {
            environment["HF_HUB_CACHE"] = _options.HuggingFaceCachePath;
        }

        return environment;
    }

    /// <inheritdoc />
    public Task<ConversionOutcome> ConvertAsync(
        ConversionRequest request,
        Action<int>? onProcessStarted = null,
        CancellationToken cancellationToken = default) =>
        KeepsModelLoaded
            ? ConvertOnServerAsync(request, onProcessStarted, cancellationToken)
            : ConvertOnceAsync(request, onProcessStarted, cancellationToken);

    /// <summary>Ein eigener Prozess nur für diesen Lauf, wie vor dem Dauerbetrieb.</summary>
    private async Task<ConversionOutcome> ConvertOnceAsync(
        ConversionRequest request,
        Action<int>? onProcessStarted,
        CancellationToken cancellationToken)
    {
        var arguments = BuildArguments(request);

        ProcessResult result;

        try
        {
            result = await ProcessRunner.RunAsync(
                _options.PythonExecutable,
                arguments,
                _options.WorkingDirectory,
                BuildEnvironment(),
                _options.Timeout,
                onProcessStarted,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Der Inferenzprozess ließ sich nicht starten.");

            return ConversionOutcome.Failure(
                ConversionErrorCode.ModelLoadFailed,
                "Die Konvertierungsumgebung ließ sich nicht starten.");
        }

        if (result.TimedOut)
        {
            logger.LogWarning("Der Lauf überschritt das Zeitlimit von {Timeout}.", _options.Timeout);

            return ConversionOutcome.Failure(
                ConversionErrorCode.Timeout, "Die Konvertierung hat zu lange gedauert.");
        }

        LogStandardError(result.StandardError);

        var line = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.StartsWith('{'));

        if (line is null)
        {
            logger.LogWarning(
                "Der Lauf endete mit Code {ExitCode} ohne verwertbare Ausgabe.", result.ExitCode);
        }

        return Interpret(line, result.StandardError);
    }

    /// <summary>Ein Lauf im stehenden Prozess, der bei Bedarf erst gestartet wird.</summary>
    private async Task<ConversionOutcome> ConvertOnServerAsync(
        ConversionRequest request,
        Action<int>? onProcessStarted,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CancelIdleUnload();

            var key = new ModelKey(request.Options.F0Condition, request.Options.Fp16);

            if (_server is { } running && (running.HasExited || running.Key != key))
            {
                logger.LogInformation(
                    running.HasExited
                        ? "Der Inferenzprozess lief nicht mehr und wird neu gestartet."
                        : "Der Auftrag braucht ein anderes Modell; der Inferenzprozess wird neu gestartet.");
                await StopServerAsync().ConfigureAwait(false);
            }

            using var timeout = new CancellationTokenSource(_options.Timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeout.Token);

            var outcome = await RunOnServerAsync(request, key, onProcessStarted, linked.Token)
                .ConfigureAwait(false);

            if (outcome is null)
            {
                // Zeitablauf oder Abbruch: Der Prozess wurde dabei beendet.
                await StopServerAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                logger.LogWarning("Der Lauf überschritt das Zeitlimit von {Timeout}.", _options.Timeout);

                return ConversionOutcome.Failure(
                    ConversionErrorCode.Timeout, "Die Konvertierung hat zu lange gedauert.");
            }

            if (!outcome.IsSuccess)
            {
                // Nach einem Fehler, gerade nach zu wenig Speicher oder einem
                // Fehler der Grafikbeschleunigung, ist dem geladenen Zustand
                // nicht mehr zu trauen. Der nächste Lauf lädt frisch.
                await StopServerAsync().ConfigureAwait(false);
            }

            return outcome;
        }
        finally
        {
            ScheduleIdleUnload();
            _gate.Release();
        }
    }

    /// <summary>
    /// Führt den Lauf aus; <c>null</c> bei Zeitablauf oder Abbruch.
    /// </summary>
    private async Task<ConversionOutcome?> RunOnServerAsync(
        ConversionRequest request,
        ModelKey key,
        Action<int>? onProcessStarted,
        CancellationToken cancellationToken)
    {
        TimeSpan? modelLoad = TimeSpan.Zero;

        if (_server is null)
        {
            try
            {
                _server = SeedVcServer.Start(
                    _options.PythonExecutable,
                    BuildServeArguments(key),
                    _options.WorkingDirectory,
                    BuildEnvironment(),
                    key);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Der Inferenzprozess ließ sich nicht starten.");
                _server = null;

                return ConversionOutcome.Failure(
                    ConversionErrorCode.ModelLoadFailed,
                    "Die Konvertierungsumgebung ließ sich nicht starten.");
            }

            _loadedSince = clock.GetUtcNow();
        }

        var server = _server;

        // Auch bei einem schon laufenden Prozess: Jeder Auftrag vermerkt die
        // Kennung, damit die Wiederherstellung nach einem Absturz den Prozess
        // findet, der zuletzt für ihn rechnete.
        onProcessStarted?.Invoke(server.ProcessId);

        // Ein Lesen aus der Leitung lässt sich nicht überall zuverlässig
        // abbrechen; das Beenden des Prozesses schließt sie in jedem Fall.
        using var registration = cancellationToken.Register(server.Kill);

        try
        {
            if (!server.IsReady)
            {
                var ready = await server.ReadResponseAsync(cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    return null;
                }

                if (!TryReadReady(ready, out var loadTime))
                {
                    var loadError = server.TakeStandardError();
                    LogStandardError(loadError);
                    logger.LogWarning("Das Modell ließ sich nicht laden.");
                    return Interpret(ready, loadError);
                }

                server.MarkReady();
                modelLoad = loadTime;
                logger.LogInformation(
                    "Modell geladen in {ModelLoad}; es bleibt {KeepFor} nach dem letzten Lauf im Speicher.",
                    loadTime, _options.KeepModelLoadedFor);
            }
            else
            {
                logger.LogInformation("Das Modell ist bereits geladen.");
            }

            await server.SendAsync(BuildServeRequest(request), cancellationToken).ConfigureAwait(false);
            var line = await server.ReadResponseAsync(cancellationToken).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            var standardError = server.TakeStandardError();
            LogStandardError(standardError);

            if (line is null)
            {
                logger.LogWarning("Der Inferenzprozess endete mitten im Lauf.");
            }

            var outcome = Interpret(line, standardError);

            return outcome.IsSuccess
                ? outcome with { ModelLoadDuration = modelLoad }
                : outcome;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (IOException ex)
        {
            // Die Leitung brach weg, weil der Prozess starb.
            logger.LogWarning(ex, "Die Verbindung zum Inferenzprozess brach ab.");
            var standardError = server.TakeStandardError();
            LogStandardError(standardError);

            return ConversionOutcome.Failure(
                ClassifyFromStandardError(standardError), "Die Konvertierung ist fehlgeschlagen.");
        }
    }

    /// <inheritdoc />
    public InferenceModelState GetState()
    {
        var server = _server;
        var loaded = server is { IsReady: true, HasExited: false };

        return new InferenceModelState(
            loaded,
            IsBusy: _gate.CurrentCount == 0,
            KeepLoadedFor: KeepsModelLoaded ? _options.KeepModelLoadedFor : TimeSpan.Zero,
            LoadedSinceUtc: loaded ? _loadedSince : null,
            UnloadAtUtc: loaded ? _unloadAt : null);
    }

    /// <inheritdoc />
    public async Task<ModelReleaseResult> ReleaseAsync(CancellationToken cancellationToken = default)
    {
        // Nicht warten: Wer den Speicher braucht, soll sofort erfahren, dass
        // noch gerechnet wird, statt womöglich eine Stunde zu hängen.
        if (!await _gate.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            return ModelReleaseResult.Busy;
        }

        try
        {
            CancelIdleUnload();

            if (_server is null)
            {
                return ModelReleaseResult.NotLoaded;
            }

            logger.LogInformation("Das Modell wird auf Anfrage entladen.");
            await StopServerAsync().ConfigureAwait(false);
            return ModelReleaseResult.Released;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            _disposed = true;
            CancelIdleUnload();
            await StopServerAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Beendet den stehenden Prozess. Nur unter <see cref="_gate" /> aufrufen.</summary>
    private async Task StopServerAsync()
    {
        var server = _server;
        _server = null;
        _loadedSince = null;
        _unloadAt = null;

        if (server is not null)
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void CancelIdleUnload()
    {
        _idleTimer?.Dispose();
        _idleTimer = null;
        _unloadAt = null;
    }

    /// <summary>Plant das Entladen nach der Leerlaufzeit. Nur unter <see cref="_gate" /> aufrufen.</summary>
    private void ScheduleIdleUnload()
    {
        CancelIdleUnload();

        if (_server is null || _disposed)
        {
            return;
        }

        _unloadAt = clock.GetUtcNow() + _options.KeepModelLoadedFor;
        _idleTimer = clock.CreateTimer(
            _ => _ = UnloadIdleAsync(), null, _options.KeepModelLoadedFor, Timeout.InfiniteTimeSpan);
    }

    private async Task UnloadIdleAsync()
    {
        // Läuft gerade ein Auftrag, plant der danach selbst neu.
        if (!await _gate.WaitAsync(TimeSpan.Zero).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            // Ein Auftrag kann zwischen Auslösen und Sperre neu geplant haben.
            if (_server is null || _unloadAt is not { } due || clock.GetUtcNow() < due)
            {
                return;
            }

            logger.LogInformation(
                "Das Modell wird nach {KeepFor} ohne Auftrag entladen.", _options.KeepModelLoadedFor);
            CancelIdleUnload();
            await StopServerAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Das Entladen nach Leerlauf ist fehlgeschlagen.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Liest die Meldung, dass das Modell geladen ist.</summary>
    private static bool TryReadReady(string? line, out TimeSpan? modelLoad)
    {
        modelLoad = null;

        if (line is null)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (root.TryGetProperty("status", out var status) && status.GetString() == "ready")
            {
                modelLoad = ReadDuration(root, "modelLoadMs");
                return true;
            }
        }
        catch (JsonException)
        {
            // Dann ist es keine Bereitmeldung; Interpret sagt, was es ist.
        }

        return false;
    }

    private void LogStandardError(string standardError)
    {
        // Die Fehlerausgabe gehört vollständig ins Log, aber niemals in die
        // Antwort an den Aufrufer.
        if (!string.IsNullOrWhiteSpace(standardError))
        {
            logger.LogInformation("Ausgabe des Inferenzlaufs: {Error}", standardError.Trim());
        }
    }

    /// <summary>Wertet die Antwortzeile eines Laufs aus.</summary>
    private ConversionOutcome Interpret(string? line, string standardError)
    {
        if (line is null)
        {
            // Das Skript kam nicht bis zur Ausgabe — etwa weil der Interpreter
            // selbst gescheitert ist. Dann bleibt nur die grobe Zuordnung.
            return ConversionOutcome.Failure(
                ClassifyFromStandardError(standardError),
                "Die Konvertierung ist fehlgeschlagen.");
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;

            if (status == "ok")
            {
                return ConversionOutcome.Success(
                    ReadDuration(root, "modelLoadMs"),
                    ReadDuration(root, "inferenceMs"));
            }

            var code = ParseErrorCode(root.TryGetProperty("code", out var c) ? c.GetString() : null);

            // Die Meldung aus Python kann Pfade enthalten; nach außen geht
            // deshalb ein knapper, unverfänglicher Text.
            return ConversionOutcome.Failure(code, DescribeFor(code));
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Die Ausgabe des Laufs war kein gültiges JSON: {Line}", line);

            return ConversionOutcome.Failure(
                ConversionErrorCode.InferenceFailed, "Die Konvertierung lieferte keine gültige Antwort.");
        }
    }

    private static TimeSpan? ReadDuration(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var ms)
            ? TimeSpan.FromMilliseconds(ms)
            : null;

    private static ConversionErrorCode ParseErrorCode(string? code) => code switch
    {
        "INVALID_AUDIO" => ConversionErrorCode.InvalidAudio,
        "UNSUPPORTED_FORMAT" => ConversionErrorCode.UnsupportedFormat,
        "REFERENCE_TOO_SHORT" => ConversionErrorCode.ReferenceTooShort,
        "MODEL_LOAD_FAILED" => ConversionErrorCode.ModelLoadFailed,
        "MODEL_DOWNLOAD_FAILED" => ConversionErrorCode.ModelDownloadFailed,
        "OUT_OF_MEMORY" => ConversionErrorCode.OutOfMemory,
        "MPS_ERROR" => ConversionErrorCode.MpsError,
        "OUTPUT_NOT_CREATED" => ConversionErrorCode.OutputNotCreated,
        "TIMEOUT" => ConversionErrorCode.Timeout,
        _ => ConversionErrorCode.InferenceFailed,
    };

    /// <summary>
    /// Grobe Zuordnung anhand der Fehlerausgabe, wenn das Skript gar nicht erst
    /// zu seiner eigenen Auswertung kam.
    /// </summary>
    internal static ConversionErrorCode ClassifyFromStandardError(string standardError)
    {
        var text = standardError.ToLowerInvariant();

        if (text.Contains("out of memory", StringComparison.Ordinal) ||
            text.Contains("memoryerror", StringComparison.Ordinal))
        {
            return ConversionErrorCode.OutOfMemory;
        }

        if (text.Contains("mps", StringComparison.Ordinal) ||
            text.Contains("metal", StringComparison.Ordinal))
        {
            return ConversionErrorCode.MpsError;
        }

        if (text.Contains("huggingface", StringComparison.Ordinal) ||
            text.Contains("hf_hub", StringComparison.Ordinal))
        {
            return ConversionErrorCode.ModelDownloadFailed;
        }

        if (text.Contains("modulenotfounderror", StringComparison.Ordinal) ||
            text.Contains("checkpoint", StringComparison.Ordinal) ||
            text.Contains("no such file", StringComparison.Ordinal))
        {
            return ConversionErrorCode.ModelLoadFailed;
        }

        return ConversionErrorCode.InferenceFailed;
    }

    private static string DescribeFor(ConversionErrorCode code) => code switch
    {
        ConversionErrorCode.InvalidAudio => "Das Eingangsmaterial ließ sich nicht verarbeiten.",
        ConversionErrorCode.ModelLoadFailed => "Das Modell konnte nicht geladen werden.",
        ConversionErrorCode.ModelDownloadFailed => "Ein benötigtes Modell konnte nicht bezogen werden.",
        ConversionErrorCode.OutOfMemory => "Der Arbeitsspeicher reichte für diesen Lauf nicht aus.",
        ConversionErrorCode.MpsError => "Die Grafikbeschleunigung meldete einen Fehler.",
        ConversionErrorCode.OutputNotCreated => "Der Lauf erzeugte keine verwertbare Ausgabedatei.",
        ConversionErrorCode.Timeout => "Die Konvertierung hat zu lange gedauert.",
        _ => "Die Konvertierung ist fehlgeschlagen.",
    };
}
