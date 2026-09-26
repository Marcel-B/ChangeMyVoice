using ChangeMyVoice.Application.Common;
using ChangeMyVoice.Application.Ports;
using ChangeMyVoice.Domain.Jobs;

namespace ChangeMyVoice.Application.UseCases.Jobs;

/// <summary>Fragt den Zustand eines Auftrags ab.</summary>
public interface IGetJobStatus
{
    /// <summary>Führt den Anwendungsfall aus.</summary>
    Task<Result<ConversionJobView>> ExecuteAsync(
        JobId id, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class GetJobStatus(IConversionJobRepository jobs) : IGetJobStatus
{
    /// <inheritdoc />
    public async Task<Result<ConversionJobView>> ExecuteAsync(
        JobId id, CancellationToken cancellationToken = default)
    {
        var job = await jobs.FindAsync(id, cancellationToken).ConfigureAwait(false);

        return job is null
            ? Result<ConversionJobView>.Failure(
                OperationErrorCode.JobNotFound, $"Es gibt keinen Auftrag mit der Kennung '{id}'.")
            : Result<ConversionJobView>.Success(ConversionJobView.From(job));
    }
}

/// <summary>Das abrufbare Ergebnis eines Auftrags.</summary>
/// <param name="Content">Der Datenstrom der Ergebnisdatei.</param>
/// <param name="FileName">Ein sprechender Dateiname für den Abruf.</param>
/// <param name="SizeBytes">Die Größe.</param>
/// <param name="Sha256">Die Prüfsumme, als Wert für den Abgleich.</param>
public sealed record JobResult(Stream Content, string FileName, long? SizeBytes, string? Sha256);

/// <summary>Liefert das Ergebnis eines Auftrags.</summary>
public interface IGetJobResult
{
    /// <summary>Führt den Anwendungsfall aus.</summary>
    Task<Result<JobResult>> ExecuteAsync(JobId id, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class GetJobResult(
    IConversionJobRepository jobs,
    IJobWorkspaceStore workspaces,
    TimeProvider clock) : IGetJobResult
{
    /// <inheritdoc />
    public async Task<Result<JobResult>> ExecuteAsync(
        JobId id, CancellationToken cancellationToken = default)
    {
        var job = await jobs.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return Result<JobResult>.Failure(
                OperationErrorCode.JobNotFound, $"Es gibt keinen Auftrag mit der Kennung '{id}'.");
        }

        if (job.Status is JobStatus.Queued or JobStatus.Running)
        {
            return Result<JobResult>.Failure(
                OperationErrorCode.ResultNotReady,
                $"Der Auftrag ist noch nicht fertig, aktueller Zustand: {job.Status}.");
        }

        if (job.Status != JobStatus.Completed)
        {
            return Result<JobResult>.Failure(
                OperationErrorCode.ResultNotReady,
                job.Error is null
                    ? $"Der Auftrag endete im Zustand {job.Status}."
                    : $"Der Auftrag ist fehlgeschlagen: {job.Error.Message}");
        }

        if (job.ArtifactsPurged)
        {
            return Result<JobResult>.Failure(
                OperationErrorCode.ResultGone,
                "Das Ergebnis wurde bereits aufgeräumt und steht nicht mehr zur Verfügung.");
        }

        var stream = await workspaces.OpenOutputAsync(id, cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            // Der Datensatz sagt „fertig“, die Datei fehlt aber — das kann nach
            // einem Eingriff von außen passieren und soll nicht als 500 enden.
            return Result<JobResult>.Failure(
                OperationErrorCode.ResultGone,
                "Die Ergebnisdatei ist nicht mehr vorhanden.");
        }

        job.MarkDownloaded(clock.GetUtcNow());
        await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false);

        return Result<JobResult>.Success(new JobResult(
            stream, $"{id}.wav", job.OutputSizeBytes, job.OutputSha256));
    }
}

/// <summary>Bricht einen Auftrag ab und räumt seine Dateien weg.</summary>
public interface ICancelJob
{
    /// <summary>Führt den Anwendungsfall aus.</summary>
    Task<Result> ExecuteAsync(JobId id, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class CancelJob(
    IConversionJobRepository jobs,
    IJobWorkspaceStore workspaces,
    IJobNotifier notifier,
    TimeProvider clock) : ICancelJob
{
    /// <inheritdoc />
    public async Task<Result> ExecuteAsync(JobId id, CancellationToken cancellationToken = default)
    {
        var job = await jobs.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return Result.Failure(
                OperationErrorCode.JobNotFound, $"Es gibt keinen Auftrag mit der Kennung '{id}'.");
        }

        // Ein bereits beendeter Auftrag wird nicht erneut abgebrochen; der Aufruf
        // gilt trotzdem als erfolgreich, damit er gefahrlos wiederholbar bleibt.
        var wasRunning = !job.IsTerminal;
        if (wasRunning)
        {
            job.Cancel(clock.GetUtcNow());
        }

        await workspaces.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        job.MarkArtifactsPurged();
        await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false);

        // Nur beim ersten Abbruch melden: Ein wiederholter Aufruf ändert nichts
        // und soll den Empfänger nicht erneut anstoßen.
        if (wasRunning)
        {
            notifier.NotifyFinished(job);
        }

        return Result.Success();
    }
}
