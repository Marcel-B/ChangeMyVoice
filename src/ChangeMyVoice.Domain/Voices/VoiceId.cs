namespace ChangeMyVoice.Domain.Voices;

/// <summary>Kennung einer gespeicherten Referenzstimme.</summary>
public readonly record struct VoiceId(Guid Value)
{
    /// <summary>Erzeugt eine neue, zufällige Kennung.</summary>
    public static VoiceId New() => new(Guid.NewGuid());

    /// <summary>Liest eine Kennung aus ihrer Textdarstellung.</summary>
    public static bool TryParse(string? text, out VoiceId id)
    {
        if (Guid.TryParse(text, out var guid))
        {
            id = new VoiceId(guid);
            return true;
        }

        id = default;
        return false;
    }

    /// <inheritdoc />
    public override string ToString() => Value.ToString("n");
}
