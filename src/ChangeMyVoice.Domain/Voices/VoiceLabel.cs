namespace ChangeMyVoice.Domain.Voices;

/// <summary>
/// Die vom Anwender vergebene Bezeichnung einer Referenzstimme. Sie ist das, was
/// in der Übersicht erscheint, und wird deshalb normalisiert, damit sich Einträge
/// nicht nur durch Leerraum oder Schreibweise unterscheiden.
/// </summary>
public sealed record VoiceLabel
{
    /// <summary>Kürzestmögliche Bezeichnung.</summary>
    public const int MinLength = 1;

    /// <summary>Längstmögliche Bezeichnung.</summary>
    public const int MaxLength = 100;

    private VoiceLabel(string value) => Value = value;

    /// <summary>Die normalisierte Bezeichnung.</summary>
    public string Value { get; }

    /// <summary>
    /// Schlüssel für den Eindeutigkeitsvergleich. Zwei Bezeichnungen, die sich nur
    /// in Groß-/Kleinschreibung unterscheiden, gelten als dieselbe.
    /// </summary>
    public string ComparisonKey => Value.ToUpperInvariant();

    /// <summary>
    /// Versucht, eine Bezeichnung zu bilden. Führender und folgender Leerraum wird
    /// entfernt; Steuerzeichen sind nicht erlaubt, weil sie sich in Listen und Logs
    /// nicht sinnvoll darstellen lassen.
    /// </summary>
    public static bool TryCreate(string? input, out VoiceLabel label, out string? error)
    {
        label = null!;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Die Bezeichnung darf nicht leer sein.";
            return false;
        }

        var trimmed = input.Trim();

        if (trimmed.Length < MinLength || trimmed.Length > MaxLength)
        {
            error = $"Die Bezeichnung muss zwischen {MinLength} und {MaxLength} Zeichen lang sein.";
            return false;
        }

        if (trimmed.Any(char.IsControl))
        {
            error = "Die Bezeichnung darf keine Steuerzeichen enthalten.";
            return false;
        }

        label = new VoiceLabel(trimmed);
        error = null;
        return true;
    }

    /// <summary>
    /// Bildet eine Bezeichnung und wirft, wenn die Eingabe ungültig ist. Nur für
    /// Stellen gedacht, an denen die Eingabe bereits geprüft wurde.
    /// </summary>
    public static VoiceLabel Create(string? input) =>
        TryCreate(input, out var label, out var error)
            ? label
            : throw new ArgumentException(error, nameof(input));

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>Vergleicht Bezeichnungen ohne Rücksicht auf die Schreibweise.</summary>
    public bool Equals(VoiceLabel? other) =>
        other is not null && string.Equals(ComparisonKey, other.ComparisonKey, StringComparison.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => ComparisonKey.GetHashCode(StringComparison.Ordinal);
}
