using System.ComponentModel.DataAnnotations;

namespace ChangeMyVoice.Adapters.Storage;

/// <summary>Wo der Dienst seine Dateien ablegt.</summary>
public sealed class StorageOptions
{
    /// <summary>Der Abschnitt in der Konfiguration.</summary>
    public const string SectionName = "Storage";

    /// <summary>
    /// Das Wurzelverzeichnis. Darunter liegen <c>voices/</c>, <c>jobs/</c> und
    /// <c>tmp/</c>.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string DataRoot { get; set; } = string.Empty;

    /// <summary>Die größte erlaubte Uploadgröße in Bytes.</summary>
    [Range(1024, long.MaxValue)]
    public long MaxUploadBytes { get; set; } = 200L * 1024 * 1024;

    /// <summary>Das Verzeichnis der Referenzstimmen.</summary>
    public string VoicesRoot => Path.Combine(DataRoot, "voices");

    /// <summary>Das Verzeichnis der Arbeitsdateien.</summary>
    public string JobsRoot => Path.Combine(DataRoot, "jobs");

    /// <summary>Das Verzeichnis für Uploads in Arbeit.</summary>
    public string TempRoot => Path.Combine(DataRoot, "tmp");
}
