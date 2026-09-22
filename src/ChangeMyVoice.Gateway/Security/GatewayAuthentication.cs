using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ChangeMyVoice.Gateway.Security;

/// <summary>Einstellungen des Authentifizierungsverfahrens.</summary>
public sealed class GatewayAuthenticationOptions : AuthenticationSchemeOptions
{
    /// <summary>Der Name des Verfahrens.</summary>
    public const string SchemeName = "ApiKey";

    /// <summary>Der Kopfzeilenname, in dem der Schlüssel erwartet wird.</summary>
    public const string HeaderName = "X-Api-Key";
}

/// <summary>
/// Prüft den Schlüssel des Aufrufers.
/// </summary>
/// <remarks>
/// Aufrufer authentifizieren sich mit ihrem eigenen Schlüssel. Der Schlüssel der
/// dahinterliegenden API wird erst beim Weiterleiten gesetzt und ist hier
/// bewusst nicht Teil der Prüfung — so kann ein einzelner Aufrufer entzogen
/// werden, ohne alle anderen zu stören.
/// </remarks>
public sealed class GatewayAuthenticationHandler(
    IOptionsMonitor<GatewayAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptionsMonitor<GatewayOptions> gateway)
    : AuthenticationHandler<GatewayAuthenticationOptions>(options, logger, encoder)
{
    /// <summary>Der Anspruch, unter dem der Name des Aufrufers steht.</summary>
    public const string ClientNameClaim = "client_id";

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(GatewayAuthenticationOptions.HeaderName, out var values) ||
            string.IsNullOrWhiteSpace(values.ToString()))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var client = FindClient(values.ToString(), gateway.CurrentValue.Clients);

        if (client is null)
        {
            Logger.LogWarning(
                "Zugang mit ungültigem Schlüssel abgewiesen, Quelle {RemoteIp}.",
                Context.Connection.RemoteIpAddress);

            return Task.FromResult(AuthenticateResult.Fail("Ungültiger Zugangsschlüssel."));
        }

        // Der Name landet als Anspruch im Kontext und damit in den Protokollen:
        // So bleibt nachvollziehbar, wer welchen Auftrag ausgelöst hat.
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, client.Name), new Claim(ClientNameClaim, client.Name)],
            GatewayAuthenticationOptions.SchemeName);

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(identity), GatewayAuthenticationOptions.SchemeName)));
    }

    /// <summary>Sucht den Aufrufer zu einem Schlüssel in gleichbleibender Zeit.</summary>
    internal static GatewayClient? FindClient(
        string presentedKey, IReadOnlyList<GatewayClient> clients)
    {
        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(presentedKey));
        GatewayClient? match = null;

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
