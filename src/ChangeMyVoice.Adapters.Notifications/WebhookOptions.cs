using System.ComponentModel.DataAnnotations;

namespace ChangeMyVoice.Adapters.Notifications;

/// <summary>Einstellungen für die Benachrichtigung per Webhook.</summary>
public sealed class WebhookOptions
{
    /// <summary>Der Abschnitt in der Konfiguration.</summary>
    public const string SectionName = "Webhooks";

    /// <summary>
    /// Die Rechner, die als Ziel angegeben werden dürfen. Leer heißt: jeder.
    /// </summary>
    /// <remarks>
    /// Der Dienst ruft die Adresse von sich aus auf, aus dem Netz heraus, in dem
    /// er läuft. Aufrufer sind zwar angemeldet, wer den Kreis der Ziele trotzdem
    /// begrenzen will, trägt sie hier ein.
    /// </remarks>
    public List<string> AllowedHosts { get; set; } = [];

    /// <summary>Wie lange ein einzelner Aufruf dauern darf.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Wie oft höchstens versucht wird zuzustellen.</summary>
    [Range(1, 10)]
    public int MaxAttempts { get; set; } = 4;

    /// <summary>
    /// Die Wartezeit vor dem zweiten Versuch; jeder weitere wartet doppelt so lange.
    /// </summary>
    /// <remarks>
    /// Bei der Voreinstellung sind das 30 Sekunden, eine und zwei Minuten: genug,
    /// um einen Neustart des Empfängers zu überbrücken.
    /// </remarks>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Wie viele Nachrichten höchstens auf ihre Zustellung warten.</summary>
    [Range(1, 10000)]
    public int QueueCapacity { get; set; } = 256;
}
