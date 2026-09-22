namespace ChangeMyVoice.Domain.Jobs;

/// <summary>Kennung eines Konvertierungsauftrags.</summary>
public readonly record struct JobId(Guid Value)
{
    /// <summary>Erzeugt eine neue, zufällige Kennung.</summary>
    public static JobId New() => new(Guid.NewGuid());

    /// <summary>Liest eine Kennung aus ihrer Textdarstellung.</summary>
    public static bool TryParse(string? text, out JobId id)
    {
        if (Guid.TryParse(text, out var guid))
        {
            id = new JobId(guid);
            return true;
        }

        id = default;
        return false;
    }

    /// <inheritdoc />
    public override string ToString() => Value.ToString("n");
}
