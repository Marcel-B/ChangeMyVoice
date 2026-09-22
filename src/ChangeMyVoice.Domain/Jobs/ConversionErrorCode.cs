namespace ChangeMyVoice.Domain.Jobs;

/// <summary>
/// Fehlerursachen einer Konvertierung. Die Werte entsprechen wörtlich der Liste
/// aus init.md §24 und werden als SCREAMING_SNAKE_CASE serialisiert, damit sie
/// für aufrufende Systeme stabil sind.
/// </summary>
public enum ConversionErrorCode
{
    /// <summary>Datei nicht lesbar oder enthält kein verwertbares Audio.</summary>
    InvalidAudio,

    /// <summary>Container oder Codec wird nicht unterstützt.</summary>
    UnsupportedFormat,

    /// <summary>Referenzaufnahme ist zu kurz für ein brauchbares Timbre.</summary>
    ReferenceTooShort,

    /// <summary>Das Modell konnte nicht geladen werden.</summary>
    ModelLoadFailed,

    /// <summary>Ein benötigtes Checkpoint konnte nicht bezogen werden.</summary>
    ModelDownloadFailed,

    /// <summary>Die Inferenz selbst ist fehlgeschlagen.</summary>
    InferenceFailed,

    /// <summary>Der Arbeitsspeicher reichte nicht aus.</summary>
    OutOfMemory,

    /// <summary>Fehler im Metal-/MPS-Backend.</summary>
    MpsError,

    /// <summary>Der Lauf endete ohne verwertbare Ausgabedatei.</summary>
    OutputNotCreated,

    /// <summary>Die Inferenz überschritt das erlaubte Zeitfenster.</summary>
    Timeout,

    /// <summary>
    /// Der Job wurde durch einen Neustart oder Absturz des Dienstes unterbrochen.
    /// Ergänzung zu init.md §24, weil ein abgebrochener Lauf sonst nicht von einem
    /// fachlich fehlgeschlagenen unterscheidbar wäre.
    /// </summary>
    Interrupted,
}
