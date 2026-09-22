namespace ChangeMyVoice.Domain.Jobs;

/// <summary>Der Lebenszyklus eines Konvertierungsauftrags.</summary>
public enum JobStatus
{
    /// <summary>Angenommen und wartet auf einen freien Platz.</summary>
    Queued,

    /// <summary>Wird gerade berechnet.</summary>
    Running,

    /// <summary>Erfolgreich beendet, das Ergebnis steht bereit.</summary>
    Completed,

    /// <summary>Endgültig fehlgeschlagen.</summary>
    Failed,

    /// <summary>Vom Aufrufer abgebrochen.</summary>
    Cancelled,
}
