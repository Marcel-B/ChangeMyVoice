using ChangeMyVoice.Api.Contracts;
using ChangeMyVoice.Application.Ports;
using ChangeMyVoice.Application.UseCases.Model;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ChangeMyVoice.Api.Endpoints;

/// <summary>Die Endpunkte rund um das geladene Modell.</summary>
internal static class ModelEndpoints
{
    /// <summary>Registriert die Endpunkte.</summary>
    public static RouteGroupBuilder MapModelEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/model", GetState)
            .WithName("GetModelState")
            .WithSummary("Geladenes Modell anzeigen")
            .WithDescription(
                "Meldet, ob Seed-VC gerade im Speicher liegt. Nach einem Lauf bleibt das "
                + "Modell eine einstellbare Zeit geladen (Inference:KeepModelLoadedFor, "
                + "Standard 5 Minuten), damit der nächste Auftrag nicht erneut laden muss; "
                + "'unloadAtUtc' nennt, wann es ohne weiteren Auftrag entladen wird.")
            .Produces<ModelStateResponse>();

        group.MapDelete("/model", ReleaseAsync)
            .WithName("ReleaseModel")
            .WithSummary("Modell entladen")
            .WithDescription(
                "Entlädt das Modell sofort und gibt seinen Speicher frei, etwa bevor auf "
                + "demselben Rechner ein anderes großes Modell lädt. Der nächste Auftrag lädt "
                + "es wieder. Wiederholbar: Ist nichts geladen, lautet die Antwort ebenfalls "
                + "204. Läuft gerade ein Auftrag, lautet sie 409 mit dem Code 'MODEL_BUSY'; "
                + "entladen wird dann nach der Leerlaufzeit von selbst oder mit einem "
                + "erneuten Aufruf, sobald der Auftrag fertig ist.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return group;
    }

    private static Ok<ModelStateResponse> GetState(IGetModelState useCase) =>
        TypedResults.Ok(ModelStateResponse.From(useCase.Execute()));

    private static async Task<Results<NoContent, ProblemHttpResult>> ReleaseAsync(
        IReleaseModel useCase, CancellationToken cancellationToken)
    {
        var result = await useCase.ExecuteAsync(cancellationToken).ConfigureAwait(false);

        return result == ModelReleaseResult.Busy
            ? TypedResults.Problem(
                detail: "Es läuft gerade ein Auftrag; das Modell wird danach entladen.",
                statusCode: StatusCodes.Status409Conflict,
                title: "Modell in Verwendung",
                extensions: new Dictionary<string, object?> { ["code"] = "MODEL_BUSY" })
            : TypedResults.NoContent();
    }
}
