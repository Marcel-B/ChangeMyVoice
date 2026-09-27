using ChangeMyVoice.Domain.Jobs;

namespace ChangeMyVoice.Application.Ports;

/// <summary>Der Auftrag an die Konvertierungsmaschine.</summary>
/// <param name="Source">Die normalisierte Quellaufnahme.</param>
/// <param name="Reference">Die normalisierte Referenzaufnahme.</param>
/// <param name="Output">Der gewünschte Ort der Ausgabe.</param>
/// <param name="Options">Die Stellschrauben des Laufs.</param>
public sealed record ConversionRequest(
    AudioArtifactRef Source,
    AudioArtifactRef Reference,
    AudioArtifactRef Output,
    ConversionOptions Options);

/// <summary>Das Ergebnis eines Konvertierungslaufs.</summary>
/// <param name="Error">Die Ursache, falls der Lauf scheiterte, sonst <c>null</c>.</param>
/// <param name="ModelLoadDuration">Wie lange das Laden der Modelle gedauert hat.</param>
/// <param name="InferenceDuration">Wie lange die eigentliche Berechnung gedauert hat.</param>
public sealed record ConversionOutcome(
    JobError? Error,
    TimeSpan? ModelLoadDuration = null,
    TimeSpan? InferenceDuration = null)
{
    /// <summary>Ob der Lauf erfolgreich war.</summary>
    public bool IsSuccess => Error is null;

    /// <summary>Ein erfolgreicher Lauf.</summary>
    public static ConversionOutcome Success(TimeSpan? modelLoad = null, TimeSpan? inference = null) =>
        new(null, modelLoad, inference);

    /// <summary>Ein gescheiterter Lauf.</summary>
    public static ConversionOutcome Failure(ConversionErrorCode code, string message) =>
        new(new JobError(code, message));
}

/// <summary>Führt die eigentliche Stimmkonvertierung aus.</summary>
/// <remarks>
/// Hinter diesem Port liegt ein Python-Prozess, der das Modell zwischen den
/// Läufen geladen halten kann (<see cref="IInferenceModelHost" />); ob er das
/// tut oder für jeden Lauf neu startet, ist Sache des Adapters, nicht dieses
/// Vertrags.
/// </remarks>
public interface IVoiceConversionEngine
{
    /// <summary>Führt einen Lauf aus.</summary>
    /// <param name="request">Die Eingaben.</param>
    /// <param name="onProcessStarted">
    /// Wird mit der Prozesskennung aufgerufen, sobald der Lauf gestartet ist,
    /// damit ein zurückgebliebener Prozess später gezielt beendet werden kann.
    /// </param>
    /// <param name="cancellationToken">Abbruchsteuerung, auch für den Zeitablauf.</param>
    Task<ConversionOutcome> ConvertAsync(
        ConversionRequest request,
        Action<int>? onProcessStarted = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Das Ergebnis einer Umgebungsprüfung.</summary>
/// <param name="Name">Der Name der Prüfung.</param>
/// <param name="IsHealthy">Ob sie bestanden wurde.</param>
/// <param name="Detail">Eine knappe Erläuterung.</param>
public sealed record ReadinessCheck(string Name, bool IsHealthy, string? Detail = null);

/// <summary>Prüft, ob Modell und Werkzeuge einsatzbereit sind.</summary>
public interface IInferenceEnvironmentProbe
{
    /// <summary>Führt alle Prüfungen aus.</summary>
    Task<IReadOnlyList<ReadinessCheck>> CheckAsync(CancellationToken cancellationToken = default);
}

/// <summary>Nimmt Aufträge zur späteren Bearbeitung entgegen.</summary>
public interface IJobQueue
{
    /// <summary>
    /// Reiht einen Auftrag ein. Liefert <c>false</c>, wenn die Warteschlange voll
    /// ist — der Aufrufer soll dann ablehnen statt zu blockieren.
    /// </summary>
    bool TryEnqueue(JobId jobId);

    /// <summary>Liefert die eingereihten Aufträge in der Reihenfolge ihrer Annahme.</summary>
    IAsyncEnumerable<JobId> DequeueAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Kennung des laufenden Dienstprozesses.
/// </summary>
/// <remarks>
/// Wird bei jedem Auftrag mitgeschrieben. Findet der Dienst beim Start einen
/// laufenden Auftrag mit fremder Kennung, stammt dieser aus einem abgestürzten
/// Vorgängerprozess und kann gefahrlos als unterbrochen gelten.
/// </remarks>
public interface IServiceInstance
{
    /// <summary>Die Kennung dieses Laufs.</summary>
    Guid InstanceId { get; }
}

/// <summary>Ob und wie lange das Modell zwischen den Aufträgen geladen bleibt.</summary>
/// <param name="IsLoaded">Ob das Modell gerade im Speicher liegt.</param>
/// <param name="IsBusy">Ob gerade ein Lauf stattfindet.</param>
/// <param name="KeepLoadedFor">
/// Wie lange das Modell nach dem letzten Lauf geladen bleibt; <see cref="TimeSpan.Zero" />,
/// wenn jeder Lauf es neu lädt.
/// </param>
/// <param name="LoadedSinceUtc">Seit wann es geladen ist.</param>
/// <param name="UnloadAtUtc">Wann es ohne weiteren Auftrag entladen wird.</param>
public sealed record InferenceModelState(
    bool IsLoaded,
    bool IsBusy,
    TimeSpan KeepLoadedFor,
    DateTimeOffset? LoadedSinceUtc = null,
    DateTimeOffset? UnloadAtUtc = null);

/// <summary>Was die Bitte, das Modell zu entladen, bewirkt hat.</summary>
public enum ModelReleaseResult
{
    /// <summary>Es war nichts geladen.</summary>
    NotLoaded,

    /// <summary>Das Modell wurde entladen und sein Speicher freigegeben.</summary>
    Released,

    /// <summary>Ein Lauf ist im Gange; entladen wird danach von selbst.</summary>
    Busy,
}

/// <summary>
/// Hält das Modell zwischen den Aufträgen im Speicher und gibt es wieder frei.
/// </summary>
/// <remarks>
/// Das Modell belegt mehrere Gigabyte gemeinsamen Speichers. Andere Modelle auf
/// demselben Rechner (etwa YuE2 oder ein Textmodell) sollen es freigeben lassen
/// können, bevor sie selbst laden, statt auf den Leerlauf zu warten.
/// </remarks>
public interface IInferenceModelHost
{
    /// <summary>Der aktuelle Zustand.</summary>
    InferenceModelState GetState();

    /// <summary>Entlädt das Modell, sofern kein Lauf im Gange ist.</summary>
    Task<ModelReleaseResult> ReleaseAsync(CancellationToken cancellationToken = default);
}
