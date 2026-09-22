using ChangeMyVoice.Domain.Audio;
using ChangeMyVoice.Domain.Jobs;
using ChangeMyVoice.Domain.Voices;

namespace ChangeMyVoice.Application.Ports;

/// <summary>
/// Ein undurchsichtiger Verweis auf eine abgelegte Audiodatei.
/// </summary>
/// <remarks>
/// Die Inferenz läuft in einem Python-Prozess und braucht echte Dateipfade — ein
/// Datenstrom genügt dort nicht. Damit die Anwendungsschicht trotzdem nicht mit
/// Pfaden hantiert, wandert der Ort als undurchsichtige Zeichenkette durch; nur
/// die Adapter wissen, was darin steht.
/// </remarks>
/// <param name="Locator">Die vom Adapter vergebene Ortsangabe.</param>
public readonly record struct AudioArtifactRef(string Locator);

/// <summary>Verwaltet die dauerhaft abgelegten Referenzstimmen.</summary>
public interface IVoiceStorage
{
    /// <summary>
    /// Legt den Ablageort für den Master einer Stimme an und liefert ihn zurück,
    /// damit die Normalisierung direkt dorthin schreiben kann. So wandert kein
    /// Datenstrom und kein Dateipfad durch die Anwendungsschicht.
    /// </summary>
    AudioArtifactRef ReserveMaster(VoiceId id);

    /// <summary>Liefert den Verweis auf den Master einer Stimme.</summary>
    AudioArtifactRef GetMaster(VoiceId id);

    /// <summary>Prüft, ob der Master vorhanden ist.</summary>
    bool MasterExists(VoiceId id);

    /// <summary>Entfernt die Dateien einer Stimme.</summary>
    Task DeleteAsync(VoiceId id, CancellationToken cancellationToken = default);
}

/// <summary>Ein Arbeitsverzeichnis für genau einen Auftrag.</summary>
/// <param name="JobId">Der zugehörige Auftrag.</param>
/// <param name="Source">Die normalisierte Quellaufnahme.</param>
/// <param name="Reference">Die auf die Zielrate gebrachte Referenz.</param>
/// <param name="Output">Der Ort, an den das Ergebnis geschrieben wird.</param>
public sealed record JobWorkspace(
    JobId JobId,
    AudioArtifactRef Source,
    AudioArtifactRef Reference,
    AudioArtifactRef Output);

/// <summary>Ein vorgefundenes Arbeitsverzeichnis auf der Platte.</summary>
/// <param name="JobId">
/// Der zugehörige Auftrag, oder <c>null</c>, wenn der Verzeichnisname keine
/// gültige Kennung ist — etwa nach einem Eingriff von außen.
/// </param>
/// <param name="Location">
/// Der Ort des Verzeichnisses. Wird gebraucht, um auch ein Verzeichnis
/// entfernen zu können, dessen Name sich nicht mehr deuten lässt.
/// </param>
/// <param name="CreatedAtUtc">Der Anlagezeitpunkt des Verzeichnisses.</param>
public sealed record JobWorkspaceEntry(
    JobId? JobId, AudioArtifactRef Location, DateTimeOffset CreatedAtUtc);

/// <summary>Verwaltet die Arbeitsverzeichnisse der Aufträge.</summary>
public interface IJobWorkspaceStore
{
    /// <summary>Legt ein Arbeitsverzeichnis an.</summary>
    JobWorkspace Create(JobId jobId);

    /// <summary>Liefert das Arbeitsverzeichnis eines Auftrags.</summary>
    JobWorkspace Get(JobId jobId);

    /// <summary>Öffnet die Ergebnisdatei zum Lesen.</summary>
    Task<Stream?> OpenOutputAsync(JobId jobId, CancellationToken cancellationToken = default);

    /// <summary>Entfernt das Arbeitsverzeichnis samt Inhalt.</summary>
    Task DeleteAsync(JobId jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Entfernt ein vorgefundenes Verzeichnis anhand seines Ortes. Wird für
    /// Verzeichnisse gebraucht, deren Name keine gültige Auftragskennung ist.
    /// </summary>
    Task DeleteAsync(JobWorkspaceEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Listet alle vorhandenen Arbeitsverzeichnisse auf.</summary>
    IReadOnlyList<JobWorkspaceEntry> Enumerate();

    /// <summary>Entfernt Reste im Zwischenablageordner, die älter sind als angegeben.</summary>
    Task<int> PurgeTemporaryAsync(
        DateTimeOffset olderThanUtc, CancellationToken cancellationToken = default);
}

/// <summary>Ermittelt die Eigenschaften einer Audiodatei.</summary>
public interface IAudioProbe
{
    /// <summary>
    /// Liest die Eigenschaften, oder <c>null</c>, wenn die Datei keinen
    /// verwertbaren Audiostrom enthält.
    /// </summary>
    Task<AudioProperties?> ProbeAsync(
        AudioArtifactRef artifact, CancellationToken cancellationToken = default);
}

/// <summary>Bringt Audiodateien in das vom Modell erwartete Format.</summary>
public interface IAudioNormalizer
{
    /// <summary>
    /// Schreibt die Eingabe als mono-PCM-WAV in der Zielabtastrate.
    /// </summary>
    /// <param name="source">Die Eingabe in beliebigem unterstütztem Format.</param>
    /// <param name="destination">Das Ziel.</param>
    /// <param name="format">Das gewünschte Zielformat.</param>
    /// <param name="maxDuration">Optionale Kürzung der Länge.</param>
    /// <param name="cancellationToken">Abbruchsteuerung.</param>
    Task NormalizeAsync(
        AudioArtifactRef source,
        AudioArtifactRef destination,
        TargetAudioFormat format,
        TimeSpan? maxDuration = null,
        CancellationToken cancellationToken = default);
}
