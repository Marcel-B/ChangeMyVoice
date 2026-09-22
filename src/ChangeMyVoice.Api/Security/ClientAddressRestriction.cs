using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ChangeMyVoice.Api.Security;

/// <summary>
/// Weist Anfragen ab, die nicht vom Gateway kommen.
/// </summary>
/// <remarks>
/// Zweite Verteidigungslinie neben dem Zugangsschlüssel: Die Vorgabe lautet,
/// dass Aufträge ausschließlich über das Gateway laufen. Fällt ein Schlüssel
/// einmal in falsche Hände, hilft es, wenn er nur von einer bekannten Adresse
/// aus überhaupt verwendbar ist.
/// </remarks>
public sealed class ClientAddressRestrictionMiddleware(
    RequestDelegate next,
    IOptionsMonitor<ApiSecurityOptions> options,
    ILogger<ClientAddressRestrictionMiddleware> logger)
{
    /// <summary>Verarbeitet eine Anfrage.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var allowed = options.CurrentValue.AllowedClientAddresses;

        // Keine Liste bedeutet: keine Einschränkung. Das ist die Voreinstellung
        // für die Entwicklung; im Betrieb steht dort die Adresse des Gateways.
        if (allowed.Count == 0 || IsAllowed(context.Connection.RemoteIpAddress, allowed))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        logger.LogWarning(
            "Anfrage von nicht zugelassener Adresse {RemoteIp} abgewiesen.",
            context.Connection.RemoteIpAddress);

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "Zugriff nicht erlaubt",
            Detail = "Anfragen werden nur über das dafür vorgesehene Gateway angenommen.",
            Extensions = { ["code"] = "FORBIDDEN_CLIENT" },
        }, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>Prüft eine Adresse gegen die Freigabeliste.</summary>
    /// <remarks>
    /// Unterstützt einzelne Adressen und CIDR-Bereiche. IPv4-Adressen, die als
    /// IPv6 abgebildet ankommen, werden vorher zurückgewandelt — sonst schlüge
    /// die Prüfung fehl, obwohl die Adresse stimmt.
    /// </remarks>
    internal static bool IsAllowed(IPAddress? address, IReadOnlyList<string> allowed)
    {
        if (address is null)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        foreach (var entry in allowed)
        {
            var value = entry.Trim();

            if (value.Contains('/', StringComparison.Ordinal))
            {
                if (IPNetwork.TryParse(value, out var network) && network.Contains(address))
                {
                    return true;
                }
            }
            else if (IPAddress.TryParse(value, out var single))
            {
                if (single.IsIPv4MappedToIPv6)
                {
                    single = single.MapToIPv4();
                }

                if (single.Equals(address))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
