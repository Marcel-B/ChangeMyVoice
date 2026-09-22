using ChangeMyVoice.Application.Ports;
using ChangeMyVoice.Domain.Jobs;
using Microsoft.Extensions.Logging;

namespace ChangeMyVoice.Application.UseCases.Maintenance;

/// <summary>Beendet Prozesse, die ein früherer Dienstlauf zurückgelassen hat.</summary>
/// <remarks>
/// Stürzt der Dienst ab, während eine Inferenz läuft, bleibt der Python-Prozess
/// als Waise zurück und belegt weiter die GPU. Er lässt sich nur über die
/// gespeicherte Prozesskennung wiederfinden.
/// </remarks>
public interface IOrphanProcessKiller
{
    /// <summary>
    /// Beendet den Prozessbaum, falls die Kennung noch zu einem Prozess dieses
    /// Dienstes gehört. Liefert <c>true</c>, wenn tatsächlich etwas beendet wurde.
    /// </summary>
    bool TryKill(int processId);
}

/// <summary>Was die Wiederherstellung beim Start bewirkt hat.</summary>
/// <param name="Requeued">Wartende Aufträge, die erneut eingereiht wurden.</param>
/// <param name="Interrupted">Laufende Aufträge aus einem früheren Prozess.</param>
/// <param name="KilledProcesses">Beendete zurückgebliebene Prozesse.</param>
public sealed record RecoveryReport(int Requeued, int Interrupted, int KilledProcesses);

/// <summary>Bringt die Aufträge nach einem Neustart in einen stimmigen Zustand.</summary>
public interface IRecoverInterruptedJobs
{
    /// <summary>Führt die Wiederherstellung aus.</summary>
    Task<RecoveryReport> ExecuteAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class RecoverInterruptedJobs(
    IConversionJobRepository jobs,
    IJobWorkspaceStore workspaces,
    IJobQueue queue,
    IOrphanProcessKiller processKiller,
    IServiceInstance instance,
    TimeProvider clock,
    ILogger<RecoverInterruptedJobs> logger) : IRecoverInterruptedJobs
{
    /// <inheritdoc />
    public async Task<RecoveryReport> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var all = await jobs.ListUnpurgedAsync(cancellationToken).ConfigureAwait(false);

        var requeued = 0;
        var interrupted = 0;
        var killed = 0;

        foreach (var job in all)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (JobRecoveryPolicy.Decide(job, instance.InstanceId))
            {
                case JobRecoveryAction.Requeue:
                    // Quelle und Referenz liegen vollständig im Arbeitsverzeichnis,
                    // ein erneuter Anlauf ist gefahrlos.
                    if (queue.TryEnqueue(job.Id))
                    {
                        requeued++;
                    }
                    else
                    {
                        job.Fail(now, new JobError(
                            ConversionErrorCode.Interrupted,
                            "Nach dem Neustart war kein Platz in der Warteschlange."));
                        await workspaces.DeleteAsync(job.Id, cancellationToken).ConfigureAwait(false);
                        job.MarkArtifactsPurged();
                        await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false);
                    }

                    break;

                case JobRecoveryAction.FailAsInterrupted:
                    if (job.InferenceProcessId is { } pid && processKiller.TryKill(pid))
                    {
                        killed++;
                        logger.LogWarning(
                            "Zurückgebliebener Inferenzprozess {ProcessId} von Auftrag {JobId} beendet.",
                            pid, job.Id);
                    }

                    // Kein automatischer Neuversuch: Ein Auftrag, der den Dienst
                    // mitgerissen hat, etwa durch Speichermangel, würde sonst bei
                    // jedem Start erneut zuschlagen.
                    job.Fail(now, new JobError(
                        ConversionErrorCode.Interrupted,
                        "Der Auftrag wurde durch einen Neustart des Dienstes unterbrochen."));
                    await workspaces.DeleteAsync(job.Id, cancellationToken).ConfigureAwait(false);
                    job.MarkArtifactsPurged();
                    await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false);

                    interrupted++;
                    break;

                case JobRecoveryAction.Leave:
                default:
                    break;
            }
        }

        if (requeued + interrupted + killed > 0)
        {
            logger.LogInformation(
                "Wiederherstellung: {Requeued} erneut eingereiht, {Interrupted} als unterbrochen "
                + "beendet, {Killed} Prozesse aufgeräumt.",
                requeued, interrupted, killed);
        }

        return new RecoveryReport(requeued, interrupted, killed);
    }
}
