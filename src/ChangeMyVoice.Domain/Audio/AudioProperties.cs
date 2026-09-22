namespace ChangeMyVoice.Domain.Audio;

/// <summary>
/// Die gemessenen Eigenschaften einer Audiodatei, wie sie eine Analyse
/// (ffprobe) liefert. Bewusst ein reiner Datensatz ohne Verhalten — geprüft
/// wird in <see cref="AudioValidationPolicy" />.
/// </summary>
/// <param name="Codec">Der Codec-Name, etwa <c>pcm_s16le</c>, <c>mp3</c> oder <c>aac</c>.</param>
/// <param name="Duration">Die Spieldauer.</param>
/// <param name="SampleRate">Die Abtastrate in Hertz.</param>
/// <param name="Channels">Die Anzahl der Kanäle.</param>
public sealed record AudioProperties(
    string Codec,
    TimeSpan Duration,
    int SampleRate,
    int Channels)
{
    /// <summary>Die Spieldauer in Sekunden, gerundet auf drei Nachkommastellen.</summary>
    public double DurationSeconds => Math.Round(Duration.TotalSeconds, 3);
}
