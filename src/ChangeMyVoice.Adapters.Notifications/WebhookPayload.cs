using ChangeMyVoice.Application.UseCases.Jobs;

namespace ChangeMyVoice.Adapters.Notifications;

/// <summary>Die Fehlerursache in der Nachricht.</summary>
/// <param name="Code">Der Fehlercode, wie ihn die Statusabfrage liefert.</param>
/// <param name="Message">Eine kurze Begründung.</param>
public sealed record WebhookError(string Code, string Message);

/// <summary>
/// Was an die Adresse eines Auftrags geschickt wird, wenn er endet.
/// </summary>
/// <remarks>
/// Die Felder heißen und schreiben sich wie in der Statusantwort, damit ein
/// Empfänger dieselben Werte in derselben Form erhält. Mehr als ein Anstoß ist
/// es trotzdem nicht: Verbindlich bleibt die Statusabfrage.
/// </remarks>
/// <param name="Event">Die Art der Nachricht, derzeit immer <c>job.finished</c>.</param>
/// <param name="JobId">Die Kennung des Auftrags.</param>
/// <param name="Status">Der Endzustand: COMPLETED, FAILED oder CANCELLED.</param>
/// <param name="VoiceId">Die verwendete Referenzstimme.</param>
/// <param name="FinishedAtUtc">Zeitpunkt des Endes.</param>
/// <param name="Error">Die Ursache, falls der Auftrag fehlgeschlagen ist.</param>
/// <param name="StatusUrl">Wo der Zustand abgefragt wird.</param>
/// <param name="ResultUrl">Wo das Ergebnis abgeholt werden kann, falls es bereitsteht.</param>
/// <param name="ResultSizeBytes">Die Größe der Ergebnisdatei.</param>
/// <param name="ResultSha256">Die Prüfsumme der Ergebnisdatei.</param>
public sealed record WebhookPayload(
    string Event,
    string JobId,
    string Status,
    string VoiceId,
    DateTimeOffset? FinishedAtUtc,
    WebhookError? Error,
    string StatusUrl,
    string? ResultUrl,
    long? ResultSizeBytes,
    string? ResultSha256)
{
    /// <summary>Bildet die Nachricht aus der Sicht auf einen beendeten Auftrag.</summary>
    public static WebhookPayload From(ConversionJobView job) =>
        new(WebhookDispatcher.JobFinishedEvent,
            job.Id.ToString(),
            ToWireFormat(job.Status.ToString()),
            job.VoiceId.ToString(),
            job.FinishedAtUtc,
            job.Error is null
                ? null
                : new WebhookError(ToWireFormat(job.Error.Code.ToString()), job.Error.Message),
            $"/api/v1/jobs/{job.Id}",
            job.IsResultAvailable ? $"/api/v1/jobs/{job.Id}/result" : null,
            job.OutputSizeBytes,
            job.OutputSha256);

    /// <summary>
    /// Dieselbe Schreibweise wie in der Statusantwort (init.md §24), etwa
    /// <c>InferenceFailed</c> zu <c>INFERENCE_FAILED</c>.
    /// </summary>
    internal static string ToWireFormat(string name) =>
        string.Concat(name.Select((c, i) =>
            char.IsUpper(c) && i > 0 ? "_" + c : c.ToString())).ToUpperInvariant();
}
