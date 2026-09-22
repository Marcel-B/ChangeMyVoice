using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ChangeMyVoice.Api.Contracts;
using Shouldly;

namespace ChangeMyVoice.Api.IntegrationTests;

/// <summary>Erzeugt Testaudio über ffmpeg.</summary>
internal static class TestAudio
{
    public static bool Available { get; } = Probe();

    public static byte[] Tone(double seconds = 10, int sampleRate = 44100, int channels = 1)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():n}.wav");

        try
        {
            Run(
            [
                "-hide_banner", "-v", "error", "-y", "-f", "lavfi",
                "-i", $"sine=frequency=440:sample_rate={sampleRate}:duration={seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                "-ac", channels.ToString(), path,
            ]);

            return File.ReadAllBytes(path);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static void Run(string[] arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo("ffmpeg")
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(info)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(error);
        }
    }

    private static bool Probe()
    {
        try
        {
            Run(["-version"]);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public class SecurityTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Ohne_Zugangsschluessel_wird_abgewiesen()
    {
        var response = await factory.CreateClient().GetAsync("/api/v1/voices");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Ein_falscher_Zugangsschluessel_wird_abgewiesen()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "falsch");

        var response = await client.GetAsync("/api/v1/voices");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Das_Lebenszeichen_ist_ohne_Schluessel_erreichbar()
    {
        // Damit die Dienstüberwachung es abfragen kann.
        var response = await factory.CreateClient().GetAsync("/health/live");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Die_Bereitschaft_verlangt_einen_Schluessel()
    {
        var response = await factory.CreateClient().GetAsync("/health/ready");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Mit_gueltigem_Schluessel_wird_geantwortet()
    {
        var response = await factory.CreateAuthenticatedClient().GetAsync("/api/v1/voices");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}

public class OpenApiContractTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Jeder_Endpunkt_erscheint_vollstaendig_in_der_Beschreibung()
    {
        // Die CLAUDE.md verlangt, dass ein Endpunkt erst als fertig gilt, wenn er
        // dort vollständig auftaucht. Dieser Test macht daraus eine Zusicherung,
        // die beim Bauen bricht, statt nur einer Absicht.
        using var document = System.Text.Json.JsonDocument.Parse(
            await factory.CreateClient().GetStringAsync("/openapi/v1.json"));

        var paths = document.RootElement.GetProperty("paths");

        var expected = new[]
        {
            "/api/v1/voices", "/api/v1/voices/{voiceId}",
            "/api/v1/jobs", "/api/v1/jobs/{jobId}", "/api/v1/jobs/{jobId}/result",
            "/health/live", "/health/ready",
        };

        foreach (var path in expected)
        {
            paths.TryGetProperty(path, out _).ShouldBeTrue($"'{path}' fehlt in der Beschreibung.");
        }
    }

    [Fact]
    public async Task Das_Schluesselverfahren_ist_beschrieben()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            await factory.CreateClient().GetStringAsync("/openapi/v1.json"));

        var scheme = document.RootElement
            .GetProperty("components").GetProperty("securitySchemes").GetProperty("ApiKey");

        scheme.GetProperty("type").GetString().ShouldBe("apiKey");
        scheme.GetProperty("in").GetString().ShouldBe("header");
        scheme.GetProperty("name").GetString().ShouldBe("X-Api-Key");
    }

    [Fact]
    public async Task Jeder_geschuetzte_Endpunkt_nennt_seine_Zugangsanforderung_und_401()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            await factory.CreateClient().GetStringAsync("/openapi/v1.json"));

        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            if (path.Name.StartsWith("/health/live", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var operation in path.Value.EnumerateObject())
            {
                operation.Value.TryGetProperty("security", out _)
                    .ShouldBeTrue($"{operation.Name.ToUpperInvariant()} {path.Name} nennt keine Zugangsanforderung.");

                operation.Value.GetProperty("responses").TryGetProperty("401", out _)
                    .ShouldBeTrue($"{operation.Name.ToUpperInvariant()} {path.Name} beschreibt keine 401.");
            }
        }
    }

    [Fact]
    public async Task Jeder_Endpunkt_beschreibt_mindestens_eine_erfolgreiche_Antwort()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            await factory.CreateClient().GetStringAsync("/openapi/v1.json"));

        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                var codes = operation.Value.GetProperty("responses")
                    .EnumerateObject().Select(r => r.Name).ToArray();

                codes.ShouldContain(
                    code => code.StartsWith('2'),
                    $"{operation.Name.ToUpperInvariant()} {path.Name} beschreibt keinen Erfolgsfall.");
            }
        }
    }

    [Fact]
    public async Task Jeder_Endpunkt_traegt_eine_Zusammenfassung()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            await factory.CreateClient().GetStringAsync("/openapi/v1.json"));

        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                operation.Value.TryGetProperty("summary", out var summary).ShouldBeTrue(
                    $"{operation.Name.ToUpperInvariant()} {path.Name} hat keine Zusammenfassung.");

                summary.GetString().ShouldNotBeNullOrWhiteSpace();
            }
        }
    }
}
