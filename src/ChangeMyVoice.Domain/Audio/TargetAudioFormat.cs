namespace ChangeMyVoice.Domain.Audio;

/// <summary>
/// Das Format, in dem Audio an das Modell übergeben wird.
/// </summary>
/// <remarks>
/// Die Abtastrate ist nicht frei wählbar: Seed-VC lädt seine Eingaben mit
/// <c>librosa.load(pfad, sr=22050)</c> beziehungsweise <c>sr=44100</c> und mit
/// <c>mono=True</c>. Es rechnet also selbst um. Würde die API auf eine andere
/// Rate normalisieren, würde zweimal hintereinander resampelt — einmal hier,
/// einmal in librosa. Deshalb wird genau auf die Rate konvertiert, die der Lauf
/// ohnehin verwendet, und das Laden in Python wird zum reinen Durchreichen.
/// </remarks>
public sealed record TargetAudioFormat
{
    private TargetAudioFormat(int sampleRate) => SampleRate = sampleRate;

    /// <summary>Abtastrate ohne F0-Konditionierung (Sprachpfad).</summary>
    public const int SampleRateWithoutF0 = 22050;

    /// <summary>Abtastrate mit F0-Konditionierung (Gesangspfad).</summary>
    public const int SampleRateWithF0 = 44100;

    /// <summary>Die Abtastrate in Hertz.</summary>
    public int SampleRate { get; }

    /// <summary>Die Kanalzahl. Seed-VC arbeitet ausschließlich mono.</summary>
    public int Channels => 1;

    /// <summary>Der verwendete PCM-Codec.</summary>
    public string Codec => "pcm_s16le";

    /// <summary>
    /// Leitet das Zielformat daraus ab, ob der Lauf mit F0-Konditionierung
    /// arbeitet — also ob der Gesangs- oder der Sprachpfad verwendet wird.
    /// </summary>
    public static TargetAudioFormat For(bool f0Condition) =>
        new(f0Condition ? SampleRateWithF0 : SampleRateWithoutF0);

    /// <summary>
    /// Das Format, in dem Referenzstimmen dauerhaft abgelegt werden. Es ist die
    /// höhere der beiden Zielraten, damit eine einzige gespeicherte Datei beide
    /// Pfade bedient und höchstens einmal heruntergerechnet werden muss.
    /// </summary>
    public static TargetAudioFormat ReferenceMaster => new(SampleRateWithF0);
}
