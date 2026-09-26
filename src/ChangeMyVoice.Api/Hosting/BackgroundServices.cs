using ChangeMyVoice.Adapters.Notifications;
using ChangeMyVoice.Adapters.Storage;
using ChangeMyVoice.Application.Ports;
using ChangeMyVoice.Application.UseCases.Jobs;
using ChangeMyVoice.Application.UseCases.Maintenance;
using Microsoft.Extensions.Options;

namespace ChangeMyVoice.Api.Hosting;

/// <summary>Einstellungen des Betriebs.</summary>
public sealed class WorkerOptions
{
    /// <summary>Der Abschnitt in der Konfiguration.</summary>
    public const string SectionName = "Worker";

    /// <summary>Wie viele Aufträge höchstens warten dürfen.</summary>
    public int QueueCapacity { get; set; } = 64;

    /// <summary>
    /// Wie viele Läufe gleichzeitig stattfinden dürfen.
    /// </summary>
    /// <remarks>
    /// Die Voreinstellung 1 folgt init.md §23: Die Grafikbeschleunigung teilt
    /// sich den Speicher mit dem restlichen System, mehrere gleichzeitige Läufe
    /// sind daher zunächst nicht vorgesehen. Höhere Werte gelten als
    /// experimentell und sollten auf dem jeweiligen Rechner erprobt werden.
    /// </remarks>
    public int MaxConcurrentInference { get; set; } = 1;

    /// <summary>Wie oft der Aufräumlauf stattfindet.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// Sorgt dafür, dass nur ein Dienst gleichzeitig auf den Daten arbeitet.
/// </summary>
/// <remarks>
/// Ohne diese Sperre wäre die Annahme „ein laufender Auftrag mit fremder
/// Dienstkennung stammt aus einem Absturz“ nicht haltbar: Zwei gleichzeitig
/// laufende Dienste würden sich gegenseitig die Aufträge abräumen.
/// </remarks>
public sealed class SingleInstanceGuard(
    IOptions<StorageOptions> storage,
    IHostApplicationLifetime lifetime,
    ILogger<SingleInstanceGuard> logger) : IHostedService, IDisposable
{
    private FileStream? _lock;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(storage.Value.DataRoot, ".lock");
        Directory.CreateDirectory(storage.Value.DataRoot);

        try
        {
            _lock = new FileStream(
                path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            logger.LogCritical(
                "Es läuft bereits ein Dienst auf demselben Datenverzeichnis ({Path}). "
                + "Dieser Start wird abgebrochen.", storage.Value.DataRoot);

            lifetime.StopApplication();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _lock?.Dispose();
        _lock = null;
    }
}

/// <summary>Prüft beim Start, ob Modell und Werkzeuge bereitstehen.</summary>
public sealed class InferenceReadinessService(
    IInferenceEnvironmentProbe probe,
    ReadinessState state,
    ILogger<InferenceReadinessService> logger) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var checks = await probe.CheckAsync(cancellationToken).ConfigureAwait(false);
        state.Update(checks);

        foreach (var check in checks.Where(c => !c.IsHealthy))
        {
            // Früh und deutlich melden: Sonst fällt der erste Auftrag nach
            // Minuten Ladezeit auf die Nase, und niemand weiß warum.
            logger.LogError("Bereitschaftsprüfung '{Check}' fehlgeschlagen: {Detail}",
                check.Name, check.Detail);
        }

        if (checks.All(c => c.IsHealthy))
        {
            logger.LogInformation("Alle Bereitschaftsprüfungen bestanden.");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Hält das Ergebnis der letzten Bereitschaftsprüfung fest.</summary>
public sealed class ReadinessState
{
    private IReadOnlyList<ReadinessCheck> _checks = [];

    /// <summary>Die zuletzt ermittelten Prüfergebnisse.</summary>
    public IReadOnlyList<ReadinessCheck> Checks => _checks;

    /// <summary>Ob der Dienst Aufträge annehmen kann.</summary>
    public bool IsReady => _checks.Count > 0 && _checks.All(c => c.IsHealthy);

    /// <summary>Übernimmt ein neues Ergebnis.</summary>
    public void Update(IReadOnlyList<ReadinessCheck> checks) => _checks = checks;
}

/// <summary>
/// Bringt die Aufträge beim Start in einen stimmigen Zustand.
/// </summary>
/// <remarks>
/// Läuft vor dem Arbeiter, damit wiedereingereihte Aufträge nicht mitten in die
/// Wiederherstellung hinein bearbeitet werden. Das ist die Stelle, an der ein
/// harter Absturz aufgeräumt wird — ein Abschlusscode beim Beenden hilft dort
/// nicht, weil er nach einem <c>SIGKILL</c> gar nicht mehr läuft.
/// </remarks>
public sealed class StartupRecoveryService(
    IServiceScopeFactory scopeFactory,
    ILogger<StartupRecoveryService> logger) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();

        var recovery = scope.ServiceProvider.GetRequiredService<IRecoverInterruptedJobs>();
        var cleanup = scope.ServiceProvider.GetRequiredService<ICleanupJobArtifacts>();

        try
        {
            await recovery.ExecuteAsync(cancellationToken).ConfigureAwait(false);

            // Direkt im Anschluss ein vollständiger Aufräumlauf: Er erwischt die
            // Verzeichnisse, zu denen es gar keinen Datensatz mehr gibt.
            await cleanup.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Ein Fehler beim Aufräumen darf den Dienst nicht am Starten hindern.
            logger.LogError(ex, "Die Wiederherstellung beim Start ist fehlgeschlagen.");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Räumt regelmäßig liegengebliebene Arbeitsdateien weg.</summary>
public sealed class JobJanitorService(
    IServiceScopeFactory scopeFactory,
    IOptions<WorkerOptions> options,
    ILogger<JobJanitorService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.CleanupInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var cleanup = scope.ServiceProvider.GetRequiredService<ICleanupJobArtifacts>();
                await cleanup.ExecuteAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Ein misslungener Durchlauf darf den Aufräumdienst nicht beenden,
                // sonst bliebe die Platte für immer vollaufend.
                logger.LogError(ex, "Ein Aufräumlauf ist fehlgeschlagen.");
            }
        }
    }
}

/// <summary>
/// Arbeitet die Warteschlange ab.
/// </summary>
/// <remarks>
/// Die Reihenfolge ergibt sich aus der Anzahl der Leser, nicht aus einer Sperre:
/// Bei der Voreinstellung liest genau eine Schleife aus der Warteschlange,
/// wodurch nie mehr als ein Lauf gleichzeitig stattfindet (init.md §23).
/// </remarks>
public sealed class ConversionWorker(
    IJobQueue queue,
    IServiceScopeFactory scopeFactory,
    IOptions<WorkerOptions> options,
    ILogger<ConversionWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Math.Max(1, options.Value.MaxConcurrentInference);

        if (workers > 1)
        {
            logger.LogWarning(
                "Es sind {Count} gleichzeitige Läufe eingestellt. Das ist nicht erprobt — "
                + "die Grafikbeschleunigung teilt sich den Speicher mit dem System.", workers);
        }

        var tasks = Enumerable.Range(0, workers)
            .Select(_ => ConsumeAsync(stoppingToken))
            .ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        await foreach (var jobId in queue.DequeueAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var process = scope.ServiceProvider.GetRequiredService<IProcessConversionJob>();
                await process.ExecuteAsync(jobId, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Niemals aus der Schleife fallen: Sonst bliebe die Warteschlange
                // für alle folgenden Aufträge stehen.
                logger.LogError(ex, "Auftrag {JobId} konnte nicht bearbeitet werden.", jobId);
            }
        }
    }
}

/// <summary>Stellt die Benachrichtigungen über beendete Aufträge zu.</summary>
public sealed class WebhookDeliveryService(WebhookDispatcher dispatcher) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await dispatcher.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
