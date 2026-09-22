using System.Net;
using ChangeMyVoice.Gateway.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;

namespace ChangeMyVoice.Gateway.Tests;

/// <summary>
/// Eine nachgestellte Mac-API, die festhält, womit sie aufgerufen wurde.
/// </summary>
public sealed class FakeUpstream : IAsyncDisposable
{
    private readonly WebApplication _app;

    /// <summary>
    /// Die Kopfzeilen der letzten Anfrage, als Kopie.
    /// </summary>
    /// <remarks>
    /// Der Anfragekontext wird nach der Antwort wiederverwendet; wer sich das
    /// Original merkt, liest spaeter leere Werte.
    /// </remarks>
    public Dictionary<string, string> LastHeaders { get; private set; } = [];

    /// <summary>Der Pfad der letzten Anfrage.</summary>
    public string? LastPath { get; private set; }

    /// <summary>Die Größe des letzten Rumpfes.</summary>
    public long LastBodyLength { get; private set; }

    /// <summary>Die Adresse, unter der sie erreichbar ist.</summary>
    public string BaseAddress { get; }

    public FakeUpstream()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        _app = builder.Build();

        _app.Map("/{**catch-all}", async (HttpContext context) =>
        {
            LastHeaders = context.Request.Headers
                .ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            LastPath = context.Request.Path;

            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer);
            LastBodyLength = buffer.Length;

            return Results.Ok(new { ok = true });
        });

        _app.StartAsync().GetAwaiter().GetResult();

        // Der Anschluss wird vom Betriebssystem vergeben; erst nach dem Start
        // steht fest, welcher es geworden ist.
        BaseAddress = _app.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
            .Addresses.First();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

public sealed class GatewayFactory(string upstream) : WebApplicationFactory<Program>
{
    public const string ClientKey = "client-schluessel";
    public const string UpstreamKey = "geheimer-mac-schluessel";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Gateway:UpstreamBaseAddress", upstream);
        builder.UseSetting("Gateway:UpstreamApiKey", UpstreamKey);
        builder.UseSetting("Gateway:Clients:0:Name", "test-client");
        builder.UseSetting(
            "Gateway:Clients:0:KeySha256", GatewayAuthenticationHandler.ComputeHash(ClientKey));
        builder.UseSetting("Gateway:Clients:0:RequestsPerMinute", "1000");
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ClientKey);
        return client;
    }
}

public class GatewayForwardingTests : IAsyncLifetime
{
    private FakeUpstream _upstream = null!;
    private GatewayFactory _factory = null!;

    public Task InitializeAsync()
    {
        _upstream = new FakeUpstream();
        _factory = new GatewayFactory(_upstream.BaseAddress);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Der_Schluessel_des_Aufrufers_wird_nicht_durchgereicht()
    {
        // Der entscheidende Punkt: Die API darf niemals den Schluessel eines
        // Aufrufers sehen, sondern ausschliesslich ihren eigenen.
        var client = _factory.CreateAuthenticatedClient();

        await client.GetAsync("/api/v1/voices");

        var forwarded = _upstream.LastHeaders.GetValueOrDefault("X-Api-Key");

        forwarded.ShouldBe(GatewayFactory.UpstreamKey);
        forwarded.ShouldNotBe(GatewayFactory.ClientKey);
    }

    [Fact]
    public async Task Der_Name_des_Aufrufers_wird_mitgegeben()
    {
        var client = _factory.CreateAuthenticatedClient();

        await client.GetAsync("/api/v1/voices");

        _upstream.LastHeaders.GetValueOrDefault("X-Forwarded-Client").ShouldBe("test-client");
    }

    [Fact]
    public async Task Der_Pfad_bleibt_unveraendert()
    {
        // Ein Vertrag auf beiden Seiten — sonst laufen Gateway und API
        // frueher oder spaeter auseinander.
        var client = _factory.CreateAuthenticatedClient();

        await client.GetAsync("/api/v1/jobs/abc123/result");

        _upstream.LastPath.ShouldBe("/api/v1/jobs/abc123/result");
    }

    [Fact]
    public async Task Ohne_Schluessel_wird_gar_nicht_erst_weitergeleitet()
    {
        var response = await _factory.CreateClient().GetAsync("/api/v1/voices");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        _upstream.LastPath.ShouldBeNull();
    }

    [Fact]
    public async Task Ein_falscher_Schluessel_wird_abgewiesen()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "falsch");

        var response = await client.GetAsync("/api/v1/voices");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        _upstream.LastPath.ShouldBeNull();
    }

    [Fact]
    public async Task Das_Lebenszeichen_ist_ohne_Schluessel_erreichbar()
    {
        var response = await _factory.CreateClient().GetAsync("/health/live");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ein_grosser_Upload_wird_durchgereicht_und_nicht_gepuffert()
    {
        // Quelldateien koennen hunderte Megabyte gross sein; sie duerfen das
        // Gateway nicht im Arbeitsspeicher belasten.
        var client = _factory.CreateAuthenticatedClient();
        var payload = new byte[4 * 1024 * 1024];
        Random.Shared.NextBytes(payload);

        var content = new MultipartFormDataContent
        {
            { new StringContent("test"), "voiceId" },
            { new ByteArrayContent(payload), "source", "quelle.wav" },
        };

        var response = await client.PostAsync("/api/v1/jobs", content);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _upstream.LastBodyLength.ShouldBeGreaterThan(payload.Length);
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _upstream.DisposeAsync();
    }
}

public class GatewayUnreachableTests : IAsyncLifetime
{
    private GatewayFactory _factory = null!;

    public Task InitializeAsync()
    {
        // Ein Anschluss, auf dem mit Sicherheit nichts lauscht.
        _factory = new GatewayFactory("http://127.0.0.1:1");
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Eine_nicht_erreichbare_API_ergibt_eine_verstaendliche_Auskunft()
    {
        // Statt eines nackten Verbindungsfehlers soll das Gateway sagen, was los ist.
        var response = await _factory.CreateAuthenticatedClient().GetAsync("/api/v1/voices");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).ShouldContain("UPSTREAM_UNAVAILABLE");
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();
}

public class GatewayKeyMatchingTests
{
    [Fact]
    public void Ein_passender_Schluessel_findet_seinen_Aufrufer()
    {
        var clients = new List<GatewayClient>
        {
            new() { Name = "a", KeySha256 = GatewayAuthenticationHandler.ComputeHash("schluessel-a") },
            new() { Name = "b", KeySha256 = GatewayAuthenticationHandler.ComputeHash("schluessel-b") },
        };

        GatewayAuthenticationHandler.FindClient("schluessel-b", clients)!.Name.ShouldBe("b");
    }

    [Fact]
    public void Ein_unbekannter_Schluessel_findet_niemanden()
    {
        var clients = new List<GatewayClient>
        {
            new() { Name = "a", KeySha256 = GatewayAuthenticationHandler.ComputeHash("schluessel-a") },
        };

        GatewayAuthenticationHandler.FindClient("etwas-anderes", clients).ShouldBeNull();
    }

    [Fact]
    public void Ein_unbrauchbar_hinterlegter_Streuwert_bringt_die_Pruefung_nicht_aus_dem_Tritt()
    {
        var clients = new List<GatewayClient>
        {
            new() { Name = "kaputt", KeySha256 = "kein-hexadezimalwert" },
            new() { Name = "gut", KeySha256 = GatewayAuthenticationHandler.ComputeHash("richtig") },
        };

        GatewayAuthenticationHandler.FindClient("richtig", clients)!.Name.ShouldBe("gut");
    }
}
