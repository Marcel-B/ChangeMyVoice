using System.Diagnostics;
using System.Text;

namespace ChangeMyVoice.Adapters.Inference;

/// <summary>Das Ergebnis eines Prozessaufrufs.</summary>
/// <param name="ExitCode">Der Rückgabewert.</param>
/// <param name="StandardOutput">Die Standardausgabe.</param>
/// <param name="StandardError">Die Fehlerausgabe.</param>
/// <param name="TimedOut">Ob der Aufruf abgebrochen wurde.</param>
internal sealed record ProcessResult(
    int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

/// <summary>
/// Startet externe Programme und sammelt deren Ausgaben ein.
/// </summary>
/// <remarks>
/// Verwendet bewusst <see cref="Process" /> direkt: Nur
/// <see cref="Process.Kill(bool)" /> beendet den gesamten Prozessbaum, und genau
/// den brauchen wir — <c>run_backend</c> startet seinerseits einen weiteren
/// Unterprozess, der sonst als Waise weiterliefe und die GPU belegt.
/// </remarks>
internal static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        TimeSpan timeout,
        Action<int>? onStarted,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? string.Empty,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        onStarted?.Invoke(process.Id);

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillTree(process);

            // Dem Abbruch des Aufrufers Vorrang geben: Er bedeutet, dass der
            // Dienst herunterfährt, und soll als solcher weitergereicht werden.
            cancellationToken.ThrowIfCancellationRequested();

            return new ProcessResult(-1, stdout.ToString(), stderr.ToString(), TimedOut: true);
        }

        return new ProcessResult(
            process.ExitCode, stdout.ToString(), stderr.ToString(), TimedOut: false);
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Der Prozess ist zwischenzeitlich selbst beendet — nichts zu tun.
        }
    }
}
