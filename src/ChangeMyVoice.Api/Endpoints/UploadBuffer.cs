using ChangeMyVoice.Adapters.Storage;
using ChangeMyVoice.Application.Ports;

namespace ChangeMyVoice.Api.Endpoints;

/// <summary>
/// Nimmt eine hochgeladene Datei entgegen und legt sie zur Prüfung ab.
/// </summary>
/// <remarks>
/// Der Inhalt wird direkt auf die Platte geschrieben und niemals vollständig in
/// den Arbeitsspeicher geladen: Eine Gesangsaufnahme kann mehrere hundert
/// Megabyte groß sein. Bleibt eine solche Datei doch einmal liegen, etwa weil
/// der Dienst mitten im Vorgang abstürzt, räumt der Aufräumlauf das
/// Zwischenverzeichnis später mit weg.
/// </remarks>
internal static class UploadBuffer
{
    /// <summary>Schreibt den Upload in das Zwischenverzeichnis.</summary>
    public static async Task<AudioArtifactRef> StoreAsync(
        IFormFile file,
        FileSystemJobWorkspaceStore workspaces,
        CancellationToken cancellationToken)
    {
        var suffix = Path.GetExtension(file.FileName);

        if (string.IsNullOrWhiteSpace(suffix) || suffix.Length > 10)
        {
            suffix = ".bin";
        }

        var target = workspaces.CreateTemporaryUpload(suffix);

        await using var destination = new FileStream(
            target.Locator, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 64 * 1024, useAsync: true);

        await file.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);

        return target;
    }

    /// <summary>Entfernt die Zwischendatei.</summary>
    public static void Discard(AudioArtifactRef upload)
    {
        try
        {
            if (File.Exists(upload.Locator))
            {
                File.Delete(upload.Locator);
            }
        }
        catch (IOException)
        {
            // Der Aufräumlauf erwischt sie später; das ist kein Grund, die
            // Antwort an den Aufrufer scheitern zu lassen.
        }
    }
}
