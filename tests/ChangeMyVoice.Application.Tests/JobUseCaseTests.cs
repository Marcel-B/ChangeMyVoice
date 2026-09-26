using ChangeMyVoice.Application.Common;
using ChangeMyVoice.Application.Ports;
using ChangeMyVoice.Application.Tests.Fakes;
using ChangeMyVoice.Application.UseCases.Jobs;
using ChangeMyVoice.Domain.Audio;
using ChangeMyVoice.Domain.Jobs;
using ChangeMyVoice.Domain.Voices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace ChangeMyVoice.Application.Tests;

public class SubmitConversionJobTests
{
    private readonly InMemoryReferenceVoiceRepository _voices = new();
    private readonly InMemoryConversionJobRepository _jobs = new();
    private readonly FakeVoiceStorage _storage = new();
    private readonly FakeJobWorkspaceStore _workspaces = new();
    private readonly FakeAudioProbe _probe = new();
    private readonly FakeAudioNormalizer _normalizer = new();
    private readonly FakeJobQueue _queue = new();
    private readonly FakeServiceInstance _instance = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));

    private static readonly AudioArtifactRef Upload = new("tmp/source-1");

    private SubmitConversionJob Sut() => new(
        _voices, _jobs, _storage, _workspaces, _probe, _normalizer, _queue, _instance, _clock);

    private async Task<VoiceId> GivenVoice()
    {
        var id = VoiceId.New();
        _storage.ReserveMaster(id);
        await _voices.SaveAsync(ReferenceVoice.Create(
            id, VoiceLabel.Create("Anna"), _clock.GetUtcNow(),
            new AudioProperties("pcm_s16le", TimeSpan.FromSeconds(10), 44100, 1),
            new AudioProperties("mp3", TimeSpan.FromSeconds(10), 44100, 2)));
        return id;
    }

    [Fact]
    public async Task Ein_gueltiger_Auftrag_wird_angenommen_und_eingereiht()
    {
        var voiceId = await GivenVoice();

        var result = await Sut().ExecuteAsync(
            new SubmitConversionJobCommand(voiceId, Upload, ConversionOptions.Default));

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Status.ShouldBe(JobStatus.Queued);
        result.Value.VoiceLabel.ShouldBe("Anna");
        _queue.Enqueued.ShouldHaveSingleItem().ShouldBe(result.Value.Id);
    }

    [Fact]
    public async Task In_der_Voreinstellung_wird_auf_44100_Hz_normalisiert()
    {
        var voiceId = await GivenVoice();

        await Sut().ExecuteAsync(
            new SubmitConversionJobCommand(voiceId, Upload, ConversionOptions.Default));

        // Genau die Rate, die librosa im Python-Lauf ohnehin verwenden wird —
        // damit wird kein zweites Mal resampelt.
        _normalizer.Calls.Count.ShouldBe(2);
        _normalizer.Calls.ShouldAllBe(c => c.SampleRate == 44100);
    }

    [Fact]
    public async Task Im_Sprachpfad_wird_auf_22050_Hz_normalisiert()
    {
        var voiceId = await GivenVoice();
        ConversionOptions.TryCreate(null, null, null, f0Condition: false, null, null, out var options, out _)
            .ShouldBeTrue();

        await Sut().ExecuteAsync(new SubmitConversionJobCommand(voiceId, Upload, options));

        _normalizer.Calls.Count.ShouldBe(2);
        _normalizer.Calls.ShouldAllBe(c => c.SampleRate == 22050);
    }

    [Fact]
    public async Task Die_Referenz_wird_in_das_Arbeitsverzeichnis_kopiert()
    {
        // Damit der Auftrag sich selbst traegt und ein gleichzeitiges Loeschen
        // der Stimme ihn nicht zerreissen kann.
        var voiceId = await GivenVoice();

        var result = await Sut().ExecuteAsync(
            new SubmitConversionJobCommand(voiceId, Upload, ConversionOptions.Default));

        var jobId = result.Value!.Id;
        _normalizer.Calls.ShouldContain(c =>
            c.Source == $"voices/{voiceId}/reference.wav" &&
            c.Destination == $"jobs/{jobId}/reference.wav");
    }

    [Fact]
    public async Task Eine_unbekannte_Stimme_wird_abgelehnt()
    {
        var result = await Sut().ExecuteAsync(
            new SubmitConversionJobCommand(VoiceId.New(), Upload, ConversionOptions.Default));

        result.Error!.Code.ShouldBe(OperationErrorCode.VoiceNotFound);
        _queue.Enqueued.ShouldBeEmpty();
    }

    [Fact]
    public async Task Eine_unbrauchbare_Quelle_kostet_keinen_Platz_in_der_Warteschlange()
    {
        var voiceId = await GivenVoice();
        _probe.SetFor(Upload, null);

        var result = await Sut().ExecuteAsync(
            new SubmitConversionJobCommand(voiceId, Upload, ConversionOptions.Default));

        result.Error!.Code.ShouldBe(OperationErrorCode.AudioRejected);
        result.Error.AudioErrorCode.ShouldBe(ConversionErrorCode.InvalidAudio);
        _queue.Enqueued.ShouldBeEmpty();
        _jobs.All.ShouldBeEmpty();
        _normalizer.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Eine_volle_Warteschlange_wird_ehrlich_gemeldet()
    {
        var voiceId = await GivenVoice();
        _queue.RejectEverything = true;

        var result = await Sut().ExecuteAsync(
            new SubmitConversionJobCommand(voiceId, Upload, ConversionOptions.Default));

        result.Error!.Code.ShouldBe(OperationErrorCode.QueueFull);
        _workspaces.DeletedLocations.ShouldNotBeEmpty();
    }
}

public class GetJobResultTests
{
    private readonly InMemoryConversionJobRepository _jobs = new();
    private readonly FakeJobWorkspaceStore _workspaces = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));

    private GetJobResult Sut() => new(_jobs, _workspaces, _clock);

    private async Task<ConversionJob> GivenCompletedJob()
    {
        var job = ConversionJob.Create(
            JobId.New(), VoiceId.New(), "Anna", ConversionOptions.Default,
            _clock.GetUtcNow(), Guid.NewGuid());
        job.Start(_clock.GetUtcNow(), Guid.NewGuid());
        job.Complete(_clock.GetUtcNow(), 4, "sha");
        await _jobs.SaveAsync(job);
        _workspaces.SetOutput(job.Id, [1, 2, 3, 4]);
        return job;
    }

    [Fact]
    public async Task Ein_fertiges_Ergebnis_wird_geliefert()
    {
        var job = await GivenCompletedJob();

        var result = await Sut().ExecuteAsync(job.Id);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.FileName.ShouldBe($"{job.Id}.wav");
        result.Value.Sha256.ShouldBe("sha");
    }

    [Fact]
    public async Task Der_Abruf_loescht_das_Ergebnis_nicht_sofort()
    {
        // Sonst waere ein abgebrochener Transfer ein Datenverlust.
        var job = await GivenCompletedJob();

        await Sut().ExecuteAsync(job.Id);
        var second = await Sut().ExecuteAsync(job.Id);

        second.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Ein_noch_laufender_Auftrag_meldet_dass_nichts_bereitliegt()
    {
        var job = ConversionJob.Create(
            JobId.New(), VoiceId.New(), "Anna", ConversionOptions.Default,
            _clock.GetUtcNow(), Guid.NewGuid());
        await _jobs.SaveAsync(job);

        var result = await Sut().ExecuteAsync(job.Id);

        result.Error!.Code.ShouldBe(OperationErrorCode.ResultNotReady);
    }

    [Fact]
    public async Task Ein_unbekannter_Auftrag_meldet_sich_als_nicht_gefunden()
    {
        var result = await Sut().ExecuteAsync(JobId.New());

        result.Error!.Code.ShouldBe(OperationErrorCode.JobNotFound);
    }

    [Fact]
    public async Task Ein_aufgeraeumtes_Ergebnis_meldet_sich_als_verschwunden()
    {
        var job = await GivenCompletedJob();
        job.MarkArtifactsPurged();
        await _jobs.SaveAsync(job);

        var result = await Sut().ExecuteAsync(job.Id);

        result.Error!.Code.ShouldBe(OperationErrorCode.ResultGone);
    }

    [Fact]
    public async Task Eine_fehlende_Datei_endet_nicht_in_einem_Serverfehler()
    {
        var job = await GivenCompletedJob();
        await _workspaces.DeleteAsync(job.Id);

        var result = await Sut().ExecuteAsync(job.Id);

        result.Error!.Code.ShouldBe(OperationErrorCode.ResultGone);
    }
}

public class ProcessConversionJobTests
{
    private readonly InMemoryConversionJobRepository _jobs = new();
    private readonly FakeJobWorkspaceStore _workspaces = new();
    private readonly FakeConversionEngine _engine = new();
    private readonly FakeServiceInstance _instance = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));

    private readonly FakeAudioNormalizer _normalizer = new();
    private readonly FakeJobNotifier _notifier = new();

    private static readonly WebhookUrl Webhook =
        WebhookUrl.Rehydrate("https://yue.example/api/voice/done");

    private ProcessConversionJob Sut() => new(
        _jobs, _workspaces, _engine, _normalizer, _instance, _notifier, _clock,
        NullLogger<ProcessConversionJob>.Instance);

    private async Task<ConversionJob> GivenQueuedJob(WebhookUrl? webhook = null)
    {
        var job = ConversionJob.Create(
            JobId.New(), VoiceId.New(), "Anna", ConversionOptions.Default,
            _clock.GetUtcNow(), _instance.InstanceId, webhookUrl: webhook);
        await _jobs.SaveAsync(job);
        return job;
    }

    [Fact]
    public async Task Ein_erfolgreicher_Lauf_schliesst_den_Auftrag_ab()
    {
        var job = await GivenQueuedJob();
        _workspaces.SetOutput(job.Id, [1, 2, 3, 4]);

        await Sut().ExecuteAsync(job.Id);

        var stored = await _jobs.FindAsync(job.Id);
        stored!.Status.ShouldBe(JobStatus.Completed);
        stored.OutputSizeBytes.ShouldBe(4);
        stored.OutputSha256.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Ein_Fehler_der_Maschine_wird_als_Fehlercode_uebernommen()
    {
        var job = await GivenQueuedJob();
        _engine.Outcome = ConversionOutcome.Failure(
            ConversionErrorCode.OutOfMemory, "kein Speicher");

        await Sut().ExecuteAsync(job.Id);

        var stored = await _jobs.FindAsync(job.Id);
        stored!.Status.ShouldBe(JobStatus.Failed);
        stored.Error!.Code.ShouldBe(ConversionErrorCode.OutOfMemory);
    }

    [Fact]
    public async Task Meldet_der_Lauf_Erfolg_ohne_Datei_gilt_das_als_fehlende_Ausgabe()
    {
        var job = await GivenQueuedJob();

        await Sut().ExecuteAsync(job.Id);

        var stored = await _jobs.FindAsync(job.Id);
        stored!.Error!.Code.ShouldBe(ConversionErrorCode.OutputNotCreated);
    }

    [Fact]
    public async Task Eine_unerwartete_Ausnahme_beendet_den_Arbeiter_nicht()
    {
        // Sonst bliebe die Warteschlange fuer alle folgenden Auftraege stehen.
        var job = await GivenQueuedJob();
        _engine.ThrowOnConvert = new InvalidOperationException("Boom");

        await Should.NotThrowAsync(Sut().ExecuteAsync(job.Id));

        var stored = await _jobs.FindAsync(job.Id);
        stored!.Status.ShouldBe(JobStatus.Failed);
        stored.Error!.Code.ShouldBe(ConversionErrorCode.InferenceFailed);
    }

    [Fact]
    public async Task Die_Prozesskennung_wird_sofort_festgehalten()
    {
        // Stuerzt der Dienst gleich darauf ab, muss sie bereits gespeichert sein,
        // sonst bleibt beim Neustart ein verwaister Python-Prozess zurueck.
        var job = await GivenQueuedJob();
        _engine.ReportProcessId = 4711;
        _workspaces.SetOutput(job.Id, [1]);

        await Sut().ExecuteAsync(job.Id);

        _engine.Calls.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Das_Ergebnis_wird_auf_die_Ausgaberate_gebracht()
    {
        // Das Modell schreibt nach raw-output in seiner eigenen Rate; daraus
        // entsteht die ausgelieferte Datei mit der Rate des Zielprojekts.
        var job = await GivenQueuedJob();
        _workspaces.SetOutput(job.Id, [1, 2, 3, 4]);

        await Sut().ExecuteAsync(job.Id);

        var call = _normalizer.Calls.ShouldHaveSingleItem();
        call.Source.ShouldEndWith("raw-output.wav");
        call.Destination.ShouldEndWith("output.wav");
        call.SampleRate.ShouldBe(48000);
    }

    [Fact]
    public async Task Das_Modell_schreibt_in_die_Rohdatei_nicht_in_die_Lieferdatei()
    {
        var job = await GivenQueuedJob();
        _workspaces.SetOutput(job.Id, [1]);

        await Sut().ExecuteAsync(job.Id);

        _engine.Calls.ShouldHaveSingleItem().Output.Locator.ShouldEndWith("raw-output.wav");
    }

    [Fact]
    public async Task Ein_bereits_abgebrochener_Auftrag_wird_uebersprungen()
    {
        var job = await GivenQueuedJob();
        job.Cancel(_clock.GetUtcNow());
        await _jobs.SaveAsync(job);

        await Sut().ExecuteAsync(job.Id);

        _engine.Calls.ShouldBeEmpty();
        (await _jobs.FindAsync(job.Id))!.Status.ShouldBe(JobStatus.Cancelled);
    }

    [Fact]
    public async Task Ein_fertiger_Auftrag_meldet_sich_an_seiner_Adresse()
    {
        var job = await GivenQueuedJob(Webhook);
        _workspaces.SetOutput(job.Id, [1, 2, 3, 4]);

        await Sut().ExecuteAsync(job.Id);

        var sent = _notifier.Sent.ShouldHaveSingleItem();
        sent.Target.ShouldBe(Webhook);
        sent.Job.Status.ShouldBe(JobStatus.Completed);
        sent.Job.IsResultAvailable.ShouldBeTrue();
        sent.Job.OutputSizeBytes.ShouldBe(4);
    }

    [Fact]
    public async Task Ein_fehlgeschlagener_Auftrag_meldet_sich_ebenfalls()
    {
        var job = await GivenQueuedJob(Webhook);
        _engine.Outcome = ConversionOutcome.Failure(
            ConversionErrorCode.OutOfMemory, "kein Speicher");

        await Sut().ExecuteAsync(job.Id);

        var sent = _notifier.Sent.ShouldHaveSingleItem();
        sent.Job.Status.ShouldBe(JobStatus.Failed);
        sent.Job.Error!.Code.ShouldBe(ConversionErrorCode.OutOfMemory);
    }

    [Fact]
    public async Task Eine_unerwartete_Ausnahme_wird_gemeldet()
    {
        var job = await GivenQueuedJob(Webhook);
        _engine.ThrowOnConvert = new InvalidOperationException("Boom");

        await Sut().ExecuteAsync(job.Id);

        _notifier.Sent.ShouldHaveSingleItem().Job.Status.ShouldBe(JobStatus.Failed);
    }

    [Fact]
    public async Task Ohne_Adresse_wird_nichts_gemeldet()
    {
        var job = await GivenQueuedJob();
        _workspaces.SetOutput(job.Id, [1]);

        await Sut().ExecuteAsync(job.Id);

        _notifier.Sent.ShouldBeEmpty();
    }
}

public class CancelJobTests
{
    private readonly InMemoryConversionJobRepository _jobs = new();
    private readonly FakeJobWorkspaceStore _workspaces = new();
    private readonly FakeJobNotifier _notifier = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));

    private CancelJob Sut() => new(_jobs, _workspaces, _notifier, _clock);

    [Fact]
    public async Task Ein_Abbruch_wird_genau_einmal_gemeldet()
    {
        // Der Abbruch ist wiederholbar; der Empfaenger soll trotzdem nur einen
        // Anstoss bekommen.
        var job = ConversionJob.Create(
            JobId.New(), VoiceId.New(), "Anna", ConversionOptions.Default,
            _clock.GetUtcNow(), Guid.NewGuid(),
            webhookUrl: WebhookUrl.Rehydrate("https://yue.example/hook"));
        await _jobs.SaveAsync(job);

        (await Sut().ExecuteAsync(job.Id)).IsSuccess.ShouldBeTrue();
        (await Sut().ExecuteAsync(job.Id)).IsSuccess.ShouldBeTrue();

        _notifier.Sent.ShouldHaveSingleItem().Job.Status.ShouldBe(JobStatus.Cancelled);
    }
}
