using ChangeMyVoice.Application.Ports;
using ChangeMyVoice.Domain.Jobs;
using Microsoft.Extensions.Logging;

namespace ChangeMyVoice.Application.UseCases.Maintenance;

/// <summary>Was ein Aufräumlauf bewirkt hat.</summary>
/// <param name="PurgedJobs">Aufträge, deren Dateien nach Fristablauf entfernt wurden.</param>
/// <param name="TimedOutJobs">Aufträge, die wegen Zeitüberschreitung beendet wurden.</param>
/// <param name="OrphanedWorkspaces">Verzeichnisse ohne zugehörigen Datensatz.</param>
/// <param name="TemporaryFiles">Reste aus abgebrochenen Uploads.</param>
public sealed record CleanupReport(
    int PurgedJobs,
    int TimedOutJobs,
    int OrphanedWorkspaces,
    int TemporaryFiles)
{
    /// <summary>Ob überhaupt etwas zu tun war.</summary>
    public bool DidAnything =>
        PurgedJobs + TimedOutJobs + OrphanedWorkspaces + TemporaryFiles > 0;
}

/// <summary>Räumt liegengebliebene Arbeitsdateien weg.</summary>
public interface ICleanupJobArtifacts
{
    /// <summary>Führt einen Aufräumlauf aus.</summary>
    Task<CleanupReport> ExecuteAsync(CancellationToken cancellationToken = default);
}

/// <summary>Einstellungen des Aufräumlaufs.</summary>
public sealed class CleanupSettings
{
    /// <summary>Die Aufbewahrungsfristen.</summary>
    public JobRetention Retention { get; set; } = JobRetention.Default;

    /// <summary>
    /// Nach welcher Zeit ein noch laufender Auftrag als hängend gilt. Sollte
    /// über dem Zeitlimit der Inferenz liegen.
    /// </summary>
    public TimeSpan StuckJobThreshold { get; set; } = TimeSpan.FromMinutes(45);
}

/// <inheritdoc />
/// <remarks>
/// Bewusst ein Anwendungsfall und kein Hintergrunddienst: Nur so lässt sich die
/// Aufräumregel mit einer gestellten Uhr prüfen, ohne den Host zu starten.
/// Referenzstimmen kommen hier an keiner Stelle vor — sie werden niemals
/// automatisch entfernt.
/// </remarks>
public sealed class CleanupJobArtifacts(
    IConversionJobRepository jobs,
    IJobWorkspaceStore workspaces,
    CleanupSettings settings,
    IJobNotifier notifier,
    TimeProvider clock,
    ILogger<CleanupJobArtifacts> logger) : ICleanupJobArtifacts
{
    /// <inheritdoc />
    public async Task<CleanupReport> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var known = await jobs.ListUnpurgedAsync(cancellationToken).ConfigureAwait(false);

        var purged = 0;
        var timedOut = 0;

        foreach (var job in known)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Ein Auftrag, der viel zu lange läuft, hat entweder seinen Prozess
            // verloren oder hängt. Beides endet hier, damit er nicht dauerhaft
            // als laufend geführt wird und Platz belegt.
            if (job.Status == JobStatus.Running &&
                job.StartedAtUtc is { } started &&
                now - started >= settings.StuckJobThreshold)
            {
                job.Fail(now, new JobError(
                    ConversionErrorCode.Timeout,
                    "Der Auftrag hat das erlaubte Zeitfenster überschritten."));
                await workspaces.DeleteAsync(job.Id, cancellationToken).ConfigureAwait(false);
                job.MarkArtifactsPurged();
                await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false);
                notifier.NotifyFinished(job);

                timedOut++;
                logger.LogWarning("Auftrag {JobId} wegen Zeitüberschreitung beendet.", job.Id);
                continue;
            }

            if (!JobRetentionPolicy.ShouldPurge(job, now, settings.Retention))
            {
                continue;
            }

            await workspaces.DeleteAsync(job.Id, cancellationToken).ConfigureAwait(false);
            job.MarkArtifactsPurged();
            await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false);

            purged++;
        }

        var orphans = await PurgeOrphanedWorkspacesAsync(now, cancellationToken).ConfigureAwait(false);

        var temporaries = await workspaces
            .PurgeTemporaryAsync(now - settings.Retention.Orphan, cancellationToken)
            .ConfigureAwait(false);

        var report = new CleanupReport(purged, timedOut, orphans, temporaries);

        if (report.DidAnything)
        {
            logger.LogInformation(
                "Aufräumlauf: {Purged} abgelaufen, {TimedOut} überfällig, "
                + "{Orphans} verwaist, {Temporary} Zwischendateien.",
                report.PurgedJobs, report.TimedOutJobs,
                report.OrphanedWorkspaces, report.TemporaryFiles);
        }

        return report;
    }

    /// <summary>
    /// Entfernt Verzeichnisse, zu denen es keinen Datensatz mehr gibt. Genau das
    /// bleibt nach einem harten Absturz zurück, bei dem kein Abschlusscode mehr
    /// gelaufen ist.
    /// </summary>
    private async Task<int> PurgeOrphanedWorkspacesAsync(
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var removed = 0;

        foreach (var entry in workspaces.Enumerate())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!JobRetentionPolicy.ShouldPurgeOrphan(
                    entry.CreatedAtUtc, now, settings.Retention))
            {
                continue;
            }

            // Ein unlesbarer Verzeichnisname gehört ebenso weg wie ein
            // Verzeichnis, dessen Auftrag nicht mehr existiert.
            if (entry.JobId is not { } jobId)
            {
                await workspaces.DeleteAsync(entry, cancellationToken).ConfigureAwait(false);
                removed++;
                continue;
            }

            if (await jobs.FindAsync(jobId, cancellationToken).ConfigureAwait(false) is not null)
            {
                continue;
            }

            await workspaces.DeleteAsync(jobId, cancellationToken).ConfigureAwait(false);
            removed++;
        }

        return removed;
    }
}
