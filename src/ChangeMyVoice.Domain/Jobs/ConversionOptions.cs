using ChangeMyVoice.Domain.Audio;

namespace ChangeMyVoice.Domain.Jobs;

/// <summary>
/// Die Stellschrauben eines Konvertierungslaufs. Die Voreinstellungen entsprechen
/// denen, die das Seed-VC-Backend selbst verwendet.
/// </summary>
public sealed record ConversionOptions
{
    /// <summary>Anzahl der Diffusionsschritte. Mehr Schritte kosten Zeit.</summary>
    public int DiffusionSteps { get; private init; } = 50;

    /// <summary>Gewichtung der klassifikatorfreien Führung.</summary>
    public double InferenceCfgRate { get; private init; } = 0.7;

    /// <summary>Streckung oder Stauchung der Länge.</summary>
    public double LengthAdjust { get; private init; } = 1.0;

    /// <summary>
    /// Ob mit F0-Konditionierung gearbeitet wird.
    /// </summary>
    /// <remarks>
    /// Das ist der Gesangspfad: 44,1 kHz mit Tonhöhenkonditionierung. Er ist
    /// die Voreinstellung, weil dieser Dienst für Gesang gedacht ist und ohne
    /// ihn die Tonhöhenführung schwächer bleibt — das Ergebnis klingt dann eher
    /// nach Sprachumwandlung. Er kostet spürbar mehr Zeit: bei rund 15 Sekunden
    /// Material etwa 60 statt 40 Sekunden. Wer den schnelleren Sprachpfad
    /// braucht, schaltet ihn je Auftrag mit <c>f0Condition=false</c> ab.
    /// </remarks>
    public bool F0Condition { get; private init; } = true;

    /// <summary>Ob der Diffusionsschritt in halber Genauigkeit läuft.</summary>
    public bool Fp16 { get; private init; } = true;

    /// <summary>
    /// Die Abtastrate der ausgelieferten Datei.
    /// </summary>
    /// <remarks>
    /// Voreingestellt sind 48 kHz, weil Projekte in der Musikproduktion meist
    /// damit arbeiten. Das Modell erzeugt 44,1 kHz; die Umrechnung am Ende
    /// erspart diesen Schritt von Hand. Wer die Datei unverändert so haben will,
    /// wie das Modell sie erzeugt hat, setzt den Wert auf 44100.
    /// </remarks>
    public int OutputSampleRate { get; private init; } = TargetAudioFormat.SampleRateForProduction;

    /// <summary>Das Format, in dem das Ergebnis ausgeliefert wird.</summary>
    public TargetAudioFormat DeliveryFormat => TargetAudioFormat.ForDelivery(OutputSampleRate);

    /// <summary>
    /// Ob nach dem Modelldurchlauf noch umgerechnet werden muss.
    /// </summary>
    public bool RequiresOutputConversion => OutputSampleRate != TargetFormat.SampleRate;

    /// <summary>Das daraus folgende Zielformat für die Eingabedateien.</summary>
    public TargetAudioFormat TargetFormat => TargetAudioFormat.For(F0Condition);

    /// <summary>Die Voreinstellung.</summary>
    public static ConversionOptions Default { get; } = new();

    /// <summary>
    /// Baut Optionen aus Anwendereingaben und meldet den ersten Verstoß, statt zu
    /// werfen — die Werte kommen von außen und dürfen den Dienst nicht stören.
    /// </summary>
    public static bool TryCreate(
        int? diffusionSteps,
        double? inferenceCfgRate,
        double? lengthAdjust,
        bool? f0Condition,
        bool? fp16,
        int? outputSampleRate,
        out ConversionOptions options,
        out string? error)
    {
        options = Default;
        error = null;

        var steps = diffusionSteps ?? Default.DiffusionSteps;
        if (steps is < 1 or > 200)
        {
            error = "diffusionSteps muss zwischen 1 und 200 liegen.";
            return false;
        }

        var cfg = inferenceCfgRate ?? Default.InferenceCfgRate;
        if (cfg is < 0 or > 1)
        {
            error = "inferenceCfgRate muss zwischen 0 und 1 liegen.";
            return false;
        }

        var length = lengthAdjust ?? Default.LengthAdjust;
        if (length is < 0.5 or > 2.0)
        {
            error = "lengthAdjust muss zwischen 0,5 und 2,0 liegen.";
            return false;
        }

        var ausgaberate = outputSampleRate ?? Default.OutputSampleRate;

        // Die Grenzen decken ab, was Audioprogramme üblicherweise verarbeiten;
        // exotische Raten brächten hier keinen Gewinn.
        if (ausgaberate is < 8000 or > 192000)
        {
            error = "outputSampleRate muss zwischen 8000 und 192000 Hz liegen.";
            return false;
        }

        options = new ConversionOptions
        {
            OutputSampleRate = ausgaberate,
            DiffusionSteps = steps,
            InferenceCfgRate = cfg,
            LengthAdjust = length,
            F0Condition = f0Condition ?? Default.F0Condition,
            Fp16 = fp16 ?? Default.Fp16,
        };

        return true;
    }
}
