using ChangeMyVoice.Adapters.Inference;
using ChangeMyVoice.Adapters.Notifications;
using ChangeMyVoice.Adapters.Persistence;
using ChangeMyVoice.Adapters.Storage;
using ChangeMyVoice.Api.Contracts;
using ChangeMyVoice.Api.Endpoints;
using ChangeMyVoice.Api.Hosting;
using ChangeMyVoice.Api.OpenApi;
using ChangeMyVoice.Api.Security;
using ChangeMyVoice.Application.Ports;
using ChangeMyVoice.Application.UseCases.Jobs;
using ChangeMyVoice.Application.UseCases.Maintenance;
using ChangeMyVoice.Application.UseCases.Voices;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Der Schlüssel und die Freigabeliste gehören nicht ins Repository. Im Betrieb
// liegen sie in einer Datei außerhalb, auf die nur der Dienstbenutzer Zugriff hat.
var configDirectory = Environment.GetEnvironmentVariable("CHANGEMYVOICE_CONFIG_DIR");
if (!string.IsNullOrWhiteSpace(configDirectory))
{
    builder.Configuration.AddJsonFile(
        Path.Combine(configDirectory, "appsettings.Production.json"),
        optional: true, reloadOnChange: true);
}

// ---------------------------------------------------------------------------
// Einstellungen. ValidateOnStart sorgt dafür, dass ein unvollständig
// eingerichteter Dienst sofort und mit klarer Meldung abbricht, statt später
// beim ersten Auftrag zu scheitern.
// ---------------------------------------------------------------------------
builder.Services.AddOptions<StorageOptions>()
    .Bind(builder.Configuration.GetSection(StorageOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<PersistenceOptions>()
    .Bind(builder.Configuration.GetSection(PersistenceOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<InferenceOptions>()
    .Bind(builder.Configuration.GetSection(InferenceOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<AudioToolingOptions>()
    .Bind(builder.Configuration.GetSection(AudioToolingOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<WorkerOptions>()
    .Bind(builder.Configuration.GetSection(WorkerOptions.SectionName));

builder.Services.AddOptions<WebhookOptions>()
    .Bind(builder.Configuration.GetSection(WebhookOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<ApiSecurityOptions>()
    .Bind(builder.Configuration.GetSection(ApiSecurityOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(
        options => options.Clients.Count > 0,
        "Ohne hinterlegten Zugangsschlüssel würde der Dienst ungeschützt laufen.")
    .ValidateOnStart();

// ---------------------------------------------------------------------------
// Adapter
// ---------------------------------------------------------------------------
builder.Services.AddSingleton<SqliteConnectionFactory>();
builder.Services.AddSingleton<IReferenceVoiceRepository, SqliteReferenceVoiceRepository>();
builder.Services.AddSingleton<IConversionJobRepository, SqliteConversionJobRepository>();

builder.Services.AddSingleton<IVoiceStorage, FileSystemVoiceStorage>();
builder.Services.AddSingleton<FileSystemJobWorkspaceStore>();
builder.Services.AddSingleton<IJobWorkspaceStore>(
    sp => sp.GetRequiredService<FileSystemJobWorkspaceStore>());

builder.Services.AddSingleton<IAudioProbe, FfprobeAudioProbe>();
builder.Services.AddSingleton<IAudioNormalizer, FfmpegAudioNormalizer>();
builder.Services.AddSingleton<IVoiceConversionEngine, MlxVcConversionEngine>();
builder.Services.AddSingleton<IInferenceEnvironmentProbe, PythonEnvironmentProbe>();
builder.Services.AddSingleton<IOrphanProcessKiller, ProcessTreeKiller>();

// Keine Weiterleitungen: Sonst könnte ein freigegebener Rechner den Aufruf zu
// einem Ziel umlenken, das die Freigabeliste gerade ausschließen soll.
builder.Services.AddHttpClient(WebhookDispatcher.HttpClientName, client =>
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ChangeMyVoice-Webhook/1"))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<WebhookDispatcher>();
builder.Services.AddSingleton<IJobNotifier>(sp => sp.GetRequiredService<WebhookDispatcher>());

builder.Services.AddSingleton<IServiceInstance, ServiceInstance>();
builder.Services.AddSingleton<IJobQueue>(sp =>
    new ChannelJobQueue(sp.GetRequiredService<IOptions<WorkerOptions>>().Value.QueueCapacity));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ReadinessState>();
builder.Services.AddSingleton(sp =>
{
    var worker = sp.GetRequiredService<IOptions<WorkerOptions>>().Value;

    return new CleanupSettings
    {
        // Bewusst großzügiger als das Zeitlimit eines Laufs: Ein Auftrag, der
        // gerade noch rechtzeitig fertig würde, soll nicht vom Aufräumlauf
        // abgeschossen werden.
        StuckJobThreshold =
            sp.GetRequiredService<IOptions<InferenceOptions>>().Value.Timeout + worker.CleanupInterval,
    };
});

// ---------------------------------------------------------------------------
// Anwendungsfälle
// ---------------------------------------------------------------------------
builder.Services.AddScoped<IAddReferenceVoice, AddReferenceVoice>();
builder.Services.AddScoped<IListReferenceVoices, ListReferenceVoices>();
builder.Services.AddScoped<IGetReferenceVoice, GetReferenceVoice>();
builder.Services.AddScoped<IGetReferenceVoiceAudio, GetReferenceVoiceAudio>();
builder.Services.AddScoped<IDeleteReferenceVoice, DeleteReferenceVoice>();
builder.Services.AddScoped<ISubmitConversionJob, SubmitConversionJob>();
builder.Services.AddScoped<IListConversionJobs, ListConversionJobs>();
builder.Services.AddScoped<IGetJobStatus, GetJobStatus>();
builder.Services.AddScoped<IGetJobResult, GetJobResult>();
builder.Services.AddScoped<ICancelJob, CancelJob>();
builder.Services.AddScoped<IProcessConversionJob, ProcessConversionJob>();
builder.Services.AddScoped<ICleanupJobArtifacts, CleanupJobArtifacts>();
builder.Services.AddScoped<IRecoverInterruptedJobs, RecoverInterruptedJobs>();

// ---------------------------------------------------------------------------
// Hintergrunddienste. Die Reihenfolge ist die Startreihenfolge und damit
// bedeutsam: Erst die Einzelinstanz-Sperre, dann die Bereitschaftsprüfung, dann
// die Wiederherstellung — und erst danach der Arbeiter, damit
// wiedereingereihte Aufträge nicht mitten in die Wiederherstellung laufen.
// ---------------------------------------------------------------------------
builder.Services.AddHostedService<SingleInstanceGuard>();
builder.Services.AddHostedService<InferenceReadinessService>();
builder.Services.AddHostedService<StartupRecoveryService>();
builder.Services.AddHostedService<JobJanitorService>();
builder.Services.AddHostedService<ConversionWorker>();
builder.Services.AddHostedService<WebhookDeliveryService>();

// Beim Herunterfahren etwas Luft lassen, damit laufende Aufträge noch ordentlich
// als unterbrochen vermerkt werden können.
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(20));

// ---------------------------------------------------------------------------
// HTTP
// ---------------------------------------------------------------------------
builder.Services
    .AddAuthentication(ApiKeyAuthenticationOptions.SchemeName)
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationOptions.SchemeName, _ => { });

builder.Services.AddAuthorization();
builder.Services.AddProblemDetails();

// Beide Grenzen muessen gesetzt sein, und zwar auf denselben Wert.
// FormOptions begrenzt den Multipart-Inhalt, Kestrel den Rumpf der Anfrage
// insgesamt. Fehlt die zweite, greift deren Standardwert von 30 MB: Der Upload
// bricht dann mitten im Uebertragen ab, obwohl die Einstellung viel mehr
// erlaubt -- und der Aufrufer sieht nur einen Verbindungsabbruch, keine
// verstaendliche Meldung.
builder.Services.AddOptions<FormOptions>().Configure<IOptions<StorageOptions>>(
    (form, storage) => form.MultipartBodyLengthLimit = storage.Value.MaxUploadBytes);

builder.Services.AddOptions<KestrelServerOptions>().Configure<IOptions<StorageOptions>>(
    (kestrel, storage) => kestrel.Limits.MaxRequestBodySize = storage.Value.MaxUploadBytes);

builder.Services.AddOpenApi("v1", options =>
{
    options.AddDocumentTransformer<ApiKeySecuritySchemeTransformer>();
    options.AddOperationTransformer<ApiKeyOperationTransformer>();
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

// Vor der Anmeldung: Wer gar nicht erst vom Gateway kommt, soll auch keine
// Schlüssel ausprobieren können.
app.UseMiddleware<ClientAddressRestrictionMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi("/openapi/{documentName}.json");
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/openapi/v1.json", "ChangeMyVoice v1");
    options.DocumentTitle = "ChangeMyVoice";
});

var api = app.MapGroup("/api/v1")
    .RequireAuthorization()
    .WithTags("ChangeMyVoice");

api.MapVoiceEndpoints();
api.MapJobEndpoints();

app.MapGet("/health/live", () => TypedResults.Ok(new HealthResponse("healthy", [])))
    .AllowAnonymous()
    .WithName("HealthLive")
    .WithSummary("Lebenszeichen")
    .WithDescription(
        "Meldet, dass der Dienst antwortet. Bewusst ohne Zugangsschlüssel, damit die "
        + "Dienstüberwachung ihn abfragen kann, und ohne Angaben zur Umgebung.")
    .WithTags("Betrieb")
    .Produces<HealthResponse>();

app.MapGet("/health/ready", (ReadinessState state) =>
    {
        var response = new HealthResponse(
            state.IsReady ? "healthy" : "unhealthy",
            state.Checks.Select(c => new HealthCheckResponse(c.Name, c.IsHealthy, c.Detail)).ToArray());

        return state.IsReady
            ? Results.Ok(response)
            : Results.Json(response, statusCode: StatusCodes.Status503ServiceUnavailable);
    })
    .RequireAuthorization()
    .WithName("HealthReady")
    .WithSummary("Bereitschaft prüfen")
    .WithDescription(
        "Meldet, ob Modell, Python-Umgebung und Audiowerkzeuge verfügbar sind. "
        + "Schlägt eine Prüfung fehl, ist das früh sichtbar statt erst beim ersten Auftrag.")
    .WithTags("Betrieb")
    .Produces<HealthResponse>()
    .Produces<HealthResponse>(StatusCodes.Status503ServiceUnavailable);

app.Run();

/// <summary>Einstiegspunkt, für die Integrationstests sichtbar gemacht.</summary>
public partial class Program;
