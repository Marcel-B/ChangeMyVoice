using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ChangeMyVoice.Api.Security;

/// <summary>Ein zugelassener Aufrufer.</summary>
public sealed class ApiClient
{
    /// <summary>Ein sprechender Name, der in den Protokollen erscheint.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Der SHA-256-Wert des Schlüssels in Hexadezimalschreibweise. Bewusst nur
    /// der Streuwert: So steht der Schlüssel selbst in keiner Konfigurationsdatei.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string KeySha256 { get; set; } = string.Empty;
}

/// <summary>Einstellungen der Zugangsprüfung.</summary>
public sealed class ApiSecurityOptions
{
    /// <summary>Der Abschnitt in der Konfiguration.</summary>
    public const string SectionName = "Security";

    /// <summary>Die zugelassenen Aufrufer.</summary>
    [MinLength(1, ErrorMessage = "Es muss mindestens ein Zugangsschlüssel hinterlegt sein.")]
    public List<ApiClient> Clients { get; set; } = [];

    /// <summary>
    /// Die Adressen, von denen Anfragen angenommen werden — üblicherweise die des
    /// Gateways. Ist die Liste leer, findet keine Prüfung statt.
    /// </summary>
    public List<string> AllowedClientAddresses { get; set; } = [];
}

/// <summary>Einstellungen des Authentifizierungsverfahrens.</summary>
public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    /// <summary>Der Name des Verfahrens.</summary>
    public const string SchemeName = "ApiKey";

    /// <summary>Der Kopfzeilenname, in dem der Schlüssel erwartet wird.</summary>
    public const string HeaderName = "X-Api-Key";
}

/// <summary>
/// Prüft den mitgesendeten Zugangsschlüssel.
/// </summary>
/// <remarks>
/// Bewusst ein Authentifizierungsverfahren und keine eigene Middleware: So
/// greifen <c>RequireAuthorization</c>, <c>AllowAnonymous</c>, die Unterscheidung
/// zwischen 401 und 403 sowie <c>HttpContext.User</c> für die Protokollierung —
/// und die Prüfung lässt sich in den Tests sauber ersetzen.
/// </remarks>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptionsMonitor<ApiSecurityOptions> security)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyAuthenticationOptions.HeaderName, out var values))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var presented = values.ToString();

        if (string.IsNullOrWhiteSpace(presented))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var client = FindClient(presented, security.CurrentValue.Clients);

        if (client is null)
        {
            // Ob der Schlüssel fehlte oder falsch war, bleibt bewusst offen —
            // die Unterscheidung wäre ein Hinweis für jemanden, der probiert.
            Logger.LogWarning(
                "Zugang mit ungültigem Schlüssel abgewiesen, Quelle {RemoteIp}.",
                Context.Connection.RemoteIpAddress);

            return Task.FromResult(AuthenticateResult.Fail("Ungültiger Zugangsschlüssel."));
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, client.Name), new Claim("client_id", client.Name)],
            ApiKeyAuthenticationOptions.SchemeName);

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(identity), ApiKeyAuthenticationOptions.SchemeName)));
    }

    /// <summary>
    /// Sucht den Aufrufer zu einem Schlüssel.
    /// </summary>
    /// <remarks>
    /// Der Vergleich läuft über alle Einträge und in gleichbleibender Zeit, damit
    /// sich aus der Antwortdauer nichts über den richtigen Schlüssel ableiten lässt.
    /// </remarks>
    internal static ApiClient? FindClient(string presentedKey, IReadOnlyList<ApiClient> clients)
    {
        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(presentedKey));
        ApiClient? match = null;

        foreach (var client in clients)
        {
            byte[] expected;

            try
            {
                expected = Convert.FromHexString(client.KeySha256);
            }
            catch (FormatException)
            {
                continue;
            }

            if (expected.Length == presented.Length &&
                CryptographicOperations.FixedTimeEquals(expected, presented))
            {
                match = client;
            }
        }

        return match;
    }

    /// <summary>Bildet den Streuwert eines Schlüssels, etwa zum Einrichten.</summary>
    public static string ComputeHash(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
}
