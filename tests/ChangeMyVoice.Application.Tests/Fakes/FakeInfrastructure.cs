using System.Collections.Concurrent;
using System.Threading.Channels;
using ChangeMyVoice.Application.Ports;
using ChangeMyVoice.Application.UseCases.Maintenance;
using ChangeMyVoice.Domain.Audio;
using ChangeMyVoice.Domain.Jobs;
using ChangeMyVoice.Domain.Voices;

namespace ChangeMyVoice.Application.Tests.Fakes;

/// <summary>
/// Ein Ablageort im Arbeitsspeicher. Hält fest, welche Orte belegt sind, damit
/// Tests prüfen können, dass Referenzstimmen erhalten bleiben.
/// </summary>
public sealed class FakeVoiceStorage : IVoiceStorage
{
    private readonly HashSet<VoiceId> _masters = [];

    public AudioArtifactRef ReserveMaster(VoiceId id)
    {
        _masters.Add(id);
        return new AudioArtifactRef($"voices/{id}/reference.wav");
    }

    public AudioArtifactRef GetMaster(VoiceId id) => new($"voices/{id}/reference.wav");

    public bool MasterExists(VoiceId id) => _masters.Contains(id);

    /// <summary>Was ein geöffneter Master enthält.</summary>
    public static readonly byte[] MasterContent = "RIFFmaster"u8.ToArray();

    public Task<Stream?> OpenMasterAsync(VoiceId id, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream?>(_masters.Contains(id) ? new MemoryStream(MasterContent) : null);

    public Task DeleteAsync(VoiceId id, CancellationToken cancellationToken = default)
    {
        _masters.Remove(id);
        return Task.CompletedTask;
    }

    /// <summary>Wie viele Stimmen noch abgelegt sind.</summary>
    public int StoredCount => _masters.Count;
}

/// <summary>Arbeitsverzeichnisse im Arbeitsspeicher.</summary>
public sealed class FakeJobWorkspaceStore : IJobWorkspaceStore
{
    private readonly ConcurrentDictionary<JobId, DateTimeOffset> _created = new();
    private readonly ConcurrentDictionary<JobId, byte[]> _outputs = new();
    private readonly List<JobWorkspaceEntry> _extraEntries = [];

    /// <summary>Zeitpunkt, mit dem neue Verzeichnisse vermerkt werden.</summary>
    public DateTimeOffset CreationTime { get; set; } = DateTimeOffset.UnixEpoch;

    /// <summary>Wie oft Zwischendateien aufgeräumt wurden.</summary>
    public int TemporaryPurgeCount { get; private set; }

    /// <summary>Die Orte, die gelöscht wurden.</summary>
    public List<string> DeletedLocations { get; } = [];

    public JobWorkspace Create(JobId jobId)
    {
        _created[jobId] = CreationTime;
        return Get(jobId);
    }

    public JobWorkspace Get(JobId jobId) => new(
        jobId,
        new AudioArtifactRef($"jobs/{jobId}/source.wav"),
        new AudioArtifactRef($"jobs/{jobId}/reference.wav"),
        new AudioArtifactRef($"jobs/{jobId}/raw-output.wav"),
        new AudioArtifactRef($"jobs/{jobId}/output.wav"));

    /// <summary>Legt ein Ergebnis für einen Auftrag ab.</summary>
    public void SetOutput(JobId jobId, byte[] content) => _outputs[jobId] = content;

    /// <summary>Trägt ein Verzeichnis ein, zu dem es keinen Auftrag gibt.</summary>
    public void AddOrphan(JobId? jobId, string location, DateTimeOffset createdAt) =>
        _extraEntries.Add(new JobWorkspaceEntry(jobId, new AudioArtifactRef(location), createdAt));

    public Task<Stream?> OpenOutputAsync(JobId jobId, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream?>(
            _outputs.TryGetValue(jobId, out var bytes) ? new MemoryStream(bytes) : null);

    public Task DeleteAsync(JobId jobId, CancellationToken cancellationToken = default)
    {
        _created.TryRemove(jobId, out _);
        _outputs.TryRemove(jobId, out _);
        _extraEntries.RemoveAll(e => e.JobId == jobId);
        DeletedLocations.Add($"jobs/{jobId}");
        return Task.CompletedTask;
    }

    public Task DeleteAsync(JobWorkspaceEntry entry, CancellationToken cancellationToken = default)
    {
        _extraEntries.Remove(entry);
        DeletedLocations.Add(entry.Location.Locator);
        return Task.CompletedTask;
    }

    public IReadOnlyList<JobWorkspaceEntry> Enumerate() =>
    [
        .. _created.Select(kv => new JobWorkspaceEntry(
            kv.Key, new AudioArtifactRef($"jobs/{kv.Key}"), kv.Value)),
        .. _extraEntries,
    ];

    public Task<int> PurgeTemporaryAsync(
        DateTimeOffset olderThanUtc, CancellationToken cancellationToken = default)
    {
        TemporaryPurgeCount++;
        return Task.FromResult(0);
    }
}

/// <summary>Liefert vorgegebene Audio-Eigenschaften.</summary>
public sealed class FakeAudioProbe : IAudioProbe
{
    private readonly Dictionary<string, AudioProperties?> _byLocator = [];

    /// <summary>Was zurückkommt, wenn zu einem Ort nichts hinterlegt ist.</summary>
    public AudioProperties? Default { get; set; } =
        new("pcm_s16le", TimeSpan.FromSeconds(10), 44100, 1);

    /// <summary>Hinterlegt Eigenschaften für einen bestimmten Ort.</summary>
    public void SetFor(AudioArtifactRef artifact, AudioProperties? properties) =>
        _byLocator[artifact.Locator] = properties;

    public Task<AudioProperties?> ProbeAsync(
        AudioArtifactRef artifact, CancellationToken cancellationToken = default) =>
        Task.FromResult(_byLocator.TryGetValue(artifact.Locator, out var p) ? p : Default);
}

/// <summary>Merkt sich, was normalisiert wurde, ohne etwas zu tun.</summary>
public sealed class FakeAudioNormalizer : IAudioNormalizer
{
    /// <summary>Die durchgeführten Umwandlungen.</summary>
    public List<(string Source, string Destination, int SampleRate, TimeSpan? MaxDuration)> Calls { get; } = [];

    /// <summary>Wird geworfen, wenn gesetzt.</summary>
    public Exception? ThrowOnNormalize { get; set; }

    public Task NormalizeAsync(
        AudioArtifactRef source,
        AudioArtifactRef destination,
        TargetAudioFormat format,
        TimeSpan? maxDuration = null,
        CancellationToken cancellationToken = default)
    {
        if (ThrowOnNormalize is not null)
        {
            throw ThrowOnNormalize;
        }

        Calls.Add((source.Locator, destination.Locator, format.SampleRate, maxDuration));
        return Task.CompletedTask;
    }
}

/// <summary>Eine Warteschlange im Arbeitsspeicher mit einstellbarer Kapazität.</summary>
public sealed class FakeJobQueue : IJobQueue
{
    private readonly Channel<JobId> _channel;

    public FakeJobQueue(int capacity = 64) =>
        _channel = Channel.CreateBounded<JobId>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
        });

    /// <summary>Alles, was eingereiht wurde.</summary>
    public List<JobId> Enqueued { get; } = [];

    /// <summary>Wenn gesetzt, wird jedes Einreihen abgelehnt.</summary>
    public bool RejectEverything { get; set; }

    public bool TryEnqueue(JobId jobId)
    {
        if (RejectEverything || !_channel.Writer.TryWrite(jobId))
        {
            return false;
        }

        Enqueued.Add(jobId);
        return true;
    }

    public IAsyncEnumerable<JobId> DequeueAllAsync(CancellationToken cancellationToken = default) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}

/// <summary>Eine feste Dienstkennung.</summary>
public sealed class FakeServiceInstance(Guid? id = null) : IServiceInstance
{
    public Guid InstanceId { get; } = id ?? Guid.NewGuid();
}

/// <summary>Eine Konvertierungsmaschine mit steuerbarem Ausgang.</summary>
public sealed class FakeConversionEngine : IVoiceConversionEngine
{
    /// <summary>Was zurückgegeben wird.</summary>
    public ConversionOutcome Outcome { get; set; } = ConversionOutcome.Success();

    /// <summary>Wird geworfen, wenn gesetzt.</summary>
    public Exception? ThrowOnConvert { get; set; }

    /// <summary>Die Prozesskennung, die gemeldet wird.</summary>
    public int? ReportProcessId { get; set; }

    /// <summary>Die entgegengenommenen Aufrufe.</summary>
    public List<ConversionRequest> Calls { get; } = [];

    public Task<ConversionOutcome> ConvertAsync(
        ConversionRequest request,
        Action<int>? onProcessStarted = null,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(request);

        if (ReportProcessId is { } pid)
        {
            onProcessStarted?.Invoke(pid);
        }

        return ThrowOnConvert is not null
            ? throw ThrowOnConvert
            : Task.FromResult(Outcome);
    }
}

/// <summary>Merkt sich, welche Prozesse beendet werden sollten.</summary>
public sealed class FakeOrphanProcessKiller : IOrphanProcessKiller
{
    /// <summary>Die angefragten Prozesskennungen.</summary>
    public List<int> Requested { get; } = [];

    /// <summary>Ob das Beenden Erfolg meldet.</summary>
    public bool Succeeds { get; set; } = true;

    public bool TryKill(int processId)
    {
        Requested.Add(processId);
        return Succeeds;
    }
}

/// <summary>Hält fest, welche Benachrichtigungen eingereiht wurden.</summary>
public sealed class FakeJobNotifier : IJobNotifier
{
    private readonly ConcurrentQueue<JobFinishedNotification> _sent = new();

    /// <summary>Die eingereihten Benachrichtigungen in ihrer Reihenfolge.</summary>
    public IReadOnlyList<JobFinishedNotification> Sent => _sent.ToArray();

    public void Enqueue(JobFinishedNotification notification) => _sent.Enqueue(notification);
}
