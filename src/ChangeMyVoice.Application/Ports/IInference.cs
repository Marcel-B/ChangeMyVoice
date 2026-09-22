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
/// Hinter diesem Port liegt derzeit ein Prozessaufruf, der die Modelle bei jedem
/// Lauf neu lädt — mlx-vc startet intern selbst einen Unterprozess und hält keine
/// Modelle vor. Sollte das später durch einen dauerhaft laufenden Arbeiter
/// ersetzt werden, ändert sich nur der Adapter, nicht dieser Vertrag.
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
