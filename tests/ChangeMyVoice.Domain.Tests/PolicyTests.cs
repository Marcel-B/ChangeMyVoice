using ChangeMyVoice.Domain.Jobs;
using ChangeMyVoice.Domain.Voices;
using Shouldly;

namespace ChangeMyVoice.Domain.Tests;

public class JobRetentionPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Instance = Guid.NewGuid();

    private static ConversionJob Completed(DateTimeOffset finishedAt)
    {
        var job = ConversionJob.Create(
            JobId.New(), VoiceId.New(), "x", ConversionOptions.Default, finishedAt, Instance);
        job.Start(finishedAt, Instance);
        job.Complete(finishedAt, 1, "sha");
        return job;
    }

    [Fact]
    public void Ein_laufender_Auftrag_wird_nicht_aufgeraeumt()
    {
        var job = ConversionJob.Create(
            JobId.New(), VoiceId.New(), "x", ConversionOptions.Default, Now, Instance);
        job.Start(Now, Instance);

        JobRetentionPolicy.ShouldPurge(job, Now.AddDays(7)).ShouldBeFalse();
    }

    [Fact]
    public void Ein_frisch_abgeschlossener_Auftrag_bleibt_zunaechst_liegen()
    {
        var job = Completed(Now);

        JobRetentionPolicy.ShouldPurge(job, Now.AddHours(1)).ShouldBeFalse();
    }

    [Fact]
    public void Nach_Ablauf_der_Frist_wird_ein_abgeschlossener_Auftrag_aufgeraeumt()
    {
        var job = Completed(Now);

        JobRetentionPolicy.ShouldPurge(job, Now.AddHours(25)).ShouldBeTrue();
    }

    [Fact]
    public void Ein_abgeholtes_Ergebnis_wird_frueher_aufgeraeumt()
    {
        var job = Completed(Now);
        job.MarkDownloaded(Now.AddMinutes(5));

        JobRetentionPolicy.ShouldPurge(job, Now.AddHours(2)).ShouldBeTrue();
    }

    [Fact]
    public void Direkt_nach_dem_Abruf_bleibt_das_Ergebnis_fuer_Wiederholungen_erhalten()
    {
        var job = Completed(Now);
        job.MarkDownloaded(Now.AddMinutes(5));

        JobRetentionPolicy.ShouldPurge(job, Now.AddMinutes(10)).ShouldBeFalse();
    }

    [Fact]
    public void Fehlgeschlagene_Auftraege_bleiben_zur_Fehlersuche_laenger_erhalten()
    {
        var job = ConversionJob.Create(
            JobId.New(), VoiceId.New(), "x", ConversionOptions.Default, Now, Instance);
        job.Fail(Now, new JobError(ConversionErrorCode.InferenceFailed, "kaputt"));

        JobRetentionPolicy.ShouldPurge(job, Now.AddHours(25)).ShouldBeFalse();
        JobRetentionPolicy.ShouldPurge(job, Now.AddHours(73)).ShouldBeTrue();
    }

    [Fact]
    public void Bereits_aufgeraeumte_Auftraege_werden_nicht_erneut_angefasst()
    {
        var job = Completed(Now);
        job.MarkArtifactsPurged();

        JobRetentionPolicy.ShouldPurge(job, Now.AddDays(30)).ShouldBeFalse();
    }

    [Fact]
    public void Ein_frisches_verwaistes_Verzeichnis_geniesst_eine_Schonfrist()
    {
        // Sonst würde ein Verzeichnis gelöscht, das gerade erst angelegt, aber
        // noch nicht gespeichert wurde.
        JobRetentionPolicy.ShouldPurgeOrphan(Now, Now.AddMinutes(5)).ShouldBeFalse();
        JobRetentionPolicy.ShouldPurgeOrphan(Now, Now.AddHours(2)).ShouldBeTrue();
    }
}

public class JobRecoveryPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Ein_wartender_Auftrag_wird_erneut_eingereiht()
    {
        var job = ConversionJob.Create(
            JobId.New(), VoiceId.New(), "x", ConversionOptions.Default, Now, Guid.NewGuid());

        JobRecoveryPolicy.Decide(job, Guid.NewGuid()).ShouldBe(JobRecoveryAction.Requeue);
    }

    [Fact]
    public void Ein_laufender_Auftrag_aus_einem_frueheren_Prozess_gilt_als_unterbrochen()
    {
        var previousRun = Guid.NewGuid();
        var job = ConversionJob.Create(
            JobId.New(), VoiceId.New(), "x", ConversionOptions.Default, Now, previousRun);
        job.Start(Now, previousRun);

        JobRecoveryPolicy.Decide(job, Guid.NewGuid())
            .ShouldBe(JobRecoveryAction.FailAsInterrupted);
    }

    [Fact]
    public void Ein_laufender_Auftrag_des_aktuellen_Prozesses_bleibt_unangetastet()
    {
        var current = Guid.NewGuid();
        var job = ConversionJob.Create(
            JobId.New(), VoiceId.New(), "x", ConversionOptions.Default, Now, current);
        job.Start(Now, current);

        JobRecoveryPolicy.Decide(job, current).ShouldBe(JobRecoveryAction.Leave);
    }

    [Fact]
    public void Abgeschlossene_Auftraege_werden_bei_der_Wiederherstellung_nicht_angefasst()
    {
        var run = Guid.NewGuid();
        var job = ConversionJob.Create(
            JobId.New(), VoiceId.New(), "x", ConversionOptions.Default, Now, run);
        job.Start(Now, run);
        job.Complete(Now, 1, "sha");

        JobRecoveryPolicy.Decide(job, Guid.NewGuid()).ShouldBe(JobRecoveryAction.Leave);
    }
}

public class VoiceLabelTests
{
    [Fact]
    public void Leerraum_am_Rand_wird_entfernt()
    {
        VoiceLabel.TryCreate("  Anna  ", out var label, out _).ShouldBeTrue();

        label.Value.ShouldBe("Anna");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Leere_Bezeichnungen_werden_abgelehnt(string? input)
    {
        VoiceLabel.TryCreate(input, out _, out var error).ShouldBeFalse();

        error.ShouldNotBeNull();
    }

    [Fact]
    public void Zu_lange_Bezeichnungen_werden_abgelehnt()
    {
        VoiceLabel.TryCreate(new string('a', 101), out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void Steuerzeichen_werden_abgelehnt()
    {
        VoiceLabel.TryCreate("An\tna", out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void Die_Schreibweise_entscheidet_nicht_ueber_die_Gleichheit()
    {
        // Damit nicht "Anna" und "anna" nebeneinander in der Übersicht stehen.
        var a = VoiceLabel.Create("Anna");
        var b = VoiceLabel.Create("ANNA");

        a.ShouldBe(b);
        a.ComparisonKey.ShouldBe(b.ComparisonKey);
    }
}
