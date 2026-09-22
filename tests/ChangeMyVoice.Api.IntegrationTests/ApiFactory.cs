using System.Net.Http.Json;
using ChangeMyVoice.Application.Ports;
using ChangeMyVoice.Domain.Jobs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ChangeMyVoice.Api.IntegrationTests;

/// <summary>
/// Eine Konvertierungsmaschine, die statt eines Modelllaufs eine Datei kopiert.
/// </summary>
/// <remarks>
/// Der echte Lauf braucht Seed-VC, die Grafikbeschleunigung und mehrere Minuten.
/// Für den Durchstich durch die Schnittstelle zählt, dass Annahme, Warteschlange,
/// Zustandswechsel und Abruf zusammenspielen — nicht die Klangqualität.
/// </remarks>
public sealed class FakeConversionEngine : IVoiceConversionEngine
{
    /// <summary>Was der Lauf melden soll.</summary>
    public ConversionOutcome Outcome { get; set; } = ConversionOutcome.Success();

    /// <summary>Wie lange der Lauf sich Zeit lässt.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    /// <inheritdoc />
    public async Task<ConversionOutcome> ConvertAsync(
        ConversionRequest request,
        Action<int>? onProcessStarted = null,
        CancellationToken cancellationToken = default)
    {
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
        }

        if (Outcome.IsSuccess)
        {
            // Die Quelle als Ergebnis durchreichen: So entsteht eine echte,
            // lesbare WAV-Datei, an der sich Größe und Prüfsumme nachmessen lassen.
            File.Copy(request.Source.Locator, request.Output.Locator, overwrite: true);
        }

        return Outcome;
    }
}

/// <summary>Meldet die Umgebung immer als bereit.</summary>
public sealed class FakeEnvironmentProbe : IInferenceEnvironmentProbe
{
    /// <inheritdoc />
    public Task<IReadOnlyList<ReadinessCheck>> CheckAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ReadinessCheck>>([new ReadinessCheck("fake", true)]);
}

/// <summary>Startet die Anwendung mit einem eigenen Datenverzeichnis.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>Der Zugangsschlüssel, mit dem die Tests arbeiten.</summary>
    public const string ApiKey = "testschluessel";

    private const string ApiKeyHash =
        "0a4bc2deb0e0dfa1c2f8e5e8d70b3e53fa4c8d14f2e4d5e3c1a6f8b9d2e0c7a4";

    private readonly string _dataRoot = Path.Combine(
        Path.GetTempPath(), "cmv-it", Guid.NewGuid().ToString("n"));

    /// <summary>Die eingesetzte Konvertierungsmaschine.</summary>
    public FakeConversionEngine Engine { get; } = new();

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.UseSetting("Storage:DataRoot", _dataRoot);
        builder.UseSetting("Storage:MaxUploadBytes", "5242880");
        builder.UseSetting("Persistence:DatabasePath", Path.Combine(_dataRoot, "test.db"));

        // Die Pfade müssen gesetzt sein, weil die Prüfung beim Start sonst
        // abbricht — verwendet werden sie dank der ersetzten Maschine nicht.
        builder.UseSetting("Inference:PythonExecutable", "/usr/bin/false");
        builder.UseSetting("Inference:ScriptPath", "/usr/bin/false");
        builder.UseSetting("Inference:WorkingDirectory", Path.GetTempPath());

        builder.UseSetting("Security:Clients:0:Name", "test");
        builder.UseSetting("Security:Clients:0:KeySha256", ComputeHash(ApiKey));

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IVoiceConversionEngine>();
            services.AddSingleton<IVoiceConversionEngine>(Engine);

            services.RemoveAll<IInferenceEnvironmentProbe>();
            services.AddSingleton<IInferenceEnvironmentProbe, FakeEnvironmentProbe>();
        });
    }

    private static string ComputeHash(string key) =>
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    /// <summary>Erzeugt einen Zugriff mit gültigem Schlüssel.</summary>
    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        return client;
    }

    /// <summary>Wartet, bis ein Auftrag einen Endzustand erreicht hat.</summary>
    public static async Task<string> WaitForTerminalAsync(
        HttpClient client, string jobId, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));

        while (DateTime.UtcNow < deadline)
        {
            var response = await client.GetFromJsonAsync<Contracts.JobResponse>(
                $"/api/v1/jobs/{jobId}").ConfigureAwait(false);

            if (response!.Status is "COMPLETED" or "FAILED" or "CANCELLED")
            {
                return response.Status;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException($"Auftrag {jobId} erreichte keinen Endzustand.");
    }

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync().ConfigureAwait(false);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }
}
