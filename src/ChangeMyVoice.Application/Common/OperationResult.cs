using ChangeMyVoice.Domain.Jobs;

namespace ChangeMyVoice.Application.Common;

/// <summary>
/// Fehler, die auf Ebene der Anwendungsfälle entstehen.
/// </summary>
/// <remarks>
/// Bewusst getrennt von <see cref="ConversionErrorCode" />: Jene Liste stammt
/// wörtlich aus init.md §24 und beschreibt, warum eine Konvertierung scheitert.
/// Ein unbekannter Bezeichner oder eine belegte Bezeichnung hat damit nichts zu
/// tun und würde die Liste nur verwässern.
/// </remarks>
public enum OperationErrorCode
{
    /// <summary>Die angefragte Referenzstimme existiert nicht.</summary>
    VoiceNotFound,

    /// <summary>Der angefragte Auftrag existiert nicht.</summary>
    JobNotFound,

    /// <summary>Die Bezeichnung ist bereits vergeben.</summary>
    DuplicateVoiceLabel,

    /// <summary>Die Stimme wird noch von einem Auftrag verwendet.</summary>
    VoiceInUse,

    /// <summary>Der Auftrag ist noch nicht fertig.</summary>
    ResultNotReady,

    /// <summary>Das Ergebnis wurde bereits aufgeräumt.</summary>
    ResultGone,

    /// <summary>Die Warteschlange ist voll.</summary>
    QueueFull,

    /// <summary>Die Eingabe ist fachlich unbrauchbar.</summary>
    InvalidInput,

    /// <summary>Die hochgeladene Audiodatei wurde abgelehnt.</summary>
    AudioRejected,
}

/// <summary>Beschreibt, warum ein Anwendungsfall nicht ausgeführt werden konnte.</summary>
/// <param name="Code">Die Art des Fehlers.</param>
/// <param name="Message">Eine weitergebbare Begründung.</param>
/// <param name="AudioErrorCode">
/// Bei abgelehnten Audiodateien der genaue Grund nach init.md §24, damit der
/// Aufrufer nicht raten muss, was an der Datei nicht stimmte.
/// </param>
public sealed record OperationError(
    OperationErrorCode Code,
    string Message,
    ConversionErrorCode? AudioErrorCode = null);

/// <summary>Das Ergebnis eines Anwendungsfalls, der einen Wert liefert.</summary>
public readonly record struct Result<T>
{
    private Result(T? value, OperationError? error)
    {
        Value = value;
        Error = error;
    }

    /// <summary>Der Rückgabewert im Erfolgsfall.</summary>
    public T? Value { get; }

    /// <summary>Der Fehler im Fehlerfall.</summary>
    public OperationError? Error { get; }

    /// <summary>Ob der Anwendungsfall erfolgreich war.</summary>
    public bool IsSuccess => Error is null;

    /// <summary>Erzeugt ein erfolgreiches Ergebnis.</summary>
    public static Result<T> Success(T value) => new(value, null);

    /// <summary>Erzeugt ein fehlerhaftes Ergebnis.</summary>
    public static Result<T> Failure(OperationError error) => new(default, error);

    /// <summary>Erzeugt ein fehlerhaftes Ergebnis aus Code und Meldung.</summary>
    public static Result<T> Failure(OperationErrorCode code, string message) =>
        new(default, new OperationError(code, message));
}

/// <summary>Das Ergebnis eines Anwendungsfalls ohne Rückgabewert.</summary>
public readonly record struct Result
{
    private Result(OperationError? error) => Error = error;

    /// <summary>Der Fehler im Fehlerfall.</summary>
    public OperationError? Error { get; }

    /// <summary>Ob der Anwendungsfall erfolgreich war.</summary>
    public bool IsSuccess => Error is null;

    /// <summary>Ein erfolgreiches Ergebnis.</summary>
    public static Result Success() => new(null);

    /// <summary>Erzeugt ein fehlerhaftes Ergebnis.</summary>
    public static Result Failure(OperationError error) => new(error);

    /// <summary>Erzeugt ein fehlerhaftes Ergebnis aus Code und Meldung.</summary>
    public static Result Failure(OperationErrorCode code, string message) =>
        new(new OperationError(code, message));
}
