using ChangeMyVoice.Domain.Jobs;
using ChangeMyVoice.Domain.Voices;

namespace ChangeMyVoice.Application.UseCases.Jobs;

/// <summary>Die Sicht auf einen Auftrag, wie sie nach außen gereicht wird.</summary>
/// <param name="Id">Die Kennung.</param>
/// <param name="VoiceId">Die verwendete Referenzstimme.</param>
/// <param name="VoiceLabel">Deren Bezeichnung zum Zeitpunkt der Annahme.</param>
/// <param name="Status">Der aktuelle Zustand.</param>
/// <param name="CreatedAtUtc">Zeitpunkt der Annahme.</param>
/// <param name="StartedAtUtc">Zeitpunkt des Beginns.</param>
/// <param name="FinishedAtUtc">Zeitpunkt des Endes.</param>
/// <param name="Error">Die Fehlerursache, falls fehlgeschlagen.</param>
/// <param name="OutputSizeBytes">Größe der Ergebnisdatei.</param>
/// <param name="OutputSha256">Prüfsumme der Ergebnisdatei.</param>
/// <param name="IsResultAvailable">Ob das Ergebnis abgerufen werden kann.</param>
/// <param name="SourceLength">Die Länge der Quellaufnahme.</param>
/// <param name="EstimatedDuration">Voraussichtliche Rechenzeit.</param>
/// <param name="WebhookUrl">Die Adresse, die beim Ende aufgerufen wird, falls angegeben.</param>
public sealed record ConversionJobView(
    JobId Id,
    VoiceId VoiceId,
    string VoiceLabel,
    JobStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    JobError? Error,
    long? OutputSizeBytes,
    string? OutputSha256,
    bool IsResultAvailable,
    TimeSpan SourceLength,
    TimeSpan EstimatedDuration,
    WebhookUrl? WebhookUrl = null)
{
    /// <summary>Bildet die Sicht auf eine Entität ab.</summary>
    public static ConversionJobView From(ConversionJob job) =>
        new(job.Id, job.VoiceId, job.VoiceLabel, job.Status, job.CreatedAtUtc,
            job.StartedAtUtc, job.FinishedAtUtc, job.Error, job.OutputSizeBytes,
            job.OutputSha256, job.IsResultAvailable, job.SourceLength, job.EstimatedDuration,
            job.WebhookUrl);
}
