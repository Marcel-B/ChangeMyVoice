using ChangeMyVoice.Application.Ports;
using ChangeMyVoice.Domain.Voices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChangeMyVoice.Adapters.Storage;

/// <summary>
/// Legt Referenzstimmen im Dateisystem ab.
/// </summary>
/// <remarks>
/// Dieser Ordner wird vom Aufräumlauf niemals angefasst. Referenzstimmen sind
/// das einzige, was der Dienst dauerhaft behält.
/// </remarks>
public sealed class FileSystemVoiceStorage : IVoiceStorage
{
    private const string MasterFileName = "reference.wav";

    private readonly StorageOptions _options;
    private readonly ILogger<FileSystemVoiceStorage> _logger;

    /// <summary>Erzeugt den Adapter und legt das Wurzelverzeichnis an.</summary>
    public FileSystemVoiceStorage(
        IOptions<StorageOptions> options, ILogger<FileSystemVoiceStorage> logger)
    {
        _options = options.Value;
        _logger = logger;
        Directory.CreateDirectory(_options.VoicesRoot);
    }

    /// <inheritdoc />
    public AudioArtifactRef ReserveMaster(VoiceId id)
    {
        Directory.CreateDirectory(DirectoryFor(id));
        return new AudioArtifactRef(PathFor(id));
    }

    /// <inheritdoc />
    public AudioArtifactRef GetMaster(VoiceId id) => new(PathFor(id));

    /// <inheritdoc />
    public bool MasterExists(VoiceId id) => File.Exists(PathFor(id));

    /// <inheritdoc />
    public Task<Stream?> OpenMasterAsync(VoiceId id, CancellationToken cancellationToken = default)
    {
        var path = PathFor(id);

        if (!File.Exists(path))
        {
            return Task.FromResult<Stream?>(null);
        }

        // Zum Lesen freigegeben, damit ein Auftrag, der die Stimme gerade
        // kopiert, nicht auf den Abruf warten muss.
        Stream stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: true);

        return Task.FromResult<Stream?>(stream);
    }

    /// <inheritdoc />
    public Task DeleteAsync(VoiceId id, CancellationToken cancellationToken = default)
    {
        var directory = DirectoryFor(id);

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
            _logger.LogInformation("Referenzstimme {VoiceId} entfernt.", id);
        }

        return Task.CompletedTask;
    }

    private string DirectoryFor(VoiceId id) => Path.Combine(_options.VoicesRoot, id.ToString());

    private string PathFor(VoiceId id) => Path.Combine(DirectoryFor(id), MasterFileName);
}
