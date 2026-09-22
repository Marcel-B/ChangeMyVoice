namespace ChangeMyVoice.Domain.Jobs;

/// <summary>Aufbewahrungsfristen für Arbeitsdateien.</summary>
/// <param name="Completed">Frist für erfolgreiche, noch nicht abgeholte Aufträge.</param>
/// <param name="Downloaded">Kürzere Frist, sobald das Ergebnis abgeholt wurde.</param>
/// <param name="Failed">Frist für fehlgeschlagene Aufträge, zur Fehlersuche etwas länger.</param>
/// <param name="Orphan">Schonfrist für Verzeichnisse ohne zugehörigen Datensatz.</param>
public sealed record JobRetention(
    TimeSpan Completed,
    TimeSpan Downloaded,
    TimeSpan Failed,
    TimeSpan Orphan)
{
    /// <summary>Die Voreinstellung.</summary>
    public static JobRetention Default { get; } = new(
        Completed: TimeSpan.FromHours(24),
        Downloaded: TimeSpan.FromHours(1),
        Failed: TimeSpan.FromHours(72),
        Orphan: TimeSpan.FromHours(1));
}

/// <summary>
/// Entscheidet, ob die Dateien eines Auftrags entfernt werden dürfen.
/// </summary>
/// <remarks>
/// Bewusst reine Rechenlogik ohne Dateisystem: Nur so lässt sich die Regel mit
/// einer gestellten Uhr prüfen, statt im Test echte Stunden verstreichen zu
/// lassen. Referenzstimmen kommen hier nicht vor — sie werden nie aufgeräumt.
/// </remarks>
public static class JobRetentionPolicy
{
    /// <summary>Ob die Arbeitsdateien des Auftrags entfernt werden sollen.</summary>
    public static bool ShouldPurge(ConversionJob job, DateTimeOffset nowUtc, JobRetention? retention = null)
    {
        retention ??= JobRetention.Default;

        if (job.ArtifactsPurged || !job.IsTerminal || job.FinishedAtUtc is not { } finished)
        {
            return false;
        }

        var age = nowUtc - finished;

        return job.Status switch
        {
            // Ein abgeholtes Ergebnis darf schneller weg; der Abruf bleibt bis
            // dahin wiederholbar, damit ein abgebrochener Transfer nichts kostet.
            JobStatus.Completed when job.DownloadedAtUtc is { } downloaded =>
                nowUtc - downloaded >= retention.Downloaded || age >= retention.Completed,
            JobStatus.Completed => age >= retention.Completed,
            JobStatus.Failed => age >= retention.Failed,
            JobStatus.Cancelled => true,
            _ => false,
        };
    }

    /// <summary>
    /// Ob ein Verzeichnis ohne zugehörigen Datensatz entfernt werden darf. Die
    /// Schonfrist verhindert, dass ein Verzeichnis gelöscht wird, das gerade erst
    /// angelegt, aber noch nicht gespeichert wurde.
    /// </summary>
    public static bool ShouldPurgeOrphan(
        DateTimeOffset directoryCreatedAtUtc,
        DateTimeOffset nowUtc,
        JobRetention? retention = null)
    {
        retention ??= JobRetention.Default;
        return nowUtc - directoryCreatedAtUtc >= retention.Orphan;
    }
}
