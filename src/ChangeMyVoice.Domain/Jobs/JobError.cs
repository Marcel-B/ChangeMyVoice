namespace ChangeMyVoice.Domain.Jobs;

/// <summary>
/// Die nach außen sichtbare Beschreibung eines Fehlschlags.
/// </summary>
/// <remarks>
/// Enthält bewusst nur eine knappe Meldung. Tracebacks und interne Pfade gehören
/// ins Log, nicht in eine Antwort an aufrufende Systeme (init.md §24).
/// </remarks>
/// <param name="Code">Die Ursache.</param>
/// <param name="Message">Eine kurze, weitergebbare Begründung.</param>
public sealed record JobError(ConversionErrorCode Code, string Message);
