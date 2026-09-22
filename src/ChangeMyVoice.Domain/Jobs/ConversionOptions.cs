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

        options = new ConversionOptions
        {
            DiffusionSteps = steps,
            InferenceCfgRate = cfg,
            LengthAdjust = length,
            F0Condition = f0Condition ?? Default.F0Condition,
            Fp16 = fp16 ?? Default.Fp16,
        };

        return true;
    }
}
