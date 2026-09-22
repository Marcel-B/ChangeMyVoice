using ChangeMyVoice.Gateway.Security;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace ChangeMyVoice.Gateway.Proxy;

/// <summary>
/// Tauscht beim Weiterleiten den Schlüssel des Aufrufers gegen den der API.
/// </summary>
/// <remarks>
/// Das ist der Kern der Vorgabe „Aufträge nur über das Gateway“: Der Schlüssel
/// der Mac-API verlässt diesen Dienst nie, und ein Aufrufer kann die API auch
/// dann nicht direkt ansprechen, wenn er sie im Netz erreichen könnte. Der
/// mitgesendete Schlüssel des Aufrufers wird ausdrücklich entfernt, statt
/// einfach überschrieben zu werden — so bleibt nichts davon übrig, falls sich
/// die Reihenfolge der Umformungen einmal ändert.
/// </remarks>
internal sealed class UpstreamKeyTransformProvider(IOptions<GatewayOptions> options)
    : ITransformProvider
{
    /// <inheritdoc />
    public void ValidateRoute(TransformRouteValidationContext context)
    {
        // Keine routenbezogenen Einstellungen zu prüfen.
    }

    /// <inheritdoc />
    public void ValidateCluster(TransformClusterValidationContext context)
    {
        // Keine clusterbezogenen Einstellungen zu prüfen.
    }

    /// <inheritdoc />
    public void Apply(TransformBuilderContext context)
    {
        context.AddRequestTransform(transform =>
        {
            transform.ProxyRequest.Headers.Remove(GatewayAuthenticationOptions.HeaderName);
            transform.ProxyRequest.Headers.TryAddWithoutValidation(
                GatewayAuthenticationOptions.HeaderName, options.Value.UpstreamApiKey);

            // Den Namen des Aufrufers mitgeben, damit sich ein Auftrag auch in
            // den Protokollen der API zuordnen lässt.
            var clientName = transform.HttpContext.User
                .FindFirst(GatewayAuthenticationHandler.ClientNameClaim)?.Value;

            if (!string.IsNullOrEmpty(clientName))
            {
                transform.ProxyRequest.Headers.TryAddWithoutValidation(
                    "X-Forwarded-Client", clientName);
            }

            return ValueTask.CompletedTask;
        });
    }
}
