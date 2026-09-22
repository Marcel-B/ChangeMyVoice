using ChangeMyVoice.Application.Ports;
using ChangeMyVoice.Domain.Jobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChangeMyVoice.Adapters.Storage;

/// <summary>
/// Verwaltet je Auftrag ein eigenes Arbeitsverzeichnis im Dateisystem.
/// </summary>
/// <remarks>
/// Aufbau nach init.md §21: <c>jobs/&lt;id&gt;/{source,reference,output}.wav</c>.
/// Alle Löschvorgänge sind strikt auf <c>jobs/</c> und <c>tmp/</c> beschränkt —
/// der Ordner der Referenzstimmen wird hier nirgends berührt.
/// </remarks>
public sealed class FileSystemJobWorkspaceStore : IJobWorkspaceStore
{
    private readonly StorageOptions _options;
    private readonly ILogger<FileSystemJobWorkspaceStore> _logger;

    /// <summary>Erzeugt den Adapter und legt die Wurzelverzeichnisse an.</summary>
    public FileSystemJobWorkspaceStore(
        IOptions<StorageOptions> options, ILogger<FileSystemJobWorkspaceStore> logger)
    {
        _options = options.Value;
        _logger = logger;
        Directory.CreateDirectory(_options.JobsRoot);
        Directory.CreateDirectory(_options.TempRoot);
    }

    /// <inheritdoc />
    public JobWorkspace Create(JobId jobId)
    {
        Directory.CreateDirectory(DirectoryFor(jobId));
        return Get(jobId);
    }

    /// <inheritdoc />
    public JobWorkspace Get(JobId jobId)
    {
        var directory = DirectoryFor(jobId);

        return new JobWorkspace(
            jobId,
            new AudioArtifactRef(Path.Combine(directory, "source.wav")),
            new AudioArtifactRef(Path.Combine(directory, "reference.wav")),
            new AudioArtifactRef(Path.Combine(directory, "output.wav")));
    }

    /// <inheritdoc />
    public Task<Stream?> OpenOutputAsync(JobId jobId, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(DirectoryFor(jobId), "output.wav");

        if (!File.Exists(path))
        {
            return Task.FromResult<Stream?>(null);
        }

        // Zum Lesen freigegeben, damit ein paralleler Abruf nicht blockiert.
        Stream stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: true);

        return Task.FromResult<Stream?>(stream);
    }

    /// <inheritdoc />
    public Task DeleteAsync(JobId jobId, CancellationToken cancellationToken = default)
    {
        DeleteDirectory(DirectoryFor(jobId));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteAsync(JobWorkspaceEntry entry, CancellationToken cancellationToken = default)
    {
        DeleteDirectory(entry.Location.Locator);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IReadOnlyList<JobWorkspaceEntry> Enumerate()
    {
        if (!Directory.Exists(_options.JobsRoot))
        {
            return [];
        }

        var entries = new List<JobWorkspaceEntry>();

        foreach (var directory in Directory.EnumerateDirectories(_options.JobsRoot))
        {
            var name = Path.GetFileName(directory);

            entries.Add(new JobWorkspaceEntry(
                JobId.TryParse(name, out var jobId) ? jobId : null,
                new AudioArtifactRef(directory),
                new DateTimeOffset(Directory.GetCreationTimeUtc(directory), TimeSpan.Zero)));
        }

        return entries;
    }

    /// <inheritdoc />
    public Task<int> PurgeTemporaryAsync(
        DateTimeOffset olderThanUtc, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_options.TempRoot))
        {
            return Task.FromResult(0);
        }

        var removed = 0;

        foreach (var path in Directory.EnumerateFiles(_options.TempRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var written = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            if (written > olderThanUtc)
            {
                continue;
            }

            try
            {
                File.Delete(path);
                removed++;
            }
            catch (IOException ex)
            {
                // Eine Datei, die gerade noch geschrieben wird, ist kein Grund,
                // den ganzen Aufräumlauf abzubrechen.
                _logger.LogDebug(ex, "Zwischendatei {Path} ließ sich nicht entfernen.", path);
            }
        }

        return Task.FromResult(removed);
    }

    /// <summary>
    /// Legt eine Datei für einen Upload an, der gerade entgegengenommen wird.
    /// </summary>
    public AudioArtifactRef CreateTemporaryUpload(string suffix)
    {
        Directory.CreateDirectory(_options.TempRoot);
        return new AudioArtifactRef(
            Path.Combine(_options.TempRoot, $"{Guid.NewGuid():n}{suffix}"));
    }

    private string DirectoryFor(JobId jobId) => Path.Combine(_options.JobsRoot, jobId.ToString());

    private void DeleteDirectory(string path)
    {
        // Sicherung gegen einen Programmierfehler weiter oben: Hier darf
        // ausschließlich unterhalb von jobs/ gelöscht werden. Ein versehentlich
        // durchgereichter Pfad auf voices/ würde dauerhaft gespeicherte Stimmen
        // vernichten.
        var full = Path.GetFullPath(path);
        var jobsRoot = Path.GetFullPath(_options.JobsRoot);

        if (!full.StartsWith(jobsRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            _logger.LogError(
                "Löschversuch außerhalb des Arbeitsverzeichnisses abgewiesen: {Path}", full);
            return;
        }

        if (Directory.Exists(full))
        {
            Directory.Delete(full, recursive: true);
        }
    }
}
