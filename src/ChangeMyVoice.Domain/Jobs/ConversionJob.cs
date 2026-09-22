using ChangeMyVoice.Domain.Common;
using ChangeMyVoice.Domain.Voices;

namespace ChangeMyVoice.Domain.Jobs;

/// <summary>
/// Ein Konvertierungsauftrag von der Annahme bis zum Ergebnis.
/// </summary>
/// <remarks>
/// Die Zustandsübergänge sind hier als Methoden gekapselt und nicht als frei
/// setzbare Eigenschaft. Ein Auftrag, der von <see cref="JobStatus.Completed" />
/// zurück auf <see cref="JobStatus.Running" /> springt, wäre ein Fehler, den
/// niemand bemerkt — deshalb wirft ein solcher Aufruf.
/// </remarks>
public sealed class ConversionJob
{
    private ConversionJob(
        JobId id,
        VoiceId voiceId,
        string voiceLabel,
        ConversionOptions options,
        DateTimeOffset createdAtUtc,
        Guid instanceId)
    {
        Id = id;
        VoiceId = voiceId;
        VoiceLabel = voiceLabel;
        Options = options;
        CreatedAtUtc = createdAtUtc;
        InstanceId = instanceId;
        Status = JobStatus.Queued;
    }

    private ConversionJob(
        JobId id,
        VoiceId voiceId,
        string voiceLabel,
        ConversionOptions options,
        JobStatus status,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset? finishedAtUtc,
        DateTimeOffset? downloadedAtUtc,
        JobError? error,
        long? outputSizeBytes,
        string? outputSha256,
        Guid instanceId,
        int? inferenceProcessId,
        bool artifactsPurged)
    {
        Id = id;
        VoiceId = voiceId;
        VoiceLabel = voiceLabel;
        Options = options;
        Status = status;
        CreatedAtUtc = createdAtUtc;
        StartedAtUtc = startedAtUtc;
        FinishedAtUtc = finishedAtUtc;
        DownloadedAtUtc = downloadedAtUtc;
        Error = error;
        OutputSizeBytes = outputSizeBytes;
        OutputSha256 = outputSha256;
        InstanceId = instanceId;
        InferenceProcessId = inferenceProcessId;
        ArtifactsPurged = artifactsPurged;
    }

    /// <summary>Die Kennung.</summary>
    public JobId Id { get; }

    /// <summary>Die verwendete Referenzstimme.</summary>
    public VoiceId VoiceId { get; }

    /// <summary>
    /// Die Bezeichnung der Stimme zum Zeitpunkt der Annahme. Als Kopie geführt,
    /// damit ein Job auch dann noch aussagekräftig bleibt, wenn die Stimme
    /// inzwischen gelöscht oder umbenannt wurde.
    /// </summary>
    public string VoiceLabel { get; }

    /// <summary>Die gewählten Stellschrauben.</summary>
    public ConversionOptions Options { get; }

    /// <summary>Der aktuelle Zustand.</summary>
    public JobStatus Status { get; private set; }

    /// <summary>Zeitpunkt der Annahme.</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>Zeitpunkt, zu dem die Berechnung begann.</summary>
    public DateTimeOffset? StartedAtUtc { get; private set; }

    /// <summary>Zeitpunkt, zu dem der Auftrag endgültig endete.</summary>
    public DateTimeOffset? FinishedAtUtc { get; private set; }

    /// <summary>Zeitpunkt, zu dem das Ergebnis abgeholt wurde.</summary>
    public DateTimeOffset? DownloadedAtUtc { get; private set; }

    /// <summary>Die Fehlerursache, falls fehlgeschlagen.</summary>
    public JobError? Error { get; private set; }

    /// <summary>Größe der Ergebnisdatei.</summary>
    public long? OutputSizeBytes { get; private set; }

    /// <summary>Prüfsumme der Ergebnisdatei, damit der Abruf verifizierbar ist.</summary>
    public string? OutputSha256 { get; private set; }

    /// <summary>
    /// Kennung des Dienstlaufs, der diesen Auftrag zuletzt angefasst hat. Nach
    /// einem Neustart lässt sich daran erkennen, welche laufenden Aufträge aus
    /// einem früheren, abgestürzten Prozess stammen.
    /// </summary>
    public Guid InstanceId { get; private set; }

    /// <summary>
    /// Die Prozesskennung der laufenden Inferenz, damit ein nach einem Absturz
    /// zurückgebliebener Python-Prozess gezielt beendet werden kann.
    /// </summary>
    public int? InferenceProcessId { get; private set; }

    /// <summary>
    /// Ob die Dateien bereits aufgeräumt wurden. Der Auftrag bleibt danach
    /// abfragbar, sein Ergebnis aber nicht mehr abrufbar.
    /// </summary>
    public bool ArtifactsPurged { get; private set; }

    /// <summary>Ob der Auftrag einen Endzustand erreicht hat.</summary>
    public bool IsTerminal => Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled;

    /// <summary>Ob das Ergebnis heruntergeladen werden kann.</summary>
    public bool IsResultAvailable => Status == JobStatus.Completed && !ArtifactsPurged;

    /// <summary>Nimmt einen neuen Auftrag an.</summary>
    public static ConversionJob Create(
        JobId id,
        VoiceId voiceId,
        string voiceLabel,
        ConversionOptions options,
        DateTimeOffset createdAtUtc,
        Guid instanceId) =>
        new(id, voiceId, voiceLabel, options, createdAtUtc, instanceId);

    /// <summary>Stellt einen gespeicherten Auftrag wieder her.</summary>
    public static ConversionJob Rehydrate(
        JobId id,
        VoiceId voiceId,
        string voiceLabel,
        ConversionOptions options,
        JobStatus status,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset? finishedAtUtc,
        DateTimeOffset? downloadedAtUtc,
        JobError? error,
        long? outputSizeBytes,
        string? outputSha256,
        Guid instanceId,
        int? inferenceProcessId,
        bool artifactsPurged) =>
        new(id, voiceId, voiceLabel, options, status, createdAtUtc, startedAtUtc, finishedAtUtc,
            downloadedAtUtc, error, outputSizeBytes, outputSha256, instanceId, inferenceProcessId,
            artifactsPurged);

    /// <summary>Beginnt die Berechnung.</summary>
    public void Start(DateTimeOffset atUtc, Guid instanceId)
    {
        EnsureStatus(JobStatus.Queued, "gestartet");
        Status = JobStatus.Running;
        StartedAtUtc = atUtc;
        InstanceId = instanceId;
    }

    /// <summary>Hält fest, welcher Prozess die Inferenz ausführt.</summary>
    public void AttachInferenceProcess(int processId)
    {
        EnsureStatus(JobStatus.Running, "mit einem Prozess verknüpft");
        InferenceProcessId = processId;
    }

    /// <summary>Schließt den Auftrag erfolgreich ab.</summary>
    public void Complete(DateTimeOffset atUtc, long outputSizeBytes, string outputSha256)
    {
        EnsureStatus(JobStatus.Running, "abgeschlossen");
        Status = JobStatus.Completed;
        FinishedAtUtc = atUtc;
        OutputSizeBytes = outputSizeBytes;
        OutputSha256 = outputSha256;
        InferenceProcessId = null;
    }

    /// <summary>Lässt den Auftrag fehlschlagen.</summary>
    public void Fail(DateTimeOffset atUtc, JobError error)
    {
        if (IsTerminal)
        {
            throw new InvalidJobTransitionException(
                $"Ein Auftrag im Zustand '{Status}' kann nicht mehr fehlschlagen.");
        }

        Status = JobStatus.Failed;
        FinishedAtUtc = atUtc;
        Error = error;
        InferenceProcessId = null;
    }

    /// <summary>Bricht den Auftrag ab.</summary>
    public void Cancel(DateTimeOffset atUtc)
    {
        if (IsTerminal)
        {
            throw new InvalidJobTransitionException(
                $"Ein Auftrag im Zustand '{Status}' kann nicht mehr abgebrochen werden.");
        }

        Status = JobStatus.Cancelled;
        FinishedAtUtc = atUtc;
        InferenceProcessId = null;
    }

    /// <summary>Vermerkt, dass das Ergebnis abgeholt wurde.</summary>
    public void MarkDownloaded(DateTimeOffset atUtc)
    {
        EnsureStatus(JobStatus.Completed, "als heruntergeladen vermerkt");

        // Der erste Abruf zählt: spätere Wiederholungen sollen die Aufbewahrungs-
        // frist nicht immer weiter nach hinten schieben.
        DownloadedAtUtc ??= atUtc;
    }

    /// <summary>Vermerkt, dass die Dateien entfernt wurden.</summary>
    public void MarkArtifactsPurged() => ArtifactsPurged = true;

    private void EnsureStatus(JobStatus expected, string action)
    {
        if (Status != expected)
        {
            throw new InvalidJobTransitionException(
                $"Ein Auftrag im Zustand '{Status}' kann nicht {action} werden; "
                + $"erwartet wurde '{expected}'.");
        }
    }
}
