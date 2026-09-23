using System.ComponentModel.DataAnnotations;

namespace ChangeMyVoice.Adapters.Inference;

/// <summary>Wo die Audiowerkzeuge liegen.</summary>
public sealed class AudioToolingOptions
{
    /// <summary>Der Abschnitt in der Konfiguration.</summary>
    public const string SectionName = "AudioTooling";

    /// <summary>Der Aufruf für ffmpeg.</summary>
    [Required(AllowEmptyStrings = false)]
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>Der Aufruf für ffprobe.</summary>
    [Required(AllowEmptyStrings = false)]
    public string FfprobePath { get; set; } = "ffprobe";

    /// <summary>Wie lange eine Umwandlung höchstens dauern darf.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Wie der Python-Lauf gestartet wird.
/// </summary>
/// <remarks>
/// Alle Pfade sind konfigurierbar und nirgends fest verdrahtet. Die bestehende
/// Python-Umgebung wird ausschließlich benutzt, nie verändert — init.md §30
/// untersagt eigenmächtige Aktualisierungen ausdrücklich.
/// </remarks>
public sealed class InferenceOptions
{
    /// <summary>Der Abschnitt in der Konfiguration.</summary>
    public const string SectionName = "Inference";

    /// <summary>
    /// Der Python-Interpreter aus der eingerichteten Umgebung, üblicherweise
    /// <c>~/mlx-vc/.venv/bin/python</c>. Bewusst der Interpreter selbst und
    /// nicht <c>uv run</c>, weil letzteres die Umgebung neu abgleicht.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string PythonExecutable { get; set; } = string.Empty;

    /// <summary>Das Wrapper-Skript dieses Projekts.</summary>
    [Required(AllowEmptyStrings = false)]
    public string ScriptPath { get; set; } = string.Empty;

    /// <summary>
    /// Das Arbeitsverzeichnis, üblicherweise <c>~/mlx-vc</c>. mlx-vc erwartet
    /// Seed-VC standardmäßig daneben unter <c>../seed-vc-ref</c>.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Der Pfad zum Seed-VC-Verzeichnis. Wird als <c>SEED_VC_PATH</c> gesetzt,
    /// damit der Lauf nicht auf relative Vorgaben angewiesen ist.
    /// </summary>
    public string? SeedVcPath { get; set; }

    /// <summary>
    /// Der Zwischenspeicher für die Modelldateien, gesetzt als
    /// <c>HF_HUB_CACHE</c>, damit alle Läufe dieselben Checkpoints verwenden.
    /// </summary>
    public string? HuggingFaceCachePath { get; set; }

    /// <summary>Das Modell-Backend.</summary>
    public string Backend { get; set; } = "seed-vc";

    /// <summary>
    /// Wie lange ein Lauf höchstens dauern darf.
    /// </summary>
    /// <remarks>
    /// Bemessen an der längsten erlaubten Quellaufnahme: Sieben Minuten
    /// Material brauchen nach den Messungen auf Apple Silicon rund 95 Minuten,
    /// zwei Stunden lassen also etwas Luft für einen ausgelasteten Rechner.
    /// <para>
    /// Die Grenze ist ein Notausstieg für hängende Läufe, kein Qualitätsmaß.
    /// Sie zu knapp zu setzen ist teuer: Das Ergebnis entsteht erst ganz am
    /// Ende in einem Zug, ein Abbruch verwirft also die gesamte gerechnete
    /// Zeit, ohne dass etwas Verwertbares übrig bleibt.
    /// </para>
    /// </remarks>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(120);
}
