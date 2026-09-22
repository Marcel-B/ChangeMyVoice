using ChangeMyVoice.Adapters.Storage;
using ChangeMyVoice.Api.Contracts;
using ChangeMyVoice.Application.UseCases.Voices;
using ChangeMyVoice.Domain.Voices;
using Microsoft.AspNetCore.Mvc;

namespace ChangeMyVoice.Api.Endpoints;

/// <summary>Die Endpunkte rund um Referenzstimmen.</summary>
internal static class VoiceEndpoints
{
    /// <summary>Registriert die Endpunkte.</summary>
    public static RouteGroupBuilder MapVoiceEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/voices", AddAsync)
            .WithName("AddReferenceVoice")
            .WithSummary("Referenzstimme senden")
            .WithDescription(
                "Nimmt eine Aufnahme der Zielstimme entgegen und legt sie dauerhaft ab. "
                + "Angenommen werden WAV, MP3, FLAC, M4A/AAC und OGG/Opus; die Datei wird "
                + "geprüft und intern als Mono-PCM-WAV mit 44,1 kHz gespeichert. "
                + "Aufnahmen über 25 Sekunden werden gekürzt, weil das Modell ohnehin nur "
                + "diesen Anfang verwendet.")
            .DisableAntiforgery()
            .Produces<ReferenceVoiceResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);

        group.MapGet("/voices", ListAsync)
            .WithName("ListReferenceVoices")
            .WithSummary("Alle Referenzstimmen anzeigen")
            .WithDescription("Liefert alle gespeicherten Referenzstimmen mit ihrer Bezeichnung.")
            .Produces<IReadOnlyList<ReferenceVoiceResponse>>();

        group.MapGet("/voices/{voiceId}", GetAsync)
            .WithName("GetReferenceVoice")
            .WithSummary("Einzelne Referenzstimme abrufen")
            .WithDescription(
                "Liefert eine einzelne Referenzstimme samt der Eigenschaften der "
                + "ursprünglich hochgeladenen Datei.")
            .Produces<ReferenceVoiceResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/voices/{voiceId}", DeleteAsync)
            .WithName("DeleteReferenceVoice")
            .WithSummary("Referenzstimme löschen")
            .WithDescription(
                "Entfernt eine Referenzstimme samt ihrer Audiodatei. Solange noch ein "
                + "Auftrag auf sie wartet oder läuft, wird das Löschen abgelehnt.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return group;
    }

    private static async Task<IResult> AddAsync(
        [FromForm] string label,
        IFormFile file,
        IAddReferenceVoice useCase,
        FileSystemJobWorkspaceStore workspaces,
        CancellationToken cancellationToken)
    {
        var upload = await UploadBuffer
            .StoreAsync(file, workspaces, cancellationToken).ConfigureAwait(false);

        try
        {
            var result = await useCase
                .ExecuteAsync(new AddReferenceVoiceCommand(label, upload), cancellationToken)
                .ConfigureAwait(false);

            return result.IsSuccess
                ? TypedResults.Created(
                    $"/api/v1/voices/{result.Value!.Id}", ReferenceVoiceResponse.From(result.Value))
                : result.Error!.ToProblem();
        }
        finally
        {
            UploadBuffer.Discard(upload);
        }
    }

    private static async Task<IResult> ListAsync(
        IListReferenceVoices useCase, CancellationToken cancellationToken)
    {
        var voices = await useCase.ExecuteAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(voices.Select(ReferenceVoiceResponse.From).ToArray());
    }

    private static async Task<IResult> GetAsync(
        string voiceId, IGetReferenceVoice useCase, CancellationToken cancellationToken)
    {
        if (!VoiceId.TryParse(voiceId, out var id))
        {
            return InvalidIdentifier("Referenzstimme");
        }

        var result = await useCase.ExecuteAsync(id, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(ReferenceVoiceResponse.From(result.Value!))
            : result.Error!.ToProblem();
    }

    private static async Task<IResult> DeleteAsync(
        string voiceId, IDeleteReferenceVoice useCase, CancellationToken cancellationToken)
    {
        if (!VoiceId.TryParse(voiceId, out var id))
        {
            return InvalidIdentifier("Referenzstimme");
        }

        var result = await useCase.ExecuteAsync(id, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? TypedResults.NoContent() : result.Error!.ToProblem();
    }

    internal static IResult InvalidIdentifier(string what) =>
        TypedResults.Problem(
            detail: $"Die angegebene Kennung der {what} ist keine gültige UUID.",
            statusCode: StatusCodes.Status400BadRequest,
            title: "Ungültige Kennung",
            extensions: new Dictionary<string, object?> { ["code"] = "INVALID_INPUT" });
}
