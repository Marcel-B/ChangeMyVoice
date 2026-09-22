using ChangeMyVoice.Application.Common;
using ChangeMyVoice.Application.Ports;
using ChangeMyVoice.Domain.Audio;
using ChangeMyVoice.Domain.Voices;

namespace ChangeMyVoice.Application.UseCases.Voices;

/// <summary>Die Sicht auf eine Referenzstimme, wie sie nach außen gereicht wird.</summary>
/// <param name="Id">Die Kennung.</param>
/// <param name="Label">Die Bezeichnung.</param>
/// <param name="CreatedAtUtc">Zeitpunkt der Anlage.</param>
/// <param name="StoredAudio">Eigenschaften der abgelegten Datei.</param>
/// <param name="OriginalAudio">Eigenschaften der ursprünglichen Datei.</param>
public sealed record ReferenceVoiceView(
    VoiceId Id,
    string Label,
    DateTimeOffset CreatedAtUtc,
    AudioProperties StoredAudio,
    AudioProperties OriginalAudio)
{
    /// <summary>Bildet die Sicht auf eine Entität ab.</summary>
    public static ReferenceVoiceView From(ReferenceVoice voice) =>
        new(voice.Id, voice.Label.Value, voice.CreatedAtUtc, voice.StoredAudio, voice.OriginalAudio);
}

/// <summary>Die Eingaben zum Anlegen einer Referenzstimme.</summary>
/// <param name="Label">Die gewünschte Bezeichnung.</param>
/// <param name="Upload">Die bereits abgelegte Uploaddatei.</param>
public sealed record AddReferenceVoiceCommand(string? Label, AudioArtifactRef Upload);

/// <summary>Nimmt eine neue Referenzstimme entgegen.</summary>
public interface IAddReferenceVoice
{
    /// <summary>Führt den Anwendungsfall aus.</summary>
    Task<Result<ReferenceVoiceView>> ExecuteAsync(
        AddReferenceVoiceCommand command, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class AddReferenceVoice(
    IReferenceVoiceRepository repository,
    IVoiceStorage voiceStorage,
    IAudioProbe probe,
    IAudioNormalizer normalizer,
    TimeProvider clock) : IAddReferenceVoice
{
    /// <inheritdoc />
    public async Task<Result<ReferenceVoiceView>> ExecuteAsync(
        AddReferenceVoiceCommand command, CancellationToken cancellationToken = default)
    {
        if (!VoiceLabel.TryCreate(command.Label, out var label, out var labelError))
        {
            return Result<ReferenceVoiceView>.Failure(OperationErrorCode.InvalidInput, labelError!);
        }

        if (await repository.ExistsWithLabelAsync(label, cancellationToken).ConfigureAwait(false))
        {
            return Result<ReferenceVoiceView>.Failure(
                OperationErrorCode.DuplicateVoiceLabel,
                $"Eine Referenzstimme mit der Bezeichnung '{label}' existiert bereits.");
        }

        // Erst messen, dann entscheiden: Eine ungeeignete Datei soll gar nicht
        // erst abgelegt werden.
        var original = await probe.ProbeAsync(command.Upload, cancellationToken)
            .ConfigureAwait(false);

        var validation = AudioValidationPolicy.Validate(original, AudioRole.Reference);
        if (!validation.IsValid)
        {
            return Result<ReferenceVoiceView>.Failure(new OperationError(
                OperationErrorCode.AudioRejected, validation.Message!, validation.ErrorCode));
        }

        var id = VoiceId.New();
        var master = voiceStorage.ReserveMaster(id);

        try
        {
            // Die Normalisierung schreibt unmittelbar an den endgültigen Ort.
            // Das erspart eine Zwischendatei und hält Dateisystemzugriffe
            // vollständig im Adapter.
            await normalizer.NormalizeAsync(
                command.Upload,
                master,
                TargetAudioFormat.ReferenceMaster,
                validation.WillBeTruncatedTo,
                cancellationToken).ConfigureAwait(false);

            var stored = await probe.ProbeAsync(master, cancellationToken).ConfigureAwait(false);

            if (stored is null)
            {
                await voiceStorage.DeleteAsync(id, cancellationToken).ConfigureAwait(false);

                return Result<ReferenceVoiceView>.Failure(new OperationError(
                    OperationErrorCode.AudioRejected,
                    "Die Aufnahme ließ sich nicht in das benötigte Format bringen.",
                    Domain.Jobs.ConversionErrorCode.InvalidAudio));
            }

            var voice = ReferenceVoice.Create(id, label, clock.GetUtcNow(), stored, original!);
            await repository.SaveAsync(voice, cancellationToken).ConfigureAwait(false);

            return Result<ReferenceVoiceView>.Success(ReferenceVoiceView.From(voice));
        }
        catch
        {
            // Eine halb geschriebene Stimme darf nicht zurückbleiben.
            await voiceStorage.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>Liefert alle Referenzstimmen.</summary>
public interface IListReferenceVoices
{
    /// <summary>Führt den Anwendungsfall aus.</summary>
    Task<IReadOnlyList<ReferenceVoiceView>> ExecuteAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class ListReferenceVoices(IReferenceVoiceRepository repository) : IListReferenceVoices
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ReferenceVoiceView>> ExecuteAsync(
        CancellationToken cancellationToken = default)
    {
        var voices = await repository.ListAsync(cancellationToken).ConfigureAwait(false);
        return voices.Select(ReferenceVoiceView.From).ToArray();
    }
}

/// <summary>Liefert eine einzelne Referenzstimme.</summary>
public interface IGetReferenceVoice
{
    /// <summary>Führt den Anwendungsfall aus.</summary>
    Task<Result<ReferenceVoiceView>> ExecuteAsync(
        VoiceId id, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class GetReferenceVoice(IReferenceVoiceRepository repository) : IGetReferenceVoice
{
    /// <inheritdoc />
    public async Task<Result<ReferenceVoiceView>> ExecuteAsync(
        VoiceId id, CancellationToken cancellationToken = default)
    {
        var voice = await repository.FindAsync(id, cancellationToken).ConfigureAwait(false);

        return voice is null
            ? Result<ReferenceVoiceView>.Failure(
                OperationErrorCode.VoiceNotFound, $"Es gibt keine Referenzstimme mit der Kennung '{id}'.")
            : Result<ReferenceVoiceView>.Success(ReferenceVoiceView.From(voice));
    }
}

/// <summary>Entfernt eine Referenzstimme.</summary>
public interface IDeleteReferenceVoice
{
    /// <summary>Führt den Anwendungsfall aus.</summary>
    Task<Result> ExecuteAsync(VoiceId id, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class DeleteReferenceVoice(
    IReferenceVoiceRepository repository,
    IConversionJobRepository jobs,
    IVoiceStorage storage) : IDeleteReferenceVoice
{
    /// <inheritdoc />
    public async Task<Result> ExecuteAsync(VoiceId id, CancellationToken cancellationToken = default)
    {
        var voice = await repository.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (voice is null)
        {
            return Result.Failure(
                OperationErrorCode.VoiceNotFound, $"Es gibt keine Referenzstimme mit der Kennung '{id}'.");
        }

        // Laufende Aufträge arbeiten zwar auf einer Kopie im eigenen
        // Arbeitsverzeichnis, aber ein Löschen mitten im Betrieb wäre trotzdem
        // überraschend — und die Übersicht würde eine Stimme verlieren, die
        // gerade noch verwendet wird.
        if (await jobs.HasActiveJobForVoiceAsync(id, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure(
                OperationErrorCode.VoiceInUse,
                "Die Referenzstimme wird noch von einem laufenden oder wartenden Auftrag verwendet.");
        }

        await storage.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        await repository.DeleteAsync(id, cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }
}
