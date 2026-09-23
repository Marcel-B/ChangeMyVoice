namespace ChangeMyVoice.Domain.Audio;

/// <summary>
/// Schätzt, wie lange die Konvertierung eines Materials dauern wird.
/// </summary>
/// <remarks>
/// Die Rechenzeit wächst nicht gleichmäßig mit der Länge, sondern stärker: Die
/// überlappende Blockverarbeitung kostet bei langem Material überproportional.
/// Auf dem eingesetzten Apple-Silicon-Rechner gemessen:
/// <list type="table">
///   <item><description>15 s Material → gut 1 Minute (rund 5-fach)</description></item>
///   <item><description>3,4 min Material → über 30 Minuten (rund 9-fach)</description></item>
///   <item><description>5,3 min Material → 59 Minuten (rund 11-fach)</description></item>
/// </list>
/// Daraus ist ein quadratisches Modell abgeleitet, das den mittleren Messpunkt
/// gut trifft. Es ist eine grobe Orientierung, keine Zusage — die tatsächliche
/// Dauer hängt an der Auslastung des Rechners und an den gewählten
/// Diffusionsschritten.
/// </remarks>
public static class ConversionDurationEstimate
{
    // Beide Beiwerte stammen aus den oben genannten Messungen.
    private const double LinearerAnteil = 4.55;
    private const double QuadratischerAnteil = 0.0211;

    /// <summary>
    /// Die Anzahl Diffusionsschritte, auf die sich die Messungen beziehen.
    /// Weniger Schritte verkürzen die Rechenzeit annähernd proportional.
    /// </summary>
    public const int GemesseneDiffusionsschritte = 50;

    /// <summary>Schätzt die Rechenzeit für ein Material.</summary>
    /// <param name="materialLength">Die Länge der Quellaufnahme.</param>
    /// <param name="diffusionSteps">Die gewählten Diffusionsschritte.</param>
    /// <param name="f0Condition">
    /// Ob mit Tonhöhenkonditionierung gearbeitet wird. Der Sprachpfad arbeitet
    /// mit halber Abtastrate und ist dadurch spürbar schneller.
    /// </param>
    public static TimeSpan For(
        TimeSpan materialLength,
        int diffusionSteps = GemesseneDiffusionsschritte,
        bool f0Condition = true)
    {
        if (materialLength <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var sekunden = materialLength.TotalSeconds;
        var geschaetzt = (LinearerAnteil * sekunden)
            + (QuadratischerAnteil * sekunden * sekunden);

        // Die Schritte gehen annähernd linear in die Rechenzeit ein.
        geschaetzt *= (double)diffusionSteps / GemesseneDiffusionsschritte;

        // Ohne Tonhöhenkonditionierung läuft alles bei 22,05 statt 44,1 kHz.
        if (!f0Condition)
        {
            geschaetzt *= 0.6;
        }

        return TimeSpan.FromSeconds(Math.Round(geschaetzt));
    }

    /// <summary>
    /// Die längste Aufnahme, die innerhalb eines Zeitlimits fertig würde.
    /// </summary>
    /// <remarks>
    /// Die Umkehrung des Modells. Damit lässt sich ein zu langes Material schon
    /// bei der Annahme ablehnen, statt es eine Stunde rechnen zu lassen und dann
    /// mit einer Zeitüberschreitung abzubrechen — wobei nichts Verwertbares
    /// übrig bliebe, weil das Ergebnis erst ganz am Ende entsteht.
    /// </remarks>
    public static TimeSpan LongestMaterialWithin(
        TimeSpan limit,
        int diffusionSteps = GemesseneDiffusionsschritte,
        bool f0Condition = true)
    {
        var verfuegbar = limit.TotalSeconds;

        verfuegbar /= (double)diffusionSteps / GemesseneDiffusionsschritte;

        if (!f0Condition)
        {
            verfuegbar /= 0.6;
        }

        // Lösung von a*t + b*t² = verfuegbar
        var wurzel = Math.Sqrt(
            (LinearerAnteil * LinearerAnteil) + (4 * QuadratischerAnteil * verfuegbar));

        var sekunden = (-LinearerAnteil + wurzel) / (2 * QuadratischerAnteil);

        return TimeSpan.FromSeconds(Math.Max(0, Math.Floor(sekunden)));
    }
}
