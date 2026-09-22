using System.Diagnostics;
using ChangeMyVoice.Application.UseCases.Maintenance;
using Microsoft.Extensions.Logging;

namespace ChangeMyVoice.Adapters.Inference;

/// <summary>
/// Beendet Inferenzprozesse, die ein abgestürzter Dienstlauf zurückgelassen hat.
/// </summary>
/// <remarks>
/// Die Prozesskennung wird vor dem Beenden gegen den Startzeitpunkt des
/// Auftrags geprüft — nach einem Neustart kann dieselbe Kennung längst an ein
/// völlig anderes Programm vergeben sein, und das darf auf keinen Fall getroffen
/// werden.
/// </remarks>
public sealed class ProcessTreeKiller(ILogger<ProcessTreeKiller> logger) : IOrphanProcessKiller
{
    /// <inheritdoc />
    public bool TryKill(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);

            if (process.HasExited)
            {
                return false;
            }

            // Der Prozess muss älter sein als dieser Dienstlauf, sonst gehört er
            // nicht zu einem früheren Leben.
            if (process.StartTime.ToUniversalTime() > Process.GetCurrentProcess().StartTime.ToUniversalTime())
            {
                logger.LogDebug(
                    "Prozess {ProcessId} ist jünger als dieser Dienstlauf und wird nicht beendet.",
                    processId);
                return false;
            }

            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (ArgumentException)
        {
            // Die Kennung gehört zu keinem laufenden Prozess mehr — der Normalfall.
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Prozess {ProcessId} ließ sich nicht beenden.", processId);
            return false;
        }
    }
}
