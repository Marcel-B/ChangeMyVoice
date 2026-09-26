using ChangeMyVoice.Adapters.Notifications;
using ChangeMyVoice.Adapters.Storage;
using ChangeMyVoice.Api.Contracts;
using ChangeMyVoice.Application.UseCases.Jobs;
using ChangeMyVoice.Domain.Jobs;
using ChangeMyVoice.Domain.Voices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

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
                + "Sprachpfad bei 22,05 kHz.\n\n"
                + "Im Gesangspfad verschiebt 'semiToneShift' (-24 bis 24) die Tonhöhe um "
                + "ganze Halbtöne; unter der ursprünglichen Begleitung bleiben nur ganze "
                + "Oktaven (±12) in der Tonart. 'autoF0Adjust=true' legt die Tonlage der "
                + "Quelle auf die der Referenz, um einen beliebigen Betrag. Beides ist ohne "
                + "F0-Konditionierung ein Fehler (400).\n\n"
                + "Mit 'webhookUrl' (absolute http- oder https-Adresse) ruft der Dienst "
                + "diese Adresse per POST auf, sobald der Auftrag COMPLETED, FAILED oder "
                + "CANCELLED ist, statt dass der Aufrufer alle paar Sekunden nachfragt. "
                + "Der Rumpf ist JSON mit 'event' (\"job.finished\"), 'jobId', 'status', "
                + "'voiceId', 'finishedAtUtc', 'error', 'statusUrl', 'resultUrl', "
                + "'resultSizeBytes' und 'resultSha256'; der Kopf 'X-ChangeMyVoice-Event' "
                + "nennt die Art. Antwortet der Empfänger nicht mit 2xx, wird bei "
                + "Zeitablauf, 408, 429 und 5xx mit wachsendem Abstand wiederholt. Die "
                + "Nachricht ist nicht signiert und nur ein Anstoß: Verbindlich bleibt "
                + "der Status, den der Empfänger daraufhin abfragt.")
            .DisableAntiforgery()
            .Produces<JobResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/jobs", ListAsync)
            .WithName("ListConversionJobs")
            .WithSummary("Alle Auftraege anzeigen")
            .WithDescription(
                "Liefert eine Uebersicht der Auftraege, die juengsten zuerst. Gedacht "
                + "fuer eine Verwaltungsoberflaeche.\n\n"
                + "Die Antwort ist seitenweise, weil aufgeraeumte Auftraege als Datensatz "
                + "erhalten bleiben und die Gesamtzahl damit dauerhaft waechst. Ueber "
                + "'limit' (1 bis 200, Standard 50) und 'offset' wird geblaettert; 'total' "
                + "nennt die Gesamtzahl der passenden Auftraege. Mit 'status' laesst sich "
                + "auf QUEUED, RUNNING, COMPLETED, FAILED oder CANCELLED einschraenken.")
            .Produces<JobListResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

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
        [FromForm] int? outputSampleRate,
        [FromForm] int? semiToneShift,
        [FromForm] bool? autoF0Adjust,
        [FromForm] string? webhookUrl,
        ISubmitConversionJob useCase,
        FileSystemJobWorkspaceStore workspaces,
        IOptions<WebhookOptions> webhookOptions,
        CancellationToken cancellationToken)
    {
        if (!VoiceId.TryParse(voiceId, out var id))
        {
            return VoiceEndpoints.InvalidIdentifier("Referenzstimme");
        }

        if (!ConversionOptions.TryCreate(
                diffusionSteps, inferenceCfgRate, lengthAdjust, f0Condition, fp16,
                outputSampleRate, semiToneShift, autoF0Adjust, out var options, out var optionsError))
        {
            return TypedResults.Problem(
                detail: optionsError,
                statusCode: StatusCodes.Status400BadRequest,
                title: "Ungültige Einstellungen",
                extensions: new Dictionary<string, object?> { ["code"] = "INVALID_INPUT" });
        }

        WebhookUrl? webhook = null;
        if (webhookUrl is not null &&
            !WebhookUrl.TryCreate(
                webhookUrl, webhookOptions.Value.AllowedHosts, out webhook, out var webhookError))
        {
            return TypedResults.Problem(
                detail: webhookError,
                statusCode: StatusCodes.Status400BadRequest,
                title: "Ungültige Webhook-Adresse",
                extensions: new Dictionary<string, object?> { ["code"] = "INVALID_INPUT" });
        }

        var upload = await UploadBuffer
            .StoreAsync(source, workspaces, cancellationToken).ConfigureAwait(false);

        try
        {
            var result = await useCase
                .ExecuteAsync(new SubmitConversionJobCommand(id, upload, options, webhook), cancellationToken)
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

    private static async Task<IResult> ListAsync(
        string? status,
        int? limit,
        int? offset,
        IListConversionJobs useCase,
        CancellationToken cancellationToken)
    {
        if (!ListConversionJobsQuery.TryCreate(status, limit, offset, out var query, out var error))
        {
            return TypedResults.Problem(
                detail: error,
                statusCode: StatusCodes.Status400BadRequest,
                title: "Ungueltige Abfrage",
                extensions: new Dictionary<string, object?> { ["code"] = "INVALID_INPUT" });
        }

        var result = await useCase.ExecuteAsync(query, cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return result.Error!.ToProblem();
        }

        var page = result.Value!;

        return TypedResults.Ok(new JobListResponse(
            page.Items.Select(JobResponse.From).ToArray(), page.Total, page.Limit, page.Offset));
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
