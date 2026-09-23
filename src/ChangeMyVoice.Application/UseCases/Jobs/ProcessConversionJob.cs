using System.Security.Cryptography;
using ChangeMyVoice.Application.Ports;
using ChangeMyVoice.Domain.Jobs;
using Microsoft.Extensions.Logging;

namespace ChangeMyVoice.Application.UseCases.Jobs;

/// <summary>Führt einen wartenden Auftrag aus.</summary>
public interface IProcessConversionJob
{
    /// <summary>Führt den Anwendungsfall aus.</summary>
    Task ExecuteAsync(JobId id, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class ProcessConversionJob(
    IConversionJobRepository jobs,
    IJobWorkspaceStore workspaces,
    IVoiceConversionEngine engine,
    IAudioNormalizer normalizer,
    IServiceInstance instance,
    TimeProvider clock,
    ILogger<ProcessConversionJob> logger) : IProcessConversionJob
{
    /// <inheritdoc />
    public async Task ExecuteAsync(JobId id, CancellationToken cancellationToken = default)
    {
        var job = await jobs.FindAsync(id, cancellationToken).ConfigureAwait(false);

        if (job is null)
        {
            logger.LogWarning("Auftrag {JobId} steht in der Warteschlange, ist aber nicht gespeichert.", id);
            return;
        }

        if (job.Status != JobStatus.Queued)
        {
            // Etwa nach einem Abbruch, der den Auftrag bereits beendet hat.
            logger.LogInformation(
                "Auftrag {JobId} wird übersprungen, Zustand ist {Status}.", id, job.Status);
            return;
        }

        job.Start(clock.GetUtcNow(), instance.InstanceId);
        await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false);

        var workspace = workspaces.Get(id);

        try
        {
            var outcome = await engine.ConvertAsync(
                new ConversionRequest(
                    workspace.Source, workspace.Reference, workspace.RawOutput, job.Options),
                onProcessStarted: pid =>
                {
                    job.AttachInferenceProcess(pid);
                    // Absichtlich synchron weggeschrieben: Stürzt der Dienst
                    // gleich darauf ab, muss die Prozesskennung bereits in der
                    // Datenbank stehen, sonst bleibt beim Neustart ein
                    // verwaister Python-Prozess zurück.
                    jobs.SaveAsync(job, CancellationToken.None).GetAwaiter().GetResult();
                },
                cancellationToken).ConfigureAwait(false);

            if (!outcome.IsSuccess)
            {
                job.Fail(clock.GetUtcNow(), outcome.Error!);
                await jobs.SaveAsync(job, CancellationToken.None).ConfigureAwait(false);

                logger.LogWarning(
                    "Auftrag {JobId} fehlgeschlagen: {Code} — {Message}",
                    id, outcome.Error!.Code, outcome.Error.Message);
                return;
            }

            // Das Modell liefert seine eigene Abtastrate; gefragt ist die, mit
            // der das Zielprojekt arbeitet. Die Umrechnung hier erspart sie von
            // Hand -- und ohne sie muesste dasselbe Resampling ohnehin
            // ausserhalb passieren.
            await normalizer.NormalizeAsync(
                workspace.RawOutput,
                workspace.Output,
                job.Options.DeliveryFormat,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);

            var (size, sha) = await MeasureOutputAsync(id).ConfigureAwait(false);

            if (size is null)
            {
                job.Fail(clock.GetUtcNow(), new JobError(
                    ConversionErrorCode.OutputNotCreated,
                    "Der Lauf meldete Erfolg, es entstand aber keine Ausgabedatei."));
                await jobs.SaveAsync(job, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            job.Complete(clock.GetUtcNow(), size.Value, sha!);
            await jobs.SaveAsync(job, CancellationToken.None).ConfigureAwait(false);

            logger.LogInformation(
                "Auftrag {JobId} abgeschlossen: {Bytes} Bytes, Modellladezeit {ModelLoad}, Inferenz {Inference}.",
                id, size, outcome.ModelLoadDuration, outcome.InferenceDuration);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Der Dienst fährt herunter. Den Auftrag als unterbrochen zu
            // kennzeichnen ist ehrlicher, als ihn dauerhaft als laufend zu führen;
            // findet die Wiederherstellung ihn später, kommt sie zum selben Schluss.
            job.Fail(clock.GetUtcNow(), new JobError(
                ConversionErrorCode.Interrupted, "Der Dienst wurde während des Laufs beendet."));
            await jobs.SaveAsync(job, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            // Ein Fehler darf den Arbeiter nie beenden, sonst bliebe die
            // Warteschlange für alle folgenden Aufträge stehen.
            logger.LogError(ex, "Unerwarteter Fehler bei Auftrag {JobId}.", id);

            job.Fail(clock.GetUtcNow(), new JobError(
                ConversionErrorCode.InferenceFailed, "Die Konvertierung ist unerwartet fehlgeschlagen."));
            await jobs.SaveAsync(job, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<(long? Size, string? Sha256)> MeasureOutputAsync(JobId id)
    {
        await using var stream = await workspaces.OpenOutputAsync(id, CancellationToken.None)
            .ConfigureAwait(false);

        if (stream is null)
        {
            return (null, null);
        }

        // Die Prüfsumme wird mitgeliefert, damit das Gateway einen abgebrochenen
        // Transfer erkennen kann, ohne die Datei erneut anzufordern.
        var hash = await SHA256.HashDataAsync(stream, CancellationToken.None).ConfigureAwait(false);
        return (stream.Length, Convert.ToHexString(hash).ToLowerInvariant());
    }
}
