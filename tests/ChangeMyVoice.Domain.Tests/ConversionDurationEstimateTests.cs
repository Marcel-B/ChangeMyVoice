using ChangeMyVoice.Domain.Audio;
using ChangeMyVoice.Domain.Jobs;
using Shouldly;

namespace ChangeMyVoice.Domain.Tests;

public class ConversionDurationEstimateTests
{
    /// <summary>
    /// Die tatsächlich auf dem Zielrechner gemessenen Läufe. Weicht die
    /// Schätzung hier zu weit ab, taugt sie nicht als Orientierung für eine
    /// Oberfläche — und genau dafür ist sie gedacht.
    /// </summary>
    [Theory]
    [InlineData(14.9, 72.5)]      // zwei kurze Läufe, Mittel aus 64 s und 81 s
    [InlineData(205.6, 1800)]     // lief über 30 Minuten und wurde abgebrochen
    [InlineData(316.6, 3557)]     // der vollständige Lauf über Nacht
    public void Die_Schaetzung_trifft_die_gemessenen_Laeufe(double material, double gemessen)
    {
        var geschaetzt = ConversionDurationEstimate.For(TimeSpan.FromSeconds(material));

        // 25 Prozent Spielraum: Es ist eine Orientierung, keine Zusage.
        geschaetzt.TotalSeconds.ShouldBeInRange(gemessen * 0.75, gemessen * 1.25);
    }

    [Fact]
    public void Die_Rechenzeit_waechst_staerker_als_die_Laenge()
    {
        // Genau das war der Trugschluss, der zum ersten Zeitablauf gefuehrt hat:
        // Aus kurzen Laeufen hochgerechnet wirkt es linear, ist es aber nicht.
        var kurz = ConversionDurationEstimate.For(TimeSpan.FromMinutes(1));
        var lang = ConversionDurationEstimate.For(TimeSpan.FromMinutes(5));

        var faktorKurz = kurz.TotalSeconds / 60;
        var faktorLang = lang.TotalSeconds / 300;

        faktorLang.ShouldBeGreaterThan(faktorKurz);
    }

    [Fact]
    public void Ohne_Material_wird_nichts_geschaetzt()
    {
        ConversionDurationEstimate.For(TimeSpan.Zero).ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void Weniger_Diffusionsschritte_verkuerzen_die_Schaetzung()
    {
        var voll = ConversionDurationEstimate.For(TimeSpan.FromMinutes(3), diffusionSteps: 50);
        var halb = ConversionDurationEstimate.For(TimeSpan.FromMinutes(3), diffusionSteps: 25);

        halb.TotalSeconds.ShouldBeLessThan(voll.TotalSeconds);
        halb.TotalSeconds.ShouldBeInRange(voll.TotalSeconds * 0.4, voll.TotalSeconds * 0.6);
    }

    [Fact]
    public void Der_Sprachpfad_ist_schneller_als_der_Gesangspfad()
    {
        var gesang = ConversionDurationEstimate.For(TimeSpan.FromMinutes(3), f0Condition: true);
        var sprache = ConversionDurationEstimate.For(TimeSpan.FromMinutes(3), f0Condition: false);

        sprache.TotalSeconds.ShouldBeLessThan(gesang.TotalSeconds);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(90)]
    [InlineData(120)]
    public void Die_Umkehrung_passt_zur_Schaetzung(int limitMinuten)
    {
        // Was gerade noch hineinpasst, muss auch gerade noch geschaetzt werden.
        var limit = TimeSpan.FromMinutes(limitMinuten);

        var laengstes = ConversionDurationEstimate.LongestMaterialWithin(limit);
        var dafuer = ConversionDurationEstimate.For(laengstes);

        dafuer.ShouldBeLessThanOrEqualTo(limit);

        // Und eine Minute mehr passt nicht mehr hinein.
        ConversionDurationEstimate.For(laengstes + TimeSpan.FromMinutes(1))
            .ShouldBeGreaterThan(limit);
    }

    [Fact]
    public void Die_erlaubte_Materiallaenge_passt_in_das_Zeitlimit()
    {
        // Die eigentliche Zusicherung: Was die API annimmt, muss auch fertig
        // werden koennen. Waren beide Werte nicht aufeinander abgestimmt, lief
        // ein Auftrag erst eine Stunde und scheiterte dann -- ohne Ergebnis,
        // weil die Datei erst ganz am Ende entsteht.
        var erlaubt = AudioLimits.Default.MaxSourceDuration;
        var zeitlimit = TimeSpan.FromMinutes(120);

        ConversionDurationEstimate.For(erlaubt).ShouldBeLessThan(zeitlimit);
    }
}

public class SourceLengthRejectionTests
{
    private static AudioProperties Material(double minuten) =>
        new("pcm_s16le", TimeSpan.FromMinutes(minuten), 44100, 1);

    [Fact]
    public void Zu_langes_Material_wird_abgelehnt_und_nennt_die_Rechenzeit()
    {
        var result = AudioValidationPolicy.Validate(Material(12), AudioRole.Source);

        result.IsValid.ShouldBeFalse();
        result.ErrorCode.ShouldBe(ConversionErrorCode.InvalidAudio);
        // Die Meldung soll erklaeren, warum abgelehnt wurde -- sonst wirkt die
        // Grenze willkuerlich.
        result.Message.ShouldNotBeNull();
        result.Message.ShouldContain("Minuten");
        result.Message.ShouldContain("Rechenzeit");
    }

    [Fact]
    public void Material_innerhalb_der_Grenze_wird_angenommen()
    {
        AudioValidationPolicy.Validate(Material(6.5), AudioRole.Source).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Die_Grenze_liegt_bei_sieben_Minuten()
    {
        AudioLimits.Default.MaxSourceDuration.ShouldBe(TimeSpan.FromMinutes(7));
    }
}

public class OutputSampleRateTests
{
    [Fact]
    public void Ausgeliefert_wird_voreingestellt_mit_48_kHz()
    {
        // Projekte in der Musikproduktion arbeiten meist damit; das Modell
        // erzeugt aber 44,1 kHz.
        ConversionOptions.Default.OutputSampleRate.ShouldBe(48000);
        ConversionOptions.Default.DeliveryFormat.SampleRate.ShouldBe(48000);
    }

    [Fact]
    public void Im_Gesangspfad_muss_am_Ende_umgerechnet_werden()
    {
        // Das Modell liefert 44,1 kHz, ausgeliefert werden 48 kHz.
        ConversionOptions.Default.TargetFormat.SampleRate.ShouldBe(44100);
        ConversionOptions.Default.RequiresOutputConversion.ShouldBeTrue();
    }

    [Fact]
    public void Wer_die_Modellrate_will_bekommt_sie_unveraendert()
    {
        ConversionOptions.TryCreate(null, null, null, null, null, 44100, out var options, out _)
            .ShouldBeTrue();

        options.OutputSampleRate.ShouldBe(44100);
        // Keine Umrechnung: die Datei bleibt genau so, wie das Modell sie erzeugt hat.
        options.RequiresOutputConversion.ShouldBeFalse();
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    public void Uebliche_Raten_werden_angenommen(int rate)
    {
        ConversionOptions.TryCreate(null, null, null, null, null, rate, out var options, out _)
            .ShouldBeTrue();

        options.OutputSampleRate.ShouldBe(rate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(500000)]
    public void Unsinnige_Raten_werden_abgelehnt(int rate)
    {
        ConversionOptions.TryCreate(null, null, null, null, null, rate, out _, out var error)
            .ShouldBeFalse();

        error.ShouldNotBeNull();
    }

    [Fact]
    public void Der_Sprachpfad_wird_ebenfalls_auf_die_Ausgaberate_gebracht()
    {
        // Er erzeugt 22,05 kHz -- ohne Umrechnung waere die Datei fuer ein
        // Projekt noch weniger brauchbar als mit 44,1 kHz.
        ConversionOptions.TryCreate(null, null, null, f0Condition: false, null, null,
            out var options, out _).ShouldBeTrue();

        options.TargetFormat.SampleRate.ShouldBe(22050);
        options.RequiresOutputConversion.ShouldBeTrue();
        options.DeliveryFormat.SampleRate.ShouldBe(48000);
    }
}
