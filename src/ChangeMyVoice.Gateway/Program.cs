using System.Net;
using System.Threading.RateLimiting;
using ChangeMyVoice.Gateway.Proxy;
using ChangeMyVoice.Gateway.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms.Builder;

// Der Bereitschaftstest des Containers laeuft ueber die Anwendung selbst. Das
// Laufzeitabbild bringt weder curl noch wget mit, und beides nur dafuer
// nachzuinstallieren wuerde die Angriffsflaeche unnoetig vergroessern.
if (args.Contains("--healthcheck"))
{
    var port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORT") ?? "8080";

    using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

    try
    {
        var probeResponse = await probe.GetAsync($"http://localhost:{port}/health/live");
        return probeResponse.IsSuccessStatusCode ? 0 : 1;
    }
    catch
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<GatewayOptions>()
    .Bind(builder.Configuration.GetSection(GatewayOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(
        options => options.Clients.Count > 0,
        "Ohne hinterlegten Zugangsschlüssel würde das Gateway ungeschützt laufen.")
    .ValidateOnStart();

var gateway = builder.Configuration
    .GetSection(GatewayOptions.SectionName).Get<GatewayOptions>() ?? new GatewayOptions();

// ---------------------------------------------------------------------------
// Weiterleitung. Die Endpunkte werden unverändert durchgereicht: ein Vertrag,
// eine Beschreibung, nichts, was auseinanderlaufen könnte.
// ---------------------------------------------------------------------------
builder.Services
    .AddReverseProxy()
    .LoadFromMemory(
        [
            new RouteConfig
            {
                RouteId = "api",
                ClusterId = "changemyvoice",
                // Der Pfad bleibt unveraendert: Aufrufer und API sprechen
                // denselben Vertrag.
                Match = new RouteMatch { Path = "/api/v1/{**catch-all}" },
            },
        ],
        [
            new ClusterConfig
            {
                ClusterId = "changemyvoice",
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    ["mac"] = new() { Address = gateway.UpstreamBaseAddress },
                },
                HttpRequest = new Yarp.ReverseProxy.Forwarder.ForwarderRequestConfig
                {
                    // Ein Konvertierungslauf kann dauern; der Abruf des
                    // Ergebnisses ist trotzdem ein gewöhnlicher Aufruf.
                    ActivityTimeout = gateway.UpstreamTimeout,
                },
            },
        ]);

builder.Services.AddSingleton<ITransformProvider, UpstreamKeyTransformProvider>();

builder.Services
    .AddAuthentication(GatewayAuthenticationOptions.SchemeName)
    .AddScheme<GatewayAuthenticationOptions, GatewayAuthenticationHandler>(
        GatewayAuthenticationOptions.SchemeName, _ => { });

builder.Services.AddAuthorization();
builder.Services.AddProblemDetails();

// Begrenzung je Aufrufer: Das hält Last vom Mac fern, der ohnehin nur einen
// Lauf gleichzeitig verarbeiten kann.
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var name = context.User.FindFirst(GatewayAuthenticationHandler.ClientNameClaim)?.Value
            ?? context.Connection.RemoteIpAddress?.ToString()
            ?? "unbekannt";

        var configured = gateway.Clients
            .FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));

        return RateLimitPartition.GetFixedWindowLimiter(name, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = configured?.RequestsPerMinute ?? 30,
            Window = TimeSpan.FromMinutes(1),
        });
    });
});

builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = gateway.MaxRequestBytes);
builder.Services.Configure<KestrelServerOptions>(
    o => o.Limits.MaxRequestBodySize = gateway.MaxRequestBytes);

var app = builder.Build();

app.UseExceptionHandler();

// Ist die API nicht erreichbar, soll eine verständliche Auskunft zurückkommen
// statt eines nackten Verbindungsfehlers.
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (HttpRequestException ex)
    {
        app.Logger.LogError(ex, "Die Konvertierungs-API ist nicht erreichbar.");

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status503ServiceUnavailable,
            Title = "Dienst nicht erreichbar",
            Detail = "Die Konvertierungs-API antwortet derzeit nicht.",
            Extensions = { ["code"] = "UPSTREAM_UNAVAILABLE" },
        });
    }
});

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapReverseProxy(proxy => proxy.Use(async (context, next) =>
{
    await next();

    // YARP beantwortet einen nicht erreichbaren Zielrechner mit 502 und einem
    // leeren Rumpf. Das Gateway macht daraus eine maschinenlesbare Auskunft:
    // Der Aufrufer soll erkennen koennen, dass es an der Gegenstelle liegt und
    // ein spaeterer Versuch sinnvoll ist.
    var error = context.Features.Get<Yarp.ReverseProxy.Forwarder.IForwarderErrorFeature>();

    if (error is not null && !context.Response.HasStarted)
    {
        app.Logger.LogError(
            error.Exception,
            "Die Konvertierungs-API ist nicht erreichbar ({Error}).", error.Error);

        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;

        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status503ServiceUnavailable,
            Title = "Dienst nicht erreichbar",
            Detail = "Die Konvertierungs-API antwortet derzeit nicht.",
            Extensions = { ["code"] = "UPSTREAM_UNAVAILABLE" },
        });
    }
})).RequireAuthorization();

app.MapGet("/health/live", () => Results.Ok(new { status = "healthy" }))
    .AllowAnonymous();

app.Run();

return 0;

/// <summary>Einstiegspunkt, für die Tests sichtbar gemacht.</summary>
public partial class Program;
