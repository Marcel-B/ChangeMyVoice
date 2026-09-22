using ChangeMyVoice.Adapters.Inference;
using ChangeMyVoice.Application.Ports;
using ChangeMyVoice.Domain.Audio;
using ChangeMyVoice.Domain.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace ChangeMyVoice.Adapters.Tests;

public class FfprobeAudioProbeTests : IDisposable
{
    private readonly AudioFixtures _fixtures = new();

    private static FfprobeAudioProbe Sut() => new(
        Options.Create(new AudioToolingOptions()), NullLogger<FfprobeAudioProbe>.Instance);

    [SkippableFact]
    public async Task Eine_WAV_Datei_wird_korrekt_vermessen()
    {
        Skip.IfNot(AudioFixtures.FfmpegAvailable, "ffmpeg ist nicht verfügbar.");
        var path = _fixtures.CreateTone("ton.wav", seconds: 5, sampleRate: 48000, channels: 2);

        var result = await Sut().ProbeAsync(new AudioArtifactRef(path));

        result.ShouldNotBeNull();
        result.SampleRate.ShouldBe(48000);
        result.Channels.ShouldBe(2);
        result.Duration.TotalSeconds.ShouldBe(5, tolerance: 0.2);
    }

    [SkippableFact]
    public async Task Eine_MP3_Datei_wird_erkannt()
    {
        Skip.IfNot(AudioFixtures.FfmpegAvailable, "ffmpeg ist nicht verfügbar.");
        var path = _fixtures.CreateTone("ton.mp3", seconds: 4, sampleRate: 44100);

        var result = await Sut().ProbeAsync(new AudioArtifactRef(path));

        result!.Codec.ShouldBe("mp3");
    }

    [SkippableFact]
    public async Task Eine_FLAC_Datei_wird_erkannt()
    {
        Skip.IfNot(AudioFixtures.FfmpegAvailable, "ffmpeg ist nicht verfügbar.");
        var path = _fixtures.CreateTone("ton.flac", seconds: 4);

        var result = await Sut().ProbeAsync(new AudioArtifactRef(path));

        result!.Codec.ShouldBe("flac");
    }

    [SkippableFact]
    public async Task Eine_M4A_Datei_wird_erkannt()
    {
        // Genau der Fall, den librosa selbst nicht lesen koennte — deshalb
        // braucht es ffmpeg davor.
        Skip.IfNot(AudioFixtures.FfmpegAvailable, "ffmpeg ist nicht verfügbar.");
        var path = _fixtures.CreateTone("ton.m4a", seconds: 4);

        var result = await Sut().ProbeAsync(new AudioArtifactRef(path));

        result!.Codec.ShouldBe("aac");
    }

    [Fact]
    public async Task Eine_Textdatei_mit_wav_Endung_liefert_kein_Ergebnis()
    {
        var path = _fixtures.CreateTextFileNamedWav();

        var result = await Sut().ProbeAsync(new AudioArtifactRef(path));

        result.ShouldBeNull();
    }

    [Fact]
    public async Task Eine_leere_Datei_liefert_kein_Ergebnis()
    {
        var path = _fixtures.CreateEmptyFile();

        var result = await Sut().ProbeAsync(new AudioArtifactRef(path));

        result.ShouldBeNull();
    }

    [Fact]
    public async Task Eine_fehlende_Datei_liefert_kein_Ergebnis()
    {
        var result = await Sut().ProbeAsync(new AudioArtifactRef(_fixtures.PathFor("gibtsnicht.wav")));

        result.ShouldBeNull();
    }

    public void Dispose() => _fixtures.Dispose();
}

public class FfmpegAudioNormalizerTests : IDisposable
{
    private readonly AudioFixtures _fixtures = new();

    private static FfmpegAudioNormalizer Sut() => new(
        Options.Create(new AudioToolingOptions()), NullLogger<FfmpegAudioNormalizer>.Instance);

    [SkippableTheory]
    [InlineData(22050)]
    [InlineData(44100)]
    public async Task Die_Ausgabe_trifft_genau_die_Zielrate_in_mono_PCM(int targetRate)
    {
        // Der eigentliche Nachweis: Wird hier auf eine andere Rate umgerechnet,
        // resampelt librosa im Python-Lauf ein zweites Mal.
        Skip.IfNot(AudioFixtures.FfmpegAvailable, "ffmpeg ist nicht verfügbar.");
        var source = _fixtures.CreateTone("quelle.wav", sampleRate: 48000, channels: 2);
        var destination = _fixtures.PathFor($"ziel-{targetRate}.wav");

        await Sut().NormalizeAsync(
            new AudioArtifactRef(source),
            new AudioArtifactRef(destination),
            TargetAudioFormat.For(f0Condition: targetRate == 44100));

        var measured = AudioFixtures.Measure(destination);
        measured.SampleRate.ShouldBe(targetRate);
        measured.Channels.ShouldBe(1);
        measured.Codec.ShouldBe("pcm_s16le");
    }

    [SkippableFact]
    public async Task Eine_MP3_Quelle_wird_in_PCM_umgewandelt()
    {
        Skip.IfNot(AudioFixtures.FfmpegAvailable, "ffmpeg ist nicht verfügbar.");
        var source = _fixtures.CreateTone("quelle.mp3", sampleRate: 22050);
        var destination = _fixtures.PathFor("aus-mp3.wav");

        await Sut().NormalizeAsync(
            new AudioArtifactRef(source),
            new AudioArtifactRef(destination),
            TargetAudioFormat.ReferenceMaster);

        var measured = AudioFixtures.Measure(destination);
        measured.Codec.ShouldBe("pcm_s16le");
        measured.SampleRate.ShouldBe(44100);
    }

    [SkippableFact]
    public async Task Eine_M4A_Quelle_wird_in_PCM_umgewandelt()
    {
        Skip.IfNot(AudioFixtures.FfmpegAvailable, "ffmpeg ist nicht verfügbar.");
        var source = _fixtures.CreateTone("quelle.m4a");
        var destination = _fixtures.PathFor("aus-m4a.wav");

        await Sut().NormalizeAsync(
            new AudioArtifactRef(source),
            new AudioArtifactRef(destination),
            TargetAudioFormat.ReferenceMaster);

        AudioFixtures.Measure(destination).Codec.ShouldBe("pcm_s16le");
    }

    [SkippableFact]
    public async Task Eine_zu_lange_Aufnahme_wird_auf_die_Vorgabe_gekuerzt()
    {
        Skip.IfNot(AudioFixtures.FfmpegAvailable, "ffmpeg ist nicht verfügbar.");
        var source = _fixtures.CreateTone("lang.wav", seconds: 40);
        var destination = _fixtures.PathFor("gekuerzt.wav");

        await Sut().NormalizeAsync(
            new AudioArtifactRef(source),
            new AudioArtifactRef(destination),
            TargetAudioFormat.ReferenceMaster,
            maxDuration: TimeSpan.FromSeconds(25));

        var probe = new FfprobeAudioProbe(
            Options.Create(new AudioToolingOptions()), NullLogger<FfprobeAudioProbe>.Instance);
        var properties = await probe.ProbeAsync(new AudioArtifactRef(destination));

        properties!.Duration.TotalSeconds.ShouldBe(25, tolerance: 0.5);
    }

    [Fact]
    public async Task Eine_unlesbare_Quelle_fuehrt_zu_einem_klaren_Fehler()
    {
        var source = _fixtures.CreateTextFileNamedWav();
        var destination = _fixtures.PathFor("ziel.wav");

        await Should.ThrowAsync<AudioConversionException>(
            Sut().NormalizeAsync(
                new AudioArtifactRef(source),
                new AudioArtifactRef(destination),
                TargetAudioFormat.ReferenceMaster));
    }

    public void Dispose() => _fixtures.Dispose();
}

public class MlxVcConversionEngineTests
{
    private static MlxVcConversionEngine Sut(InferenceOptions? options = null) => new(
        Options.Create(options ?? new InferenceOptions
        {
            PythonExecutable = "/pfad/zu/python",
            ScriptPath = "/pfad/zu/skript.py",
            WorkingDirectory = "/pfad/zu/mlx-vc",
        }),
        NullLogger<MlxVcConversionEngine>.Instance);

    private static ConversionRequest Request(ConversionOptions? options = null) => new(
        new AudioArtifactRef("/tmp/source.wav"),
        new AudioArtifactRef("/tmp/reference.wav"),
        new AudioArtifactRef("/tmp/output.wav"),
        options ?? ConversionOptions.Default);

    [Fact]
    public void Der_Aufruf_enthaelt_niemals_paketverwaltende_Argumente()
    {
        // init.md §30 untersagt es ausdrücklich, die funktionierende Umgebung
        // eigenmächtig zu verändern. Diese Regel steht sonst nur in einem
        // Dokument — hier bricht stattdessen der Build.
        var arguments = Sut().BuildArguments(Request());

        var offenders = arguments
            .Where(argument => MlxVcConversionEngine.ForbiddenArgumentMarkers.Any(
                marker => string.Equals(argument, marker, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        offenders.ShouldBeEmpty(
            "Diese Argumente würden die Python-Umgebung verändern: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void Der_Aufruf_uebergibt_Quelle_Referenz_und_Ziel()
    {
        var arguments = Sut().BuildArguments(Request());

        arguments.ShouldContain("--source");
        arguments.ShouldContain("/tmp/source.wav");
        arguments.ShouldContain("--reference");
        arguments.ShouldContain("--output");
        arguments[0].ShouldBe("/pfad/zu/skript.py");
    }

    [Fact]
    public void Ohne_F0_Konditionierung_wird_der_Schalter_weggelassen()
    {
        Sut().BuildArguments(Request()).ShouldNotContain("--f0-condition");
    }

    [Fact]
    public void Mit_F0_Konditionierung_wird_der_Schalter_gesetzt()
    {
        ConversionOptions.TryCreate(null, null, null, true, null, out var options, out _);

        Sut().BuildArguments(Request(options)).ShouldContain("--f0-condition");
    }

    [Fact]
    public void Die_Modellpfade_werden_fest_vorgegeben()
    {
        // Sonst greifen die relativen Vorgaben von seed_vc_infer.py und
        // verschiedene Läufe könnten unterschiedliche Checkpoints verwenden.
        var engine = Sut(new InferenceOptions
        {
            PythonExecutable = "/python",
            ScriptPath = "/skript.py",
            WorkingDirectory = "/mlx-vc",
            SeedVcPath = "/seed-vc-ref",
            HuggingFaceCachePath = "/cache",
        });

        var environment = engine.BuildEnvironment();

        environment["SEED_VC_PATH"].ShouldBe("/seed-vc-ref");
        environment["HF_HUB_CACHE"].ShouldBe("/cache");
    }

    [Theory]
    [InlineData("RuntimeError: MPS backend out of memory", ConversionErrorCode.OutOfMemory)]
    [InlineData("RuntimeError: Metal device lost", ConversionErrorCode.MpsError)]
    [InlineData("huggingface_hub.errors.HfHubHTTPError", ConversionErrorCode.ModelDownloadFailed)]
    [InlineData("ModuleNotFoundError: No module named 'mlx_vc'", ConversionErrorCode.ModelLoadFailed)]
    [InlineData("irgendein anderer Fehler", ConversionErrorCode.InferenceFailed)]
    public void Die_Fehlerausgabe_wird_einem_Fehlercode_zugeordnet(
        string stderr, ConversionErrorCode expected)
    {
        MlxVcConversionEngine.ClassifyFromStandardError(stderr).ShouldBe(expected);
    }
}
