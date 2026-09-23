using ChangeMyVoice.Application.UseCases.Jobs;
using ChangeMyVoice.Application.UseCases.Voices;
using ChangeMyVoice.Domain.Audio;

namespace ChangeMyVoice.Api.Contracts;

/// <summary>Die Eigenschaften einer Audioaufnahme.</summary>
/// <param name="Codec">Der Codec, etwa <c>pcm_s16le</c> oder <c>mp3</c>.</param>
/// <param name="DurationSeconds">Die Spieldauer in Sekunden.</param>
/// <param name="SampleRate">Die Abtastrate in Hertz.</param>
/// <param name="Channels">Die Anzahl der Kanäle.</param>
public sealed record AudioPropertiesResponse(
    string Codec,
    double DurationSeconds,
    int SampleRate,
    int Channels)
{
    /// <summary>Bildet die Antwort aus den gemessenen Eigenschaften.</summary>
    public static AudioPropertiesResponse From(AudioProperties properties) =>
        new(properties.Codec, properties.DurationSeconds, properties.SampleRate, properties.Channels);
}

/// <summary>Eine gespeicherte Referenzstimme.</summary>
/// <param name="Id">Die Kennung, mit der sie bei einem Auftrag angegeben wird.</param>
/// <param name="Label">Die Bezeichnung.</param>
/// <param name="CreatedAtUtc">Zeitpunkt der Anlage.</param>
/// <param name="Stored">
/// Die Eigenschaften der abgelegten Datei — stets mono, PCM und 44,1 kHz.
/// </param>
/// <param name="Original">
/// Die Eigenschaften der hochgeladenen Datei. Daran ist erkennbar, ob das
/// Ausgangsmaterial für ein gutes Ergebnis taugt.
/// </param>
public sealed record ReferenceVoiceResponse(
    string Id,
    string Label,
    DateTimeOffset CreatedAtUtc,
    AudioPropertiesResponse Stored,
    AudioPropertiesResponse Original)
{
    /// <summary>Bildet die Antwort aus der Sicht der Anwendungsschicht.</summary>
    public static ReferenceVoiceResponse From(ReferenceVoiceView view) =>
        new(view.Id.ToString(), view.Label, view.CreatedAtUtc,
            AudioPropertiesResponse.From(view.StoredAudio),
            AudioPropertiesResponse.From(view.OriginalAudio));
}

/// <summary>Die Fehlerursache eines Auftrags.</summary>
/// <param name="Code">Der Fehlercode.</param>
/// <param name="Message">Eine kurze Begründung.</param>
public sealed record JobErrorResponse(string Code, string Message);

/// <summary>Der Zustand eines Konvertierungsauftrags.</summary>
/// <param name="JobId">Die Kennung.</param>
/// <param name="Status">Der Zustand: QUEUED, RUNNING, COMPLETED, FAILED oder CANCELLED.</param>
/// <param name="VoiceId">Die verwendete Referenzstimme.</param>
/// <param name="VoiceLabel">Deren Bezeichnung bei Annahme des Auftrags.</param>
/// <param name="CreatedAtUtc">Zeitpunkt der Annahme.</param>
/// <param name="StartedAtUtc">Zeitpunkt des Beginns.</param>
/// <param name="FinishedAtUtc">Zeitpunkt des Endes.</param>
/// <param name="Error">Die Ursache, falls der Auftrag fehlgeschlagen ist.</param>
/// <param name="ResultUrl">Wo das Ergebnis abgeholt werden kann, sobald es bereitsteht.</param>
/// <param name="ResultSizeBytes">Die Größe der Ergebnisdatei.</param>
/// <param name="ResultSha256">
/// Die Prüfsumme der Ergebnisdatei, damit ein abgebrochener Abruf erkennbar ist.
/// </param>
/// <param name="SourceLengthSeconds">Die Länge der Quellaufnahme in Sekunden.</param>
/// <param name="EstimatedDurationSeconds">
/// Voraussichtliche Rechenzeit in Sekunden. Eine Orientierung, keine Zusage —
/// gedacht für eine Fortschrittsanzeige, denn ein Lauf kann je nach Länge des
/// Materials deutlich über eine Stunde dauern.
/// </param>
public sealed record JobResponse(
    string JobId,
    string Status,
    string VoiceId,
    string VoiceLabel,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    JobErrorResponse? Error,
    string? ResultUrl,
    long? ResultSizeBytes,
    string? ResultSha256,
    double SourceLengthSeconds,
    double EstimatedDurationSeconds)
{
    /// <summary>Bildet die Antwort aus der Sicht der Anwendungsschicht.</summary>
    public static JobResponse From(ConversionJobView view) =>
        new(view.Id.ToString(),
            ToWireFormat(view.Status.ToString()),
            view.VoiceId.ToString(),
            view.VoiceLabel,
            view.CreatedAtUtc,
            view.StartedAtUtc,
            view.FinishedAtUtc,
            view.Error is null
                ? null
                : new JobErrorResponse(ToWireFormat(view.Error.Code.ToString()), view.Error.Message),
            view.IsResultAvailable ? $"/api/v1/jobs/{view.Id}/result" : null,
            view.OutputSizeBytes,
            view.OutputSha256,
            Math.Round(view.SourceLength.TotalSeconds, 1),
            Math.Round(view.EstimatedDuration.TotalSeconds));

    /// <summary>
    /// Wandelt einen Aufzählungsnamen in die Schreibweise um, die init.md §24
    /// für Fehlercodes vorgibt.
    /// </summary>
    internal static string ToWireFormat(string name) =>
        string.Concat(name.Select((c, i) =>
            char.IsUpper(c) && i > 0 ? "_" + c : c.ToString())).ToUpperInvariant();
}

/// <summary>Ein Ausschnitt aus der Auftragsliste.</summary>
/// <param name="Items">Die Aufträge dieser Seite, die jüngsten zuerst.</param>
/// <param name="Total">Wie viele Aufträge insgesamt zum Filter passen.</param>
/// <param name="Limit">Die verwendete Seitengröße.</param>
/// <param name="Offset">Der verwendete Versatz.</param>
public sealed record JobListResponse(
    IReadOnlyList<JobResponse> Items,
    int Total,
    int Limit,
    int Offset);

/// <summary>Der Zustand des Dienstes.</summary>
/// <param name="Status">"healthy" oder "unhealthy".</param>
/// <param name="Checks">Die einzelnen Prüfungen.</param>
public sealed record HealthResponse(string Status, IReadOnlyList<HealthCheckResponse> Checks);

/// <summary>Eine einzelne Bereitschaftsprüfung.</summary>
/// <param name="Name">Der Name der Prüfung.</param>
/// <param name="Healthy">Ob sie bestanden wurde.</param>
/// <param name="Detail">Eine knappe Erläuterung, falls nicht.</param>
public sealed record HealthCheckResponse(string Name, bool Healthy, string? Detail);
