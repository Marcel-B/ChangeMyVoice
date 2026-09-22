using ChangeMyVoice.Domain.Audio;

namespace ChangeMyVoice.Domain.Voices;

/// <summary>
/// Eine dauerhaft gespeicherte Zielstimme.
/// </summary>
/// <remarks>
/// Referenzstimmen werden vom Aufräumlauf niemals angefasst — sie sind das
/// einzige, was der Dienst über die Lebensdauer eines Jobs hinaus behält.
/// </remarks>
public sealed class ReferenceVoice
{
    private ReferenceVoice(
        VoiceId id,
        VoiceLabel label,
        DateTimeOffset createdAtUtc,
        AudioProperties storedAudio,
        AudioProperties originalAudio)
    {
        Id = id;
        Label = label;
        CreatedAtUtc = createdAtUtc;
        StoredAudio = storedAudio;
        OriginalAudio = originalAudio;
    }

    /// <summary>Die Kennung.</summary>
    public VoiceId Id { get; }

    /// <summary>Die vom Anwender vergebene Bezeichnung.</summary>
    public VoiceLabel Label { get; private set; }

    /// <summary>Zeitpunkt der Anlage.</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>
    /// Die Eigenschaften der tatsächlich abgelegten Datei: mono, PCM, 44,1 kHz.
    /// </summary>
    public AudioProperties StoredAudio { get; }

    /// <summary>
    /// Die Eigenschaften der ursprünglich hochgeladenen Datei. Sie werden
    /// mitgeführt, damit später nachvollziehbar bleibt, ob jemand etwa eine
    /// 8-kHz-Aufnahme geliefert hat — sonst sucht man ein schwaches Ergebnis
    /// im Modell statt im Ausgangsmaterial.
    /// </summary>
    public AudioProperties OriginalAudio { get; }

    /// <summary>Legt eine neue Referenzstimme an.</summary>
    public static ReferenceVoice Create(
        VoiceId id,
        VoiceLabel label,
        DateTimeOffset createdAtUtc,
        AudioProperties storedAudio,
        AudioProperties originalAudio) =>
        new(id, label, createdAtUtc, storedAudio, originalAudio);

    /// <summary>Stellt eine gespeicherte Referenzstimme wieder her.</summary>
    public static ReferenceVoice Rehydrate(
        VoiceId id,
        VoiceLabel label,
        DateTimeOffset createdAtUtc,
        AudioProperties storedAudio,
        AudioProperties originalAudio) =>
        new(id, label, createdAtUtc, storedAudio, originalAudio);

    /// <summary>Ändert die Bezeichnung.</summary>
    public void Rename(VoiceLabel label) => Label = label;
}
