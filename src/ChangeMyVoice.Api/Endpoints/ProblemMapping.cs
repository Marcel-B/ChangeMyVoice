using ChangeMyVoice.Api.Contracts;
using ChangeMyVoice.Application.Common;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ChangeMyVoice.Api.Endpoints;

/// <summary>
/// Übersetzt Fehler der Anwendungsschicht in HTTP-Antworten.
/// </summary>
/// <remarks>
/// Alles kommt als <c>application/problem+json</c> mit dem Fehlercode als
/// zusätzliches Feld zurück. init.md §24 verlangt ausdrücklich, dass ein Fehler
/// nicht einfach als HTTP 500 endet — der Aufrufer soll unterscheiden können,
/// ob seine Datei untauglich war oder der Dienst ein Problem hat.
/// </remarks>
internal static class ProblemMapping
{
    /// <summary>Bildet einen Fehler auf eine Antwort ab.</summary>
    public static ProblemHttpResult ToProblem(this OperationError error)
    {
        var (status, title) = error.Code switch
        {
            OperationErrorCode.VoiceNotFound => (StatusCodes.Status404NotFound, "Referenzstimme nicht gefunden"),
            OperationErrorCode.JobNotFound => (StatusCodes.Status404NotFound, "Auftrag nicht gefunden"),
            OperationErrorCode.DuplicateVoiceLabel => (StatusCodes.Status409Conflict, "Bezeichnung bereits vergeben"),
            OperationErrorCode.VoiceInUse => (StatusCodes.Status409Conflict, "Referenzstimme in Verwendung"),
            OperationErrorCode.ResultNotReady => (StatusCodes.Status409Conflict, "Ergebnis noch nicht bereit"),
            OperationErrorCode.ResultGone => (StatusCodes.Status410Gone, "Ergebnis nicht mehr vorhanden"),
            OperationErrorCode.QueueFull => (StatusCodes.Status503ServiceUnavailable, "Dienst ausgelastet"),
            OperationErrorCode.AudioRejected => (StatusCodes.Status400BadRequest, "Audiodatei abgelehnt"),
            _ => (StatusCodes.Status400BadRequest, "Ungültige Eingabe"),
        };

        // Bei abgelehnten Audiodateien steht der genaue Grund nach init.md §24
        // im Code, damit der Aufrufer nicht raten muss, was an der Datei fehlte.
        var code = error.AudioErrorCode is { } audioCode
            ? JobResponse.ToWireFormat(audioCode.ToString())
            : JobResponse.ToWireFormat(error.Code.ToString());

        return TypedResults.Problem(
            detail: error.Message,
            statusCode: status,
            title: title,
            extensions: new Dictionary<string, object?> { ["code"] = code });
    }
}
