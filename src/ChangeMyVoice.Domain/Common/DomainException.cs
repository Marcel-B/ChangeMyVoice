namespace ChangeMyVoice.Domain.Common;

/// <summary>
/// Verletzung einer Domänenregel. Wird nur dort geworfen, wo ein Aufruf einen
/// Zustand herstellen würde, den es fachlich nicht geben darf — nicht für
/// Eingabevalidierung, die reguläre Fehlercodes zurückgibt.
/// </summary>
public class DomainException(string message) : Exception(message);

/// <summary>Ein unzulässiger Zustandsübergang eines Jobs.</summary>
public sealed class InvalidJobTransitionException(string message) : DomainException(message);
