using ChangeMyVoice.Domain.Common;
using ChangeMyVoice.Domain.Jobs;
using ChangeMyVoice.Domain.Voices;
using Shouldly;

namespace ChangeMyVoice.Domain.Tests;

public class ConversionJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Instance = Guid.NewGuid();

    private static ConversionJob NewJob() => ConversionJob.Create(
        JobId.New(), VoiceId.New(), "Testsängerin", ConversionOptions.Default, Now, Instance);

    [Fact]
    public void Ein_neuer_Auftrag_wartet_in_der_Warteschlange()
    {
        var job = NewJob();

        job.Status.ShouldBe(JobStatus.Queued);
        job.IsTerminal.ShouldBeFalse();
        job.IsResultAvailable.ShouldBeFalse();
        job.StartedAtUtc.ShouldBeNull();
    }

    [Fact]
    public void Ein_erfolgreicher_Durchlauf_fuehrt_zum_abrufbaren_Ergebnis()
    {
        var job = NewJob();

        job.Start(Now.AddSeconds(5), Instance);
        job.Status.ShouldBe(JobStatus.Running);

        job.Complete(Now.AddMinutes(2), outputSizeBytes: 4096, outputSha256: "abc");

        job.Status.ShouldBe(JobStatus.Completed);
        job.IsTerminal.ShouldBeTrue();
        job.IsResultAvailable.ShouldBeTrue();
        job.OutputSizeBytes.ShouldBe(4096);
        job.OutputSha256.ShouldBe("abc");
        job.FinishedAtUtc.ShouldBe(Now.AddMinutes(2));
    }

    [Fact]
    public void Ein_Auftrag_kann_nicht_zweimal_gestartet_werden()
    {
        var job = NewJob();
        job.Start(Now, Instance);

        Should.Throw<InvalidJobTransitionException>(() => job.Start(Now, Instance));
    }

    [Fact]
    public void Ein_wartender_Auftrag_kann_nicht_direkt_abgeschlossen_werden()
    {
        var job = NewJob();

        Should.Throw<InvalidJobTransitionException>(() => job.Complete(Now, 1, "x"));
    }

    [Fact]
    public void Ein_abgeschlossener_Auftrag_kann_nicht_mehr_fehlschlagen()
    {
        var job = NewJob();
        job.Start(Now, Instance);
        job.Complete(Now, 1, "x");

        Should.Throw<InvalidJobTransitionException>(
            () => job.Fail(Now, new JobError(ConversionErrorCode.InferenceFailed, "zu spät")));
    }

    [Fact]
    public void Ein_abgeschlossener_Auftrag_kann_nicht_mehr_abgebrochen_werden()
    {
        var job = NewJob();
        job.Start(Now, Instance);
        job.Complete(Now, 1, "x");

        Should.Throw<InvalidJobTransitionException>(() => job.Cancel(Now));
    }

    [Fact]
    public void Ein_wartender_Auftrag_darf_ohne_Start_fehlschlagen()
    {
        var job = NewJob();

        job.Fail(Now, new JobError(ConversionErrorCode.Interrupted, "Neustart"));

        job.Status.ShouldBe(JobStatus.Failed);
        job.Error!.Code.ShouldBe(ConversionErrorCode.Interrupted);
    }

    [Fact]
    public void Ein_wartender_Auftrag_darf_abgebrochen_werden()
    {
        var job = NewJob();

        job.Cancel(Now);

        job.Status.ShouldBe(JobStatus.Cancelled);
        job.IsTerminal.ShouldBeTrue();
    }

    [Fact]
    public void Das_Fehlschlagen_loest_die_Prozessverknuepfung()
    {
        var job = NewJob();
        job.Start(Now, Instance);
        job.AttachInferenceProcess(4711);
        job.InferenceProcessId.ShouldBe(4711);

        job.Fail(Now, new JobError(ConversionErrorCode.Timeout, "zu lang"));

        job.InferenceProcessId.ShouldBeNull();
    }

    [Fact]
    public void Nur_der_erste_Abruf_setzt_den_Abholzeitpunkt()
    {
        // Sonst würde jeder wiederholte Abruf die Aufbewahrungsfrist verlängern
        // und die Dateien blieben unbegrenzt liegen.
        var job = NewJob();
        job.Start(Now, Instance);
        job.Complete(Now, 1, "x");

        job.MarkDownloaded(Now.AddMinutes(1));
        job.MarkDownloaded(Now.AddHours(5));

        job.DownloadedAtUtc.ShouldBe(Now.AddMinutes(1));
    }

    [Fact]
    public void Ein_nicht_abgeschlossener_Auftrag_kann_nicht_als_abgeholt_gelten()
    {
        var job = NewJob();
        job.Start(Now, Instance);

        Should.Throw<InvalidJobTransitionException>(() => job.MarkDownloaded(Now));
    }

    [Fact]
    public void Aufgeraeumte_Dateien_machen_das_Ergebnis_unerreichbar()
    {
        var job = NewJob();
        job.Start(Now, Instance);
        job.Complete(Now, 1, "x");

        job.MarkArtifactsPurged();

        job.Status.ShouldBe(JobStatus.Completed);
        job.IsResultAvailable.ShouldBeFalse();
    }
}
