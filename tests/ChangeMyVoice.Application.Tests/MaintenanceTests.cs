using ChangeMyVoice.Application.Tests.Fakes;
using ChangeMyVoice.Application.UseCases.Maintenance;
using ChangeMyVoice.Domain.Audio;
using ChangeMyVoice.Domain.Jobs;
using ChangeMyVoice.Domain.Voices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace ChangeMyVoice.Application.Tests;

public class CleanupJobArtifactsTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryConversionJobRepository _jobs = new();
    private readonly FakeJobWorkspaceStore _workspaces = new();
    private readonly FakeTimeProvider _clock = new(Start);
    private readonly CleanupSettings _settings = new();
    private readonly FakeJobNotifier _notifier = new();

    private CleanupJobArtifacts Sut() => new(
        _jobs, _workspaces, _settings, _notifier, _clock, NullLogger<CleanupJobArtifacts>.Instance);

    private async Task<ConversionJob> GivenCompletedJob(DateTimeOffset finishedAt)
    {
        var job = ConversionJob.Create(
            JobId.New(), VoiceId.New(), "Anna", ConversionOptions.Default, finishedAt, Guid.NewGuid());
        job.Start(finishedAt, Guid.NewGuid());
        job.Complete(finishedAt, 10, "sha");
        await _jobs.SaveAsync(job);
        _workspaces.Create(job.Id);
        return job;
    }

    [Fact]
    public async Task Ein_frischer_Auftrag_bleibt_unangetastet()
    {
        var job = await GivenCompletedJob(Start);
        _clock.SetUtcNow(Start.AddMinutes(30));

        var report = await Sut().ExecuteAsync();

        report.PurgedJobs.ShouldBe(0);
        (await _jobs.FindAsync(job.Id))!.ArtifactsPurged.ShouldBeFalse();
    }

    [Fact]
    public async Task Nach_Fristablauf_werden_die_Dateien_entfernt()
    {
        var job = await GivenCompletedJob(Start);
        _clock.SetUtcNow(Start.AddHours(25));

        var report = await Sut().ExecuteAsync();

        report.PurgedJobs.ShouldBe(1);
        var stored = await _jobs.FindAsync(job.Id);
        stored!.ArtifactsPurged.ShouldBeTrue();
        // Der Auftrag bleibt abfragbar, nur seine Dateien sind weg.
        stored.Status.ShouldBe(JobStatus.Completed);
    }

    [Fact]
    public async Task Ein_haengender_Auftrag_wird_mit_Zeitueberschreitung_beendet()
    {
        var job = ConversionJob.Create(
            JobId.New(), VoiceId.New(), "Anna", ConversionOptions.Default, Start, Guid.NewGuid());
        job.Start(Start, Guid.NewGuid());
        await _jobs.SaveAsync(job);
        _clock.SetUtcNow(Start.AddHours(2));

        var report = await Sut().ExecuteAsync();

        report.TimedOutJobs.ShouldBe(1);
        var stored = await _jobs.FindAsync(job.Id);
        stored!.Status.ShouldBe(JobStatus.Failed);
        stored.Error!.Code.ShouldBe(ConversionErrorCode.Timeout);
    }

    [Fact]
    public async Task Ein_haengender_Auftrag_meldet_die_Zeitueberschreitung()
    {
        var job = ConversionJob.Create(
            JobId.New(), VoiceId.New(), "Anna", ConversionOptions.Default, Start, Guid.NewGuid(),
            webhookUrl: WebhookUrl.Rehydrate("https://yue.example/hook"));
        job.Start(Start, Guid.NewGuid());
        await _jobs.SaveAsync(job);
        _clock.SetUtcNow(Start.AddHours(2));

        await Sut().ExecuteAsync();

        _notifier.Sent.ShouldHaveSingleItem().Job.Error!.Code.ShouldBe(ConversionErrorCode.Timeout);
    }

    [Fact]
    public async Task Das_Aufraeumen_eines_fertigen_Auftrags_meldet_nichts()
    {
        // Das Ende wurde schon gemeldet, als der Auftrag fertig wurde.
        var job = ConversionJob.Create(
            JobId.New(), VoiceId.New(), "Anna", ConversionOptions.Default, Start, Guid.NewGuid(),
            webhookUrl: WebhookUrl.Rehydrate("https://yue.example/hook"));
        job.Start(Start, Guid.NewGuid());
        job.Complete(Start, 10, "sha");
        await _jobs.SaveAsync(job);
        _clock.SetUtcNow(Start.AddHours(25));

        (await Sut().ExecuteAsync()).PurgedJobs.ShouldBe(1);

        _notifier.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Ein_Verzeichnis_ohne_Auftrag_wird_nach_der_Schonfrist_entfernt()
    {
        // Genau das bleibt nach einem harten Absturz zurueck.
        var orphanId = JobId.New();
        _workspaces.AddOrphan(orphanId, $"jobs/{orphanId}", Start);
        _clock.SetUtcNow(Start.AddHours(2));

        var report = await Sut().ExecuteAsync();

        report.OrphanedWorkspaces.ShouldBe(1);
        _workspaces.DeletedLocations.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Ein_frisches_Verzeichnis_ohne_Auftrag_geniesst_eine_Schonfrist()
    {
        // Sonst traefe es ein Verzeichnis, das gerade erst angelegt wurde.
        var orphanId = JobId.New();
        _workspaces.AddOrphan(orphanId, $"jobs/{orphanId}", Start);
        _clock.SetUtcNow(Start.AddMinutes(5));

        var report = await Sut().ExecuteAsync();

        report.OrphanedWorkspaces.ShouldBe(0);
    }

    [Fact]
    public async Task Ein_unlesbarer_Verzeichnisname_wird_ebenfalls_entfernt()
    {
        _workspaces.AddOrphan(null, "jobs/kaputter-name", Start);
        _clock.SetUtcNow(Start.AddHours(2));

        var report = await Sut().ExecuteAsync();

        report.OrphanedWorkspaces.ShouldBe(1);
        _workspaces.DeletedLocations.ShouldContain("jobs/kaputter-name");
    }

    [Fact]
    public async Task Zwischendateien_werden_bei_jedem_Lauf_aufgeraeumt()
    {
        await Sut().ExecuteAsync();

        _workspaces.TemporaryPurgeCount.ShouldBe(1);
    }

    [Fact]
    public async Task Referenzstimmen_ueberstehen_beliebig_viele_Aufraeumlaeufe()
    {
        // Die zentrale Zusicherung: Der Aufraeumlauf fasst ausschliesslich
        // Arbeitsdateien an. Waere hier je ein Stimmenordner betroffen, waeren
        // dauerhaft gespeicherte Daten verloren.
        var voices = new FakeVoiceStorage();
        var kept = Enumerable.Range(0, 5).Select(_ =>
        {
            var id = VoiceId.New();
            voices.ReserveMaster(id);
            return id;
        }).ToArray();

        await GivenCompletedJob(Start);
        _workspaces.AddOrphan(JobId.New(), "jobs/verwaist", Start);

        for (var i = 0; i < 200; i++)
        {
            _clock.SetUtcNow(Start.AddHours(i + 1));
            await Sut().ExecuteAsync();
        }

        voices.StoredCount.ShouldBe(5);
        kept.ShouldAllBe(id => voices.MasterExists(id));
        _workspaces.DeletedLocations.ShouldAllBe(location => location.StartsWith("jobs/"));
    }
}

public class RecoverInterruptedJobsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryConversionJobRepository _jobs = new();
    private readonly FakeJobWorkspaceStore _workspaces = new();
    private readonly FakeJobQueue _queue = new();
    private readonly FakeOrphanProcessKiller _killer = new();
    private readonly FakeServiceInstance _instance = new();
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly FakeJobNotifier _notifier = new();

    private RecoverInterruptedJobs Sut() => new(
        _jobs, _workspaces, _queue, _killer, _instance, _notifier, _clock,
        NullLogger<RecoverInterruptedJobs>.Instance);

    [Fact]
    public async Task Ein_wartender_Auftrag_wird_nach_dem_Neustart_erneut_eingereiht()
    {
        var job = ConversionJob.Create(
            JobId.New(), VoiceId.New(), "Anna", ConversionOptions.Default, Now, Guid.NewGuid());
        await _jobs.SaveAsync(job);

        var report = await Sut().ExecuteAsync();

        report.Requeued.ShouldBe(1);
        _queue.Enqueued.ShouldHaveSingleItem().ShouldBe(job.Id);
    }

    [Fact]
    public async Task Ein_laufender_Auftrag_aus_einem_frueheren_Prozess_gilt_als_unterbrochen()
    {
        var previousRun = Guid.NewGuid();
        var job = ConversionJob.Create(
            JobId.New(), VoiceId.New(), "Anna", ConversionOptions.Default, Now, previousRun);
        job.Start(Now, previousRun);
        await _jobs.SaveAsync(job);

        var report = await Sut().ExecuteAsync();

        report.Interrupted.ShouldBe(1);
        var stored = await _jobs.FindAsync(job.Id);
        stored!.Status.ShouldBe(JobStatus.Failed);
        stored.Error!.Code.ShouldBe(ConversionErrorCode.Interrupted);
        stored.ArtifactsPurged.ShouldBeTrue();
    }

    [Fact]
    public async Task Ein_unterbrochener_Auftrag_meldet_sich_an_seiner_Adresse()
    {
        var previousRun = Guid.NewGuid();
        var job = ConversionJob.Create(
            JobId.New(), VoiceId.New(), "Anna", ConversionOptions.Default, Now, previousRun,
            webhookUrl: WebhookUrl.Rehydrate("https://yue.example/hook"));
        job.Start(Now, previousRun);
        await _jobs.SaveAsync(job);

        await Sut().ExecuteAsync();

        var sent = _notifier.Sent.ShouldHaveSingleItem();
        sent.Job.Status.ShouldBe(JobStatus.Failed);
        sent.Job.Error!.Code.ShouldBe(ConversionErrorCode.Interrupted);
    }

    [Fact]
    public async Task Ein_erneut_eingereihter_Auftrag_meldet_sich_noch_nicht()
    {
        var job = ConversionJob.Create(
            JobId.New(), VoiceId.New(), "Anna", ConversionOptions.Default, Now, Guid.NewGuid(),
            webhookUrl: WebhookUrl.Rehydrate("https://yue.example/hook"));
        await _jobs.SaveAsync(job);

        await Sut().ExecuteAsync();

        _notifier.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Ein_unterbrochener_Auftrag_wird_bewusst_nicht_wiederholt()
    {
        // Ein Auftrag, der den Dienst mitgerissen hat, wuerde sonst bei jedem
        // Start erneut zuschlagen.
        var previousRun = Guid.NewGuid();
        var job = ConversionJob.Create(
            JobId.New(), VoiceId.New(), "Anna", ConversionOptions.Default, Now, previousRun);
        job.Start(Now, previousRun);
        await _jobs.SaveAsync(job);

        await Sut().ExecuteAsync();

        _queue.Enqueued.ShouldBeEmpty();
    }

    [Fact]
    public async Task Ein_zurueckgebliebener_Python_Prozess_wird_beendet()
    {
        var previousRun = Guid.NewGuid();
        var job = ConversionJob.Create(
            JobId.New(), VoiceId.New(), "Anna", ConversionOptions.Default, Now, previousRun);
        job.Start(Now, previousRun);
        job.AttachInferenceProcess(4711);
        await _jobs.SaveAsync(job);

        var report = await Sut().ExecuteAsync();

        _killer.Requested.ShouldHaveSingleItem().ShouldBe(4711);
        report.KilledProcesses.ShouldBe(1);
    }

    [Fact]
    public async Task Abgeschlossene_Auftraege_bleiben_unberuehrt()
    {
        var job = ConversionJob.Create(
            JobId.New(), VoiceId.New(), "Anna", ConversionOptions.Default, Now, Guid.NewGuid());
        job.Start(Now, Guid.NewGuid());
        job.Complete(Now, 10, "sha");
        await _jobs.SaveAsync(job);

        var report = await Sut().ExecuteAsync();

        report.ShouldBe(new RecoveryReport(0, 0, 0));
        (await _jobs.FindAsync(job.Id))!.Status.ShouldBe(JobStatus.Completed);
    }
}

public class ApplicationArchitectureTests
{
    [Fact]
    public void Die_Anwendungsschicht_kennt_kein_Dateisystem_und_keine_Prozesse()
    {
        // Technik gehoert in einen Adapter. Ein direkter File- oder
        // Process-Zugriff hier waere ein Bruch der Ports-and-Adapters-Trennung.
        var assembly = typeof(CleanupJobArtifacts).Assembly;

        var forbidden = assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name =>
                name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) ||
                name.StartsWith("Microsoft.Data", StringComparison.Ordinal) ||
                name.StartsWith("Dapper", StringComparison.Ordinal) ||
                name.StartsWith("Yarp", StringComparison.Ordinal))
            .ToArray();

        forbidden.ShouldBeEmpty(
            "Infrastruktur gehört nicht in die Anwendungsschicht, gefunden: "
            + string.Join(", ", forbidden));
    }
}
