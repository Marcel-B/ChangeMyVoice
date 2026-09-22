using ChangeMyVoice.Adapters.Storage;
using ChangeMyVoice.Api.Contracts;
using ChangeMyVoice.Application.UseCases.Jobs;
using ChangeMyVoice.Domain.Jobs;
using ChangeMyVoice.Domain.Voices;
using Microsoft.AspNetCore.Mvc;

namespace ChangeMyVoice.Api.Endpoints;

/// <summary>Die Endpunkte rund um Konvertierungsaufträge.</summary>
internal static class JobEndpoints
{
    /// <summary>Registriert die Endpunkte.</summary>
    public static RouteGroupBuilder MapJobEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/jobs", SubmitAsync)
            .WithName("SubmitConversionJob")
            .WithSummary("Stimme zur Änderung senden")
            .WithDescription(
                "Nimmt die zu konvertierende Aufnahme entgegen und startet den Auftrag mit "
                + "der angegebenen Referenzstimme. Die Antwort kommt sofort; der Fortschritt "
                + "wird über den Status abgefragt. Die Gesangsdarbietung der Quelle bleibt "
                + "erhalten, das Timbre stammt aus der Referenz.\n\n"
                + "Standardmäßig läuft die Konvertierung im Gesangspfad: 44,1 kHz mit "
                + "Tonhöhenkonditionierung, wodurch die Tonhöhe sauber übertragen wird. "
                + "Das dauert länger — bei rund 15 Sekunden Material etwa 60 statt 40 "
                + "Sekunden. Mit 'f0Condition=false' läuft stattdessen der schnellere "
                + "Sprachpfad bei 22,05 kHz.")
            .DisableAntiforgery()
            .Produces<JobResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/jobs/{jobId}", GetStatusAsync)
            .WithName("GetJobStatus")
            .WithSummary("Status abfragen")
            .WithDescription(
                "Liefert den Zustand eines Auftrags. Steht das Ergebnis bereit, enthält die "
                + "Antwort zusätzlich die Abrufadresse sowie Größe und Prüfsumme der Datei.")
            .Produces<JobResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/jobs/{jobId}/result", GetResultAsync)
            .WithName("GetJobResult")
            .WithSummary("Ergebnis herunterladen")
            .WithDescription(
                "Liefert die konvertierte Aufnahme als WAV. Der Abruf ist wiederholbar; "
                + "die Datei wird erst nach Ablauf der Aufbewahrungsfrist entfernt.")
            .Produces<IResult>(StatusCodes.Status200OK, "audio/wav")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone);

        group.MapDelete("/jobs/{jobId}", CancelAsync)
            .WithName("CancelJob")
            .WithSummary("Auftrag abbrechen")
            .WithDescription(
                "Bricht einen Auftrag ab und entfernt seine Arbeitsdateien sofort, statt auf "
                + "den regelmäßigen Aufräumlauf zu warten. Der Aufruf ist wiederholbar.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    private static async Task<IResult> SubmitAsync(
        [FromForm] string voiceId,
        IFormFile source,
        [FromForm] int? diffusionSteps,
        [FromForm] double? inferenceCfgRate,
        [FromForm] double? lengthAdjust,
        [FromForm] bool? f0Condition,
        [FromForm] bool? fp16,
        ISubmitConversionJob useCase,
        FileSystemJobWorkspaceStore workspaces,
        CancellationToken cancellationToken)
    {
        if (!VoiceId.TryParse(voiceId, out var id))
        {
            return VoiceEndpoints.InvalidIdentifier("Referenzstimme");
        }

        if (!ConversionOptions.TryCreate(
                diffusionSteps, inferenceCfgRate, lengthAdjust, f0Condition, fp16,
                out var options, out var optionsError))
        {
            return TypedResults.Problem(
                detail: optionsError,
                statusCode: StatusCodes.Status400BadRequest,
                title: "Ungültige Einstellungen",
                extensions: new Dictionary<string, object?> { ["code"] = "INVALID_INPUT" });
        }

        var upload = await UploadBuffer
            .StoreAsync(source, workspaces, cancellationToken).ConfigureAwait(false);

        try
        {
            var result = await useCase
                .ExecuteAsync(new SubmitConversionJobCommand(id, upload, options), cancellationToken)
                .ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                return result.Error!.ToProblem();
            }

            var response = JobResponse.From(result.Value!);

            return TypedResults.Accepted($"/api/v1/jobs/{response.JobId}", response);
        }
        finally
        {
            UploadBuffer.Discard(upload);
        }
    }

    private static async Task<IResult> GetStatusAsync(
        string jobId, IGetJobStatus useCase, CancellationToken cancellationToken)
    {
        if (!JobId.TryParse(jobId, out var id))
        {
            return VoiceEndpoints.InvalidIdentifier("Auftrags");
        }

        var result = await useCase.ExecuteAsync(id, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.Ok(JobResponse.From(result.Value!))
            : result.Error!.ToProblem();
    }

    private static async Task<IResult> GetResultAsync(
        string jobId, IGetJobResult useCase, CancellationToken cancellationToken)
    {
        if (!JobId.TryParse(jobId, out var id))
        {
            return VoiceEndpoints.InvalidIdentifier("Auftrags");
        }

        var result = await useCase.ExecuteAsync(id, cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return result.Error!.ToProblem();
        }

        var payload = result.Value!;

        // Die Prüfsumme als Marke mitgeben: Damit kann das Gateway einen
        // unveränderten Abruf erkennen, ohne die Datei erneut zu laden.
        return TypedResults.Stream(
            payload.Content,
            contentType: "audio/wav",
            fileDownloadName: payload.FileName,
            entityTag: payload.Sha256 is null
                ? null
                : new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{payload.Sha256}\""),
            enableRangeProcessing: true);
    }

    private static async Task<IResult> CancelAsync(
        string jobId, ICancelJob useCase, CancellationToken cancellationToken)
    {
        if (!JobId.TryParse(jobId, out var id))
        {
            return VoiceEndpoints.InvalidIdentifier("Auftrags");
        }

        var result = await useCase.ExecuteAsync(id, cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? TypedResults.NoContent() : result.Error!.ToProblem();
    }
}
