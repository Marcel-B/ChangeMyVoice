namespace ChangeMyVoice.Domain.Jobs;

/// <summary>Was beim Start mit einem vorgefundenen Auftrag geschehen soll.</summary>
public enum JobRecoveryAction
{
    /// <summary>Unverändert lassen.</summary>
    Leave,

    /// <summary>Erneut in die Warteschlange stellen.</summary>
    Requeue,

    /// <summary>Als unterbrochen abschließen und die Dateien entfernen.</summary>
    FailAsInterrupted,
}

/// <summary>
/// Entscheidet, wie ein beim Start vorgefundener Auftrag zu behandeln ist.
/// </summary>
/// <remarks>
/// Ein Auftrag, der noch als laufend verzeichnet ist, obwohl er aus einem
/// früheren Prozesslauf stammt, kann den Neustart nicht überlebt haben: Die
/// Inferenz lief im Kindprozess dieses Dienstes. Er wird deshalb als unterbrochen
/// beendet und bewusst <em>nicht</em> automatisch wiederholt — ein Auftrag, der
/// den Dienst mitgerissen hat, etwa durch Speichermangel, würde sonst eine
/// Absturzschleife auslösen.
/// </remarks>
public static class JobRecoveryPolicy
{
    /// <summary>Bestimmt die Maßnahme für einen vorgefundenen Auftrag.</summary>
    /// <param name="job">Der geladene Auftrag.</param>
    /// <param name="currentInstanceId">Die Kennung des laufenden Dienstlaufs.</param>
    public static JobRecoveryAction Decide(ConversionJob job, Guid currentInstanceId) =>
        job.Status switch
        {
            // Die Eingabedateien liegen vollständig auf der Platte, hier ist ein
            // erneuter Anlauf gefahrlos.
            JobStatus.Queued => JobRecoveryAction.Requeue,
            JobStatus.Running when job.InstanceId != currentInstanceId => JobRecoveryAction.FailAsInterrupted,
            _ => JobRecoveryAction.Leave,
        };
}
