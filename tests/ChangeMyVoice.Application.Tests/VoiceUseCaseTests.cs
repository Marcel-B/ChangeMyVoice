using ChangeMyVoice.Application.Common;
using ChangeMyVoice.Application.Ports;
using ChangeMyVoice.Application.Tests.Fakes;
using ChangeMyVoice.Application.UseCases.Voices;
using ChangeMyVoice.Domain.Audio;
using ChangeMyVoice.Domain.Jobs;
using ChangeMyVoice.Domain.Voices;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace ChangeMyVoice.Application.Tests;

public class AddReferenceVoiceTests
{
    private readonly InMemoryReferenceVoiceRepository _voices = new();
    private readonly FakeVoiceStorage _storage = new();
    private readonly FakeAudioProbe _probe = new();
    private readonly FakeAudioNormalizer _normalizer = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));

    private static readonly AudioArtifactRef Upload = new("tmp/upload-1");

    private AddReferenceVoice Sut() => new(_voices, _storage, _probe, _normalizer, _clock);

    [Fact]
    public async Task Eine_gueltige_Aufnahme_wird_angelegt()
    {
        var result = await Sut().ExecuteAsync(new AddReferenceVoiceCommand("Anna", Upload));

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Label.ShouldBe("Anna");
        result.Value.CreatedAtUtc.ShouldBe(_clock.GetUtcNow());
        _voices.Count.ShouldBe(1);
        _storage.StoredCount.ShouldBe(1);
    }

    [Fact]
    public async Task Die_Aufnahme_wird_als_Master_mit_44100_Hz_abgelegt()
    {
        // Die hoehere der beiden Zielraten, damit eine einzige Datei sowohl den
        // Sprach- als auch den Gesangspfad bedienen kann.
        await Sut().ExecuteAsync(new AddReferenceVoiceCommand("Anna", Upload));

        var call = _normalizer.Calls.ShouldHaveSingleItem();
        call.SampleRate.ShouldBe(44100);
        call.Source.ShouldBe("tmp/upload-1");
    }

    [Fact]
    public async Task Die_urspruenglichen_Eigenschaften_bleiben_nachvollziehbar()
    {
        // Sonst sucht man ein schwaches Ergebnis spaeter im Modell statt im
        // Ausgangsmaterial.
        var original = new AudioProperties("mp3", TimeSpan.FromSeconds(12), 22050, 2);
        _probe.SetFor(Upload, original);
        _probe.SetFor(new AudioArtifactRef("voices/"), null);

        var result = await Sut().ExecuteAsync(new AddReferenceVoiceCommand("Anna", Upload));

        result.Value!.OriginalAudio.Codec.ShouldBe("mp3");
        result.Value.OriginalAudio.Channels.ShouldBe(2);
        result.Value.StoredAudio.SampleRate.ShouldBe(44100);
    }

    [Fact]
    public async Task Eine_zu_kurze_Referenz_wird_mit_dem_passenden_Code_abgelehnt()
    {
        _probe.SetFor(Upload, new AudioProperties("pcm_s16le", TimeSpan.FromSeconds(1), 44100, 1));

        var result = await Sut().ExecuteAsync(new AddReferenceVoiceCommand("Anna", Upload));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(OperationErrorCode.AudioRejected);
        result.Error.AudioErrorCode.ShouldBe(ConversionErrorCode.ReferenceTooShort);
        _voices.Count.ShouldBe(0);
        _storage.StoredCount.ShouldBe(0);
    }

    [Fact]
    public async Task Eine_unlesbare_Datei_wird_abgelehnt()
    {
        _probe.SetFor(Upload, null);

        var result = await Sut().ExecuteAsync(new AddReferenceVoiceCommand("Anna", Upload));

        result.Error!.AudioErrorCode.ShouldBe(ConversionErrorCode.InvalidAudio);
        _normalizer.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Eine_zu_lange_Referenz_wird_angenommen_und_gekuerzt()
    {
        _probe.SetFor(Upload, new AudioProperties("pcm_s16le", TimeSpan.FromMinutes(3), 44100, 1));

        var result = await Sut().ExecuteAsync(new AddReferenceVoiceCommand("Anna", Upload));

        result.IsSuccess.ShouldBeTrue();
        _normalizer.Calls.ShouldHaveSingleItem().MaxDuration.ShouldBe(TimeSpan.FromSeconds(25));
    }

    [Fact]
    public async Task Eine_belegte_Bezeichnung_wird_abgelehnt()
    {
        await Sut().ExecuteAsync(new AddReferenceVoiceCommand("Anna", Upload));

        var result = await Sut().ExecuteAsync(new AddReferenceVoiceCommand("anna", Upload));

        result.Error!.Code.ShouldBe(OperationErrorCode.DuplicateVoiceLabel);
        _voices.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Eine_leere_Bezeichnung_wird_abgelehnt()
    {
        var result = await Sut().ExecuteAsync(new AddReferenceVoiceCommand("  ", Upload));

        result.Error!.Code.ShouldBe(OperationErrorCode.InvalidInput);
    }

    [Fact]
    public async Task Bricht_die_Umwandlung_ab_bleibt_keine_halbe_Stimme_zurueck()
    {
        _normalizer.ThrowOnNormalize = new InvalidOperationException("ffmpeg kaputt");

        await Should.ThrowAsync<InvalidOperationException>(
            Sut().ExecuteAsync(new AddReferenceVoiceCommand("Anna", Upload)));

        _storage.StoredCount.ShouldBe(0);
        _voices.Count.ShouldBe(0);
    }
}

public class DeleteReferenceVoiceTests
{
    private readonly InMemoryReferenceVoiceRepository _voices = new();
    private readonly InMemoryConversionJobRepository _jobs = new();
    private readonly FakeVoiceStorage _storage = new();

    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private async Task<VoiceId> GivenVoice()
    {
        var id = VoiceId.New();
        _storage.ReserveMaster(id);
        await _voices.SaveAsync(ReferenceVoice.Create(
            id, VoiceLabel.Create("Anna"), Now,
            new AudioProperties("pcm_s16le", TimeSpan.FromSeconds(10), 44100, 1),
            new AudioProperties("mp3", TimeSpan.FromSeconds(10), 44100, 2)));
        return id;
    }

    private DeleteReferenceVoice Sut() => new(_voices, _jobs, _storage);

    [Fact]
    public async Task Eine_unbenutzte_Stimme_wird_entfernt()
    {
        var id = await GivenVoice();

        var result = await Sut().ExecuteAsync(id);

        result.IsSuccess.ShouldBeTrue();
        _voices.Count.ShouldBe(0);
        _storage.StoredCount.ShouldBe(0);
    }

    [Fact]
    public async Task Eine_unbekannte_Stimme_meldet_sich_als_nicht_gefunden()
    {
        var result = await Sut().ExecuteAsync(VoiceId.New());

        result.Error!.Code.ShouldBe(OperationErrorCode.VoiceNotFound);
    }

    [Fact]
    public async Task Eine_noch_benoetigte_Stimme_wird_nicht_entfernt()
    {
        var id = await GivenVoice();
        await _jobs.SaveAsync(ConversionJob.Create(
            JobId.New(), id, "Anna", ConversionOptions.Default, Now, Guid.NewGuid()));

        var result = await Sut().ExecuteAsync(id);

        result.Error!.Code.ShouldBe(OperationErrorCode.VoiceInUse);
        _voices.Count.ShouldBe(1);
        _storage.StoredCount.ShouldBe(1);
    }

    [Fact]
    public async Task Ein_abgeschlossener_Auftrag_blockiert_das_Loeschen_nicht()
    {
        var id = await GivenVoice();
        var job = ConversionJob.Create(
            JobId.New(), id, "Anna", ConversionOptions.Default, Now, Guid.NewGuid());
        job.Start(Now, Guid.NewGuid());
        job.Complete(Now, 10, "sha");
        await _jobs.SaveAsync(job);

        var result = await Sut().ExecuteAsync(id);

        result.IsSuccess.ShouldBeTrue();
    }
}
