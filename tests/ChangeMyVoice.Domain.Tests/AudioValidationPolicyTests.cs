using ChangeMyVoice.Domain.Audio;
using ChangeMyVoice.Domain.Jobs;
using Shouldly;

namespace ChangeMyVoice.Domain.Tests;

public class AudioValidationPolicyTests
{
    private static AudioProperties Props(
        string codec = "pcm_s16le",
        double seconds = 10,
        int sampleRate = 44100,
        int channels = 1) =>
        new(codec, TimeSpan.FromSeconds(seconds), sampleRate, channels);

    [Fact]
    public void Eine_nicht_lesbare_Datei_gilt_als_ungueltiges_Audio()
    {
        var result = AudioValidationPolicy.Validate(null, AudioRole.Reference);

        result.IsValid.ShouldBeFalse();
        result.ErrorCode.ShouldBe(ConversionErrorCode.InvalidAudio);
    }

    [Theory]
    [InlineData("pcm_s16le")]
    [InlineData("mp3")]
    [InlineData("flac")]
    [InlineData("aac")]
    [InlineData("opus")]
    public void Verbreitete_Codecs_werden_angenommen(string codec)
    {
        var result = AudioValidationPolicy.Validate(Props(codec: codec), AudioRole.Reference);

        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("h264")]
    [InlineData("wmav2")]
    public void Fremde_Codecs_werden_abgelehnt(string codec)
    {
        var result = AudioValidationPolicy.Validate(Props(codec: codec), AudioRole.Reference);

        result.ErrorCode.ShouldBe(ConversionErrorCode.UnsupportedFormat);
    }

    [Fact]
    public void Eine_Aufnahme_ohne_Laenge_wird_abgelehnt()
    {
        var result = AudioValidationPolicy.Validate(Props(seconds: 0), AudioRole.Reference);

        result.ErrorCode.ShouldBe(ConversionErrorCode.InvalidAudio);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Ungewoehnliche_Kanalzahlen_werden_abgelehnt(int channels)
    {
        var result = AudioValidationPolicy.Validate(Props(channels: channels), AudioRole.Reference);

        result.ErrorCode.ShouldBe(ConversionErrorCode.UnsupportedFormat);
    }

    [Fact]
    public void Stereo_wird_angenommen_und_spaeter_zusammengelegt()
    {
        var result = AudioValidationPolicy.Validate(Props(channels: 2), AudioRole.Reference);

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Zu_niedrige_Abtastraten_werden_abgelehnt()
    {
        var result = AudioValidationPolicy.Validate(Props(sampleRate: 8000), AudioRole.Reference);

        result.ErrorCode.ShouldBe(ConversionErrorCode.UnsupportedFormat);
    }

    [Fact]
    public void Eine_zu_kurze_Referenz_wird_abgelehnt()
    {
        var result = AudioValidationPolicy.Validate(Props(seconds: 1.5), AudioRole.Reference);

        result.ErrorCode.ShouldBe(ConversionErrorCode.ReferenceTooShort);
    }

    [Fact]
    public void Eine_zu_lange_Referenz_wird_angenommen_aber_gekuerzt()
    {
        // Seed-VC schneidet ohnehin bei 25 Sekunden ab; das offen zu melden ist
        // ehrlicher, als stillschweigend zu kuerzen.
        var result = AudioValidationPolicy.Validate(Props(seconds: 180), AudioRole.Reference);

        result.IsValid.ShouldBeTrue();
        result.WillBeTruncatedTo.ShouldBe(TimeSpan.FromSeconds(25));
        result.Message.ShouldNotBeNull();
    }

    [Fact]
    public void Eine_kurze_Quelle_unterliegt_nicht_der_Referenzuntergrenze()
    {
        var result = AudioValidationPolicy.Validate(Props(seconds: 1.5), AudioRole.Source);

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Eine_ueberlange_Quelle_wird_abgelehnt()
    {
        var result = AudioValidationPolicy.Validate(
            Props(seconds: TimeSpan.FromMinutes(30).TotalSeconds), AudioRole.Source);

        result.ErrorCode.ShouldBe(ConversionErrorCode.InvalidAudio);
    }
}

public class TargetAudioFormatTests
{
    [Fact]
    public void Ohne_F0_Konditionierung_gilt_der_Sprachpfad_mit_22050_Hz()
    {
        var format = TargetAudioFormat.For(f0Condition: false);

        format.SampleRate.ShouldBe(22050);
        format.Channels.ShouldBe(1);
    }

    [Fact]
    public void Mit_F0_Konditionierung_gilt_der_Gesangspfad_mit_44100_Hz()
    {
        var format = TargetAudioFormat.For(f0Condition: true);

        format.SampleRate.ShouldBe(44100);
    }

    [Fact]
    public void Der_Referenzmaster_liegt_auf_der_hoeheren_der_beiden_Raten()
    {
        // So bedient eine einzige gespeicherte Datei beide Pfade und muss
        // hoechstens einmal heruntergerechnet werden.
        TargetAudioFormat.ReferenceMaster.SampleRate.ShouldBe(44100);
    }

    [Fact]
    public void Die_Voreinstellung_ist_der_Gesangspfad()
    {
        // Der Dienst ist fuer Gesang gedacht; ohne F0-Konditionierung klingt
        // das Ergebnis eher nach Sprachumwandlung.
        ConversionOptions.Default.F0Condition.ShouldBeTrue();
        ConversionOptions.Default.TargetFormat.SampleRate.ShouldBe(44100);
    }

    [Fact]
    public void Der_Sprachpfad_laesst_sich_ausdruecklich_waehlen()
    {
        ConversionOptions.TryCreate(null, null, null, f0Condition: false, null, null, null, null,
            out var options, out _).ShouldBeTrue();

        options.TargetFormat.SampleRate.ShouldBe(22050);
    }
}
