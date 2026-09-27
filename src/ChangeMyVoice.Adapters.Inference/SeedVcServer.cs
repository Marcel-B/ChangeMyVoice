using System.Diagnostics;
using System.Text;

namespace ChangeMyVoice.Adapters.Inference;

/// <summary>Welcher Checkpoint geladen ist.</summary>
/// <param name="F0Condition">Gesangspfad (44,1 kHz mit Tonhöhe) oder Sprachpfad.</param>
/// <param name="Fp16">Ob mit halber Genauigkeit gerechnet wird.</param>
/// <remarks>
/// Beides legt fest, was <c>inference.load_models</c> lädt. Ein Auftrag mit
/// anderen Werten braucht deshalb einen neuen Prozess.
/// </remarks>
internal readonly record struct ModelKey(bool F0Condition, bool Fp16);

/// <summary>
/// Ein Python-Prozess im Dauerbetrieb (<c>--serve</c>), der das Modell
/// zwischen den Aufträgen geladen hält.
/// </summary>
/// <remarks>
/// Das Protokoll steht oben in <c>scripts/changemyvoice_infer.py</c>: eine
/// Zeile je Auftrag hinein, eine Zeile je Antwort heraus. Der Prozess endet,
/// wenn seine Standardeingabe geschlossen wird; stirbt der Dienst hart, sieht
/// er deshalb das Ende der Eingabe und beendet sich nach dem laufenden Auftrag
/// selbst.
/// </remarks>
internal sealed class SeedVcServer : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _stderr = new();
    private readonly Lock _stderrLock = new();

    private SeedVcServer(Process process, ModelKey key)
    {
        _process = process;
        Key = key;
    }

    /// <summary>Welcher Checkpoint geladen ist.</summary>
    public ModelKey Key { get; }

    /// <summary>Ob das Modell fertig geladen ist.</summary>
    public bool IsReady { get; private set; }

    /// <summary>Vermerkt, dass die Bereitmeldung gelesen wurde.</summary>
    public void MarkReady() => IsReady = true;

    /// <summary>Die Prozesskennung.</summary>
    public int ProcessId => _process.Id;

    /// <summary>Ob der Prozess nicht mehr läuft.</summary>
    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    /// <summary>Startet den Prozess; das Modell lädt er danach von selbst.</summary>
    public static SeedVcServer Start(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        ModelKey key)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? string.Empty,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment)
        {
            startInfo.Environment[name] = value;
        }

        var process = new Process { StartInfo = startInfo };
        var server = new SeedVcServer(process, key);

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (server._stderrLock)
                {
                    server._stderr.AppendLine(e.Data);
                }
            }
        };

        process.Start();
        process.BeginErrorReadLine();
        return server;
    }

    /// <summary>
    /// Liest die nächste Antwortzeile; <c>null</c>, wenn der Prozess vorher
    /// endete.
    /// </summary>
    public async Task<string?> ReadResponseAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await _process.StandardOutput.ReadLineAsync(cancellationToken)
                .ConfigureAwait(false);

            if (line is null)
            {
                return null;
            }

            // Das Skript lenkt alles andere auf die Fehlerausgabe um; was
            // trotzdem durchrutscht, ist keine Antwort.
            if (line.TrimStart().StartsWith('{'))
            {
                return line.Trim();
            }
        }
    }

    /// <summary>Schickt einen Auftrag als eine Zeile.</summary>
    public async Task SendAsync(string requestLine, CancellationToken cancellationToken)
    {
        await _process.StandardInput.WriteLineAsync(requestLine.AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Liefert die seit dem letzten Aufruf gesammelte Fehlerausgabe.</summary>
    public string TakeStandardError()
    {
        lock (_stderrLock)
        {
            var text = _stderr.ToString();
            _stderr.Clear();
            return text;
        }
    }

    /// <summary>Beendet den Prozess sofort, samt allem, was er gestartet hat.</summary>
    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Schon beendet.
        }
    }

    /// <summary>
    /// Beendet den Prozess: erst freundlich über das Ende der Eingabe, nach
    /// kurzer Frist hart.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            _process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // Die Leitung ist schon zu, weil der Prozess nicht mehr läuft.
        }

        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        try
        {
            await _process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill();
        }
        catch (InvalidOperationException)
        {
            // Nie gestartet oder schon aufgeräumt.
        }

        _process.Dispose();
    }
}
