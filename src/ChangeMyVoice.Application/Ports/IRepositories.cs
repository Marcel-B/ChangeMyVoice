using ChangeMyVoice.Domain.Jobs;
using ChangeMyVoice.Domain.Voices;

namespace ChangeMyVoice.Application.Ports;

/// <summary>Speichert die Stammdaten der Referenzstimmen.</summary>
public interface IReferenceVoiceRepository
{
    /// <summary>Legt eine Stimme an oder überschreibt sie.</summary>
    Task SaveAsync(ReferenceVoice voice, CancellationToken cancellationToken = default);

    /// <summary>Liest eine Stimme, oder <c>null</c>, wenn es sie nicht gibt.</summary>
    Task<ReferenceVoice?> FindAsync(VoiceId id, CancellationToken cancellationToken = default);

    /// <summary>Liest alle Stimmen, nach Bezeichnung sortiert.</summary>
    Task<IReadOnlyList<ReferenceVoice>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Prüft, ob eine Bezeichnung bereits vergeben ist.</summary>
    Task<bool> ExistsWithLabelAsync(VoiceLabel label, CancellationToken cancellationToken = default);

    /// <summary>Entfernt eine Stimme.</summary>
    Task DeleteAsync(VoiceId id, CancellationToken cancellationToken = default);
}

/// <summary>Speichert die Auftragsdaten.</summary>
public interface IConversionJobRepository
{
    /// <summary>Legt einen Auftrag an oder überschreibt ihn.</summary>
    Task SaveAsync(ConversionJob job, CancellationToken cancellationToken = default);

    /// <summary>Liest einen Auftrag, oder <c>null</c>, wenn es ihn nicht gibt.</summary>
    Task<ConversionJob?> FindAsync(JobId id, CancellationToken cancellationToken = default);

    /// <summary>Liest alle Aufträge, deren Dateien noch vorhanden sind.</summary>
    Task<IReadOnlyList<ConversionJob>> ListUnpurgedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Liest einen Ausschnitt aller Aufträge, die jüngsten zuerst.
    /// </summary>
    /// <remarks>
    /// Bewusst seitenweise: Aufgeräumte Aufträge bleiben als Datensatz erhalten,
    /// die Gesamtzahl wächst also dauerhaft. Eine Abfrage ohne Grenze würde mit
    /// der Zeit immer langsamer und die Antwort immer größer.
    /// </remarks>
    /// <param name="status">Auf diesen Zustand einschränken, oder <c>null</c> für alle.</param>
    /// <param name="limit">Wie viele Einträge höchstens.</param>
    /// <param name="offset">Wie viele Einträge übersprungen werden.</param>
    /// <param name="cancellationToken">Abbruchsteuerung.</param>
    Task<IReadOnlyList<ConversionJob>> ListAsync(
        JobStatus? status, int limit, int offset, CancellationToken cancellationToken = default);

    /// <summary>Zählt die Aufträge, die zum Filter passen.</summary>
    Task<int> CountAsync(JobStatus? status, CancellationToken cancellationToken = default);

    /// <summary>
    /// Prüft, ob eine Stimme noch von einem unerledigten Auftrag benötigt wird.
    /// </summary>
    Task<bool> HasActiveJobForVoiceAsync(VoiceId voiceId, CancellationToken cancellationToken = default);

    /// <summary>Entfernt einen Auftrag vollständig.</summary>
    Task DeleteAsync(JobId id, CancellationToken cancellationToken = default);
}
