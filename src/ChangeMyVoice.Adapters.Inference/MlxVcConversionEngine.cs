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
/// </remarks>
public sealed class MlxVcConversionEngine(
    IOptions<InferenceOptions> options,
    ILogger<MlxVcConversionEngine> logger) : IVoiceConversionEngine
{
    private readonly InferenceOptions _options = options.Value;

    /// <summary>
    /// Zeichenfolgen, die in einem Aufruf nichts zu suchen haben, weil sie die
    /// Python-Umgebung verändern würden.
    /// </summary>
    internal static readonly string[] ForbiddenArgumentMarkers =
        ["pip", "uv", "--upgrade", "-U", "sync", "lock", "install"];

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
    public async Task<ConversionOutcome> ConvertAsync(
        ConversionRequest request,
        Action<int>? onProcessStarted = null,
        CancellationToken cancellationToken = default)
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

        // Die Fehlerausgabe gehört vollständig ins Log, aber niemals in die
        // Antwort an den Aufrufer.
        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            logger.LogInformation("Ausgabe des Inferenzlaufs: {Error}", result.StandardError.Trim());
        }

        return Interpret(result);
    }

    private ConversionOutcome Interpret(ProcessResult result)
    {
        var line = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.StartsWith('{'));

        if (line is null)
        {
            // Das Skript kam nicht bis zur Ausgabe — etwa weil der Interpreter
            // selbst gescheitert ist. Dann bleibt nur die grobe Zuordnung.
            logger.LogWarning(
                "Der Lauf endete mit Code {ExitCode} ohne verwertbare Ausgabe.", result.ExitCode);

            return ConversionOutcome.Failure(
                ClassifyFromStandardError(result.StandardError),
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

            var code = root.TryGetProperty("code", out var c) ? c.GetString() : null;
            var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;

            return ConversionOutcome.Failure(
                ParseErrorCode(code),
                // Die Meldung aus Python kann Pfade enthalten; nach außen geht
                // deshalb ein knapper, unverfänglicher Text.
                DescribeFor(ParseErrorCode(code)));
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
