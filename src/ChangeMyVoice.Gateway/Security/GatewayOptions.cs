using System.ComponentModel.DataAnnotations;

namespace ChangeMyVoice.Gateway.Security;

/// <summary>Ein Aufrufer, der das Gateway verwenden darf.</summary>
public sealed class GatewayClient
{
    /// <summary>Ein sprechender Name, der in den Protokollen erscheint.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Der SHA-256-Wert des Schlüssels in Hexadezimalschreibweise.</summary>
    [Required(AllowEmptyStrings = false)]
    public string KeySha256 { get; set; } = string.Empty;

    /// <summary>Wie viele Anfragen dieser Aufrufer pro Minute stellen darf.</summary>
    [Range(1, 10000)]
    public int RequestsPerMinute { get; set; } = 60;
}

/// <summary>Einstellungen des Gateways.</summary>
public sealed class GatewayOptions
{
    /// <summary>Der Abschnitt in der Konfiguration.</summary>
    public const string SectionName = "Gateway";

    /// <summary>Die Adresse der Mac-API, etwa <c>http://10.0.0.5:5080</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    public string UpstreamBaseAddress { get; set; } = string.Empty;

    /// <summary>
    /// Der Zugangsschlüssel der Mac-API.
    /// </summary>
    /// <remarks>
    /// Er wird ausschließlich hier gehalten und gegenüber der API gesetzt. Kein
    /// Aufrufer bekommt ihn zu sehen — dadurch bleibt die API auch dann
    /// unerreichbar, wenn ein Schlüssel eines Aufrufers bekannt wird.
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    public string UpstreamApiKey { get; set; } = string.Empty;

    /// <summary>Die zugelassenen Aufrufer.</summary>
    [MinLength(1, ErrorMessage = "Es muss mindestens ein Zugangsschlüssel hinterlegt sein.")]
    public List<GatewayClient> Clients { get; set; } = [];

    /// <summary>Die größte erlaubte Anfragengröße in Bytes.</summary>
    public long MaxRequestBytes { get; set; } = 200L * 1024 * 1024;

    /// <summary>Wie lange auf die API gewartet wird.</summary>
    public TimeSpan UpstreamTimeout { get; set; } = TimeSpan.FromMinutes(5);
}
