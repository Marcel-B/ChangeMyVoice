using ChangeMyVoice.Application.Common;
using ChangeMyVoice.Application.Ports;
using ChangeMyVoice.Domain.Audio;
using ChangeMyVoice.Domain.Jobs;
using ChangeMyVoice.Domain.Voices;

namespace ChangeMyVoice.Application.UseCases.Jobs;

/// <summary>Die Eingaben eines Konvertierungsauftrags.</summary>
/// <param name="VoiceId">Die gewünschte Referenzstimme.</param>
/// <param name="Upload">Die bereits abgelegte Quellaufnahme.</param>
/// <param name="Options">Die gewählten Stellschrauben.</param>
public sealed record SubmitConversionJobCommand(
    VoiceId VoiceId,
    AudioArtifactRef Upload,
    ConversionOptions Options);

/// <summary>Nimmt einen Konvertierungsauftrag entgegen.</summary>
public interface ISubmitConversionJob
{
    /// <summary>Führt den Anwendungsfall aus.</summary>
    Task<Result<ConversionJobView>> ExecuteAsync(
        SubmitConversionJobCommand command, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class SubmitConversionJob(
    IReferenceVoiceRepository voices,
    IConversionJobRepository jobs,
    IVoiceStorage voiceStorage,
    IJobWorkspaceStore workspaces,
    IAudioProbe probe,
    IAudioNormalizer normalizer,
    IJobQueue queue,
    IServiceInstance instance,
    TimeProvider clock) : ISubmitConversionJob
{
    /// <inheritdoc />
    public async Task<Result<ConversionJobView>> ExecuteAsync(
        SubmitConversionJobCommand command, CancellationToken cancellationToken = default)
    {
        var voice = await voices.FindAsync(command.VoiceId, cancellationToken).ConfigureAwait(false);
        if (voice is null)
        {
            return Result<ConversionJobView>.Failure(
                OperationErrorCode.VoiceNotFound,
                $"Es gibt keine Referenzstimme mit der Kennung '{command.VoiceId}'.");
        }

        if (!voiceStorage.MasterExists(command.VoiceId))
        {
            return Result<ConversionJobView>.Failure(
                OperationErrorCode.VoiceNotFound,
                "Zu dieser Referenzstimme ist keine Audiodatei vorhanden.");
        }

        // Die Quelldatei prüfen, bevor irgendetwas angelegt wird: Ein ungeeigneter
        // Upload soll weder einen Platz in der Warteschlange noch Ladezeit kosten.
        var sourceProperties = await probe
            .ProbeAsync(command.Upload, cancellationToken).ConfigureAwait(false);

        var validation = AudioValidationPolicy.Validate(sourceProperties, AudioRole.Source);
        if (!validation.IsValid)
        {
            return Result<ConversionJobView>.Failure(new OperationError(
                OperationErrorCode.AudioRejected, validation.Message!, validation.ErrorCode));
        }

        var jobId = JobId.New();
        var workspace = workspaces.Create(jobId);
        var target = command.Options.TargetFormat;

        try
        {
            await normalizer.NormalizeAsync(
                command.Upload,
                workspace.Source,
                target,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // Die Referenz wird in das Arbeitsverzeichnis kopiert, statt später aus
            // dem Stimmenordner gelesen zu werden. Damit trägt sich der Auftrag
            // selbst, und ein gleichzeitiges Löschen der Stimme kann ihn nicht
            // zerreißen. Vom 44,1-kHz-Master aus ist das für den Sprachpfad genau
            // eine Umrechnung, für den Gesangspfad gar keine.
            await normalizer.NormalizeAsync(
                voiceStorage.GetMaster(command.VoiceId),
                workspace.Reference,
                target,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var job = ConversionJob.Create(
                jobId, command.VoiceId, voice.Label.Value, command.Options,
                clock.GetUtcNow(), instance.InstanceId);

            await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false);

            if (!queue.TryEnqueue(jobId))
            {
                // Ehrliches Signal an das Gateway statt einer wachsenden
                // Warteschlange, die irgendwann in einen Zeitablauf läuft.
                job.Fail(clock.GetUtcNow(), new JobError(
                    ConversionErrorCode.InferenceFailed, "Die Warteschlange ist ausgelastet."));
                await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false);
                await workspaces.DeleteAsync(jobId, cancellationToken).ConfigureAwait(false);

                return Result<ConversionJobView>.Failure(
                    OperationErrorCode.QueueFull,
                    "Die Warteschlange ist ausgelastet. Bitte später erneut versuchen.");
            }

            return Result<ConversionJobView>.Success(ConversionJobView.From(job));
        }
        catch
        {
            await workspaces.DeleteAsync(jobId, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }
}
