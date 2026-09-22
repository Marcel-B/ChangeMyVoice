using ChangeMyVoice.Application.Tests.Fakes;
using ChangeMyVoice.Application.UseCases.Jobs;
using ChangeMyVoice.Domain.Jobs;
using ChangeMyVoice.Domain.Voices;
using Shouldly;

namespace ChangeMyVoice.Application.Tests;

public class ListConversionJobsTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryConversionJobRepository _jobs = new();

    private ListConversionJobs Sut() => new(_jobs);

    private async Task<ConversionJob> GivenJob(int minuteOffset, JobStatus status = JobStatus.Queued)
    {
        var at = Start.AddMinutes(minuteOffset);
        var job = ConversionJob.Create(
            JobId.New(), VoiceId.New(), "Anna", ConversionOptions.Default, at, Guid.NewGuid());

        switch (status)
        {
            case JobStatus.Running:
                job.Start(at, Guid.NewGuid());
                break;
            case JobStatus.Completed:
                job.Start(at, Guid.NewGuid());
                job.Complete(at, 10, "sha");
                break;
            case JobStatus.Failed:
                job.Fail(at, new JobError(ConversionErrorCode.InferenceFailed, "kaputt"));
                break;
            case JobStatus.Cancelled:
                job.Cancel(at);
                break;
        }

        await _jobs.SaveAsync(job);
        return job;
    }

    [Fact]
    public async Task Die_juengsten_Auftraege_stehen_zuoberst()
    {
        // Eine Verwaltungsoberflaeche zeigt zuoberst, was gerade passiert ist.
        await GivenJob(0);
        var mittlerer = await GivenJob(10);
        var neuester = await GivenJob(20);

        var result = await Sut().ExecuteAsync(new ListConversionJobsQuery(null, 50, 0));

        var items = result.Value!.Items;
        items[0].Id.ShouldBe(neuester.Id);
        items[1].Id.ShouldBe(mittlerer.Id);
        items.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Die_Gesamtzahl_wird_unabhaengig_von_der_Seite_gemeldet()
    {
        // Ohne sie koennte eine Oberflaeche keine Blaetterung anzeigen.
        for (var i = 0; i < 7; i++)
        {
            await GivenJob(i);
        }

        var result = await Sut().ExecuteAsync(new ListConversionJobsQuery(null, 3, 0));

        result.Value!.Items.Count.ShouldBe(3);
        result.Value.Total.ShouldBe(7);
    }

    [Fact]
    public async Task Der_Versatz_blaettert_weiter()
    {
        for (var i = 0; i < 5; i++)
        {
            await GivenJob(i);
        }

        var erste = await Sut().ExecuteAsync(new ListConversionJobsQuery(null, 2, 0));
        var zweite = await Sut().ExecuteAsync(new ListConversionJobsQuery(null, 2, 2));

        erste.Value!.Items.Select(i => i.Id)
            .ShouldNotBe(zweite.Value!.Items.Select(i => i.Id));
        zweite.Value.Offset.ShouldBe(2);
    }

    [Fact]
    public async Task Der_Filter_schraenkt_auf_einen_Zustand_ein()
    {
        await GivenJob(0, JobStatus.Completed);
        await GivenJob(1, JobStatus.Completed);
        await GivenJob(2, JobStatus.Failed);

        var result = await Sut().ExecuteAsync(
            new ListConversionJobsQuery(JobStatus.Completed, 50, 0));

        result.Value!.Items.Count.ShouldBe(2);
        result.Value.Total.ShouldBe(2);
        result.Value.Items.ShouldAllBe(i => i.Status == JobStatus.Completed);
    }

    [Fact]
    public async Task Ohne_Auftraege_kommt_eine_leere_Seite()
    {
        var result = await Sut().ExecuteAsync(new ListConversionJobsQuery(null, 50, 0));

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Items.ShouldBeEmpty();
        result.Value.Total.ShouldBe(0);
    }

    [Fact]
    public async Task Aufgeraeumte_Auftraege_bleiben_in_der_Uebersicht()
    {
        // Ihre Dateien sind weg, der Vorgang bleibt aber nachvollziehbar.
        var job = await GivenJob(0, JobStatus.Completed);
        job.MarkArtifactsPurged();
        await _jobs.SaveAsync(job);

        var result = await Sut().ExecuteAsync(new ListConversionJobsQuery(null, 50, 0));

        var item = result.Value!.Items.ShouldHaveSingleItem();
        item.Status.ShouldBe(JobStatus.Completed);
        item.IsResultAvailable.ShouldBeFalse();
    }
}

public class ListConversionJobsQueryTests
{
    [Fact]
    public void Ohne_Angaben_gelten_die_Voreinstellungen()
    {
        ListConversionJobsQuery.TryCreate(null, null, null, out var query, out _).ShouldBeTrue();

        query.Status.ShouldBeNull();
        query.Limit.ShouldBe(ListConversionJobsQuery.DefaultLimit);
        query.Offset.ShouldBe(0);
    }

    [Theory]
    [InlineData("completed", JobStatus.Completed)]
    [InlineData("RUNNING", JobStatus.Running)]
    [InlineData("Failed", JobStatus.Failed)]
    public void Der_Zustand_wird_unabhaengig_von_der_Schreibweise_erkannt(
        string input, JobStatus expected)
    {
        ListConversionJobsQuery.TryCreate(input, null, null, out var query, out _).ShouldBeTrue();

        query.Status.ShouldBe(expected);
    }

    [Fact]
    public void Ein_unbekannter_Zustand_wird_abgelehnt_und_nennt_die_erlaubten()
    {
        ListConversionJobsQuery.TryCreate("irgendwas", null, null, out _, out var error)
            .ShouldBeFalse();

        error.ShouldNotBeNull();
        error.ShouldContain("COMPLETED");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    [InlineData(-5)]
    public void Unsinnige_Seitengroessen_werden_abgelehnt(int limit)
    {
        // Die Obergrenze schuetzt davor, dass eine Oberflaeche versehentlich
        // den gesamten Bestand auf einmal anfordert.
        ListConversionJobsQuery.TryCreate(null, limit, null, out _, out var error).ShouldBeFalse();

        error.ShouldNotBeNull();
    }

    [Fact]
    public void Ein_negativer_Versatz_wird_abgelehnt()
    {
        ListConversionJobsQuery.TryCreate(null, null, -1, out _, out var error).ShouldBeFalse();

        error.ShouldNotBeNull();
    }
}
