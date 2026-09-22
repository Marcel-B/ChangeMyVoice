using ChangeMyVoice.Application.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChangeMyVoice.Adapters.Inference;

/// <summary>
/// Prüft, ob Modell und Werkzeuge einsatzbereit sind.
/// </summary>
/// <remarks>
/// Bewusst ohne vollständigen Probelauf: Da die Modelle bei jedem Auftrag ohnehin
/// neu geladen werden, brächte ein Aufwärmlauf keinen Zeitvorteil, würde den
/// Dienststart aber um Minuten verzögern. Geprüft wird deshalb nur, was schnell
/// geht — und genau das macht den Unterschied zwischen „Dienst läuft, scheitert
/// aber am ersten Auftrag“ und einer ehrlichen Startmeldung.
/// </remarks>
public sealed class PythonEnvironmentProbe(
    IOptions<InferenceOptions> inference,
    IOptions<AudioToolingOptions> tooling,
    ILogger<PythonEnvironmentProbe> logger) : IInferenceEnvironmentProbe
{
    private readonly InferenceOptions _inference = inference.Value;
    private readonly AudioToolingOptions _tooling = tooling.Value;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReadinessCheck>> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        var checks = new List<ReadinessCheck>
        {
            FileCheck("python-interpreter", _inference.PythonExecutable),
            FileCheck("inference-script", _inference.ScriptPath),
            DirectoryCheck("mlx-vc", _inference.WorkingDirectory),
        };

        if (!string.IsNullOrWhiteSpace(_inference.SeedVcPath))
        {
            checks.Add(DirectoryCheck("seed-vc", _inference.SeedVcPath));
        }

        checks.Add(await ToolCheck("ffmpeg", _tooling.FfmpegPath, cancellationToken).ConfigureAwait(false));
        checks.Add(await ToolCheck("ffprobe", _tooling.FfprobePath, cancellationToken).ConfigureAwait(false));
        checks.Add(await TorchCheck(cancellationToken).ConfigureAwait(false));

        return checks;
    }

    private static ReadinessCheck FileCheck(string name, string path) =>
        File.Exists(path)
            ? new ReadinessCheck(name, true)
            : new ReadinessCheck(name, false, "Die Datei wurde nicht gefunden.");

    private static ReadinessCheck DirectoryCheck(string name, string path) =>
        Directory.Exists(path)
            ? new ReadinessCheck(name, true)
            : new ReadinessCheck(name, false, "Das Verzeichnis wurde nicht gefunden.");

    private async Task<ReadinessCheck> ToolCheck(
        string name, string executable, CancellationToken cancellationToken)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(
                executable, ["-version"], null, null,
                TimeSpan.FromSeconds(15), null, cancellationToken).ConfigureAwait(false);

            return result.ExitCode == 0
                ? new ReadinessCheck(name, true)
                : new ReadinessCheck(name, false, "Das Werkzeug meldete einen Fehler.");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "{Tool} ließ sich nicht aufrufen.", name);
            return new ReadinessCheck(name, false, "Das Werkzeug ließ sich nicht aufrufen.");
        }
    }

    /// <summary>
    /// Prüft in einem kurzen Lauf, ob Torch da ist und die Grafikbeschleunigung
    /// zur Verfügung steht — ohne irgendetwas zu installieren.
    /// </summary>
    private async Task<ReadinessCheck> TorchCheck(CancellationToken cancellationToken)
    {
        if (!File.Exists(_inference.PythonExecutable))
        {
            return new ReadinessCheck("torch-mps", false, "Der Interpreter fehlt.");
        }

        try
        {
            var result = await ProcessRunner.RunAsync(
                _inference.PythonExecutable,
                ["-c", "import torch; print(torch.backends.mps.is_available())"],
                _inference.WorkingDirectory,
                null,
                TimeSpan.FromSeconds(60),
                null,
                cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                return new ReadinessCheck("torch-mps", false, "Torch ließ sich nicht laden.");
            }

            var available = result.StandardOutput.Contains("True", StringComparison.Ordinal);

            return new ReadinessCheck(
                "torch-mps", available,
                available ? null : "Die Grafikbeschleunigung steht nicht zur Verfügung.");
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Die Torch-Prüfung ließ sich nicht ausführen.");
            return new ReadinessCheck("torch-mps", false, "Die Prüfung ließ sich nicht ausführen.");
        }
    }
}
