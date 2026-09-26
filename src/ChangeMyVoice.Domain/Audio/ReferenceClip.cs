namespace ChangeMyVoice.Domain.Audio;

/// <summary>
/// Der Ausschnitt einer Referenzaufnahme, der als Stimme dienen soll.
/// </summary>
/// <remarks>
/// Seed-VC verwendet von der Referenz nur die ersten 25 Sekunden. Ohne Angabe
/// ist das der Anfang der Datei, oft ein Intro oder Atmen; mit Start und Ende
/// lassen sich die besten 25 Sekunden wählen, ohne die Datei vorher selbst zu
/// schneiden.
/// </remarks>
/// <param name="Start">Ab wo die Aufnahme verwendet wird.</param>
/// <param name="End">Bis wohin, oder <c>null</c> für bis zum Ende.</param>
public sealed record ReferenceClip(TimeSpan Start, TimeSpan? End)
{
    /// <summary>Die ganze Aufnahme, wie ohne Angabe.</summary>
    public static ReferenceClip Whole { get; } = new(TimeSpan.Zero, null);

    /// <summary>Ob wirklich ein Ausschnitt gewählt ist.</summary>
    public bool IsWhole => Start == TimeSpan.Zero && End is null;

    /// <summary>Prüft die Angaben in Sekunden.</summary>
    /// <param name="startSeconds">Der Beginn, leer für den Anfang.</param>
    /// <param name="endSeconds">Das Ende, leer für das Ende der Aufnahme.</param>
    /// <param name="clip">Der Ausschnitt, wenn die Angaben stimmen.</param>
    /// <param name="error">Die Begründung, wenn nicht.</param>
    public static bool TryCreate(
        double? startSeconds, double? endSeconds, out ReferenceClip clip, out string? error)
    {
        clip = Whole;
        error = null;

        if (startSeconds is { } start && (!double.IsFinite(start) || start < 0))
        {
            error = "'startSeconds' muss eine Zahl ab 0 sein.";
            return false;
        }

        if (endSeconds is { } end && (!double.IsFinite(end) || end <= (startSeconds ?? 0)))
        {
            error = "'endSeconds' muss hinter 'startSeconds' liegen.";
            return false;
        }

        clip = new ReferenceClip(
            TimeSpan.FromSeconds(startSeconds ?? 0),
            endSeconds is { } e ? TimeSpan.FromSeconds(e) : null);
        return true;
    }

    /// <summary>Wie lang der Ausschnitt in einer Aufnahme dieser Länge ist.</summary>
    public TimeSpan LengthIn(TimeSpan duration)
    {
        var end = End is { } e && e < duration ? e : duration;
        return end > Start ? end - Start : TimeSpan.Zero;
    }
}
