using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using ChangeMyVoice.Application.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChangeMyVoice.Adapters.Notifications;

/// <summary>
/// Stellt das Ende eines Auftrags per HTTP-POST an die Adresse zu, die der
/// Aufrufer beim Anlegen genannt hat.
/// </summary>
/// <remarks>
/// Eingereiht wird sofort, zugestellt im Hintergrund über <see cref="RunAsync" />.
/// Jede Zustellung läuft für sich, damit ein Empfänger, der gerade nicht
/// antwortet und Wiederholungen braucht, die anderen nicht aufhält.
///
/// Nachrichten, die beim Beenden des Dienstes noch warten, gehen verloren. Das
/// ist hinnehmbar, weil der Aufruf nur ein Anstoß ist: Der Zustand bleibt über
/// die Statusabfrage verbindlich, und ein Aufrufer, der nichts hört, fragt dort
/// nach.
/// </remarks>
public sealed class WebhookDispatcher : IJobNotifier
{
    /// <summary>Der Name des HTTP-Clients.</summary>
    public const string HttpClientName = "webhooks";

    /// <summary>Der Kopf, der die Art der Nachricht nennt.</summary>
    public const string EventHeader = "X-ChangeMyVoice-Event";

    /// <summary>Die Art der Nachricht.</summary>
    public const string JobFinishedEvent = "job.finished";

    private readonly Channel<JobFinishedNotification> _channel;
    private readonly IHttpClientFactory _httpClients;
    private readonly WebhookOptions _options;
    private readonly ILogger<WebhookDispatcher> _logger;

    /// <summary>Erzeugt den Zusteller.</summary>
    public WebhookDispatcher(
        IHttpClientFactory httpClients,
        IOptions<WebhookOptions> options,
        ILogger<WebhookDispatcher> logger)
    {
        _httpClients = httpClients;
        _options = options.Value;
        _logger = logger;
        _channel = Channel.CreateBounded<JobFinishedNotification>(
            new BoundedChannelOptions(_options.QueueCapacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropWrite,
            });
    }

    /// <inheritdoc />
    public void Enqueue(JobFinishedNotification notification)
    {
        if (!_channel.Writer.TryWrite(notification))
        {
            _logger.LogWarning(
                "Benachrichtigung für Auftrag {JobId} verworfen, die Warteschlange ist voll.",
                notification.Job.Id);
        }
    }

    /// <summary>Stellt eingereihte Nachrichten zu, bis der Dienst endet.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await foreach (var notification in _channel.Reader
                           .ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            _ = DeliverAsync(notification, cancellationToken);
        }
    }

    /// <summary>
    /// Stellt eine Nachricht zu, mit Wiederholungen. Liefert, ob es geklappt hat.
    /// </summary>
    internal async Task<bool> DeliverAsync(
        JobFinishedNotification notification, CancellationToken cancellationToken)
    {
        var payload = WebhookPayload.From(notification.Job);
        var delay = _options.RetryDelay;

        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            if (attempt > 1)
            {
                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }

                delay *= 2;
            }

            var (delivered, retry, reason) = await SendAsync(
                notification.Target.Value, payload, cancellationToken).ConfigureAwait(false);

            if (delivered)
            {
                _logger.LogInformation(
                    "Ende von Auftrag {JobId} an {Target} gemeldet.",
                    payload.JobId, notification.Target.Value.Host);
                return true;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            _logger.LogWarning(
                "Meldung von Auftrag {JobId} an {Target} fehlgeschlagen (Versuch {Attempt}/{Max}): {Reason}",
                payload.JobId, notification.Target.Value.Host, attempt, _options.MaxAttempts, reason);

            if (!retry)
            {
                return false;
            }
        }

        return false;
    }

    private async Task<(bool Delivered, bool Retry, string? Reason)> SendAsync(
        Uri target, WebhookPayload payload, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);

        try
        {
            var client = _httpClients.CreateClient(HttpClientName);

            using var request = new HttpRequestMessage(HttpMethod.Post, target)
            {
                Content = JsonContent.Create(payload, options: JsonSerializerOptions.Web),
            };
            request.Headers.Add(EventHeader, JobFinishedEvent);

            using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return (true, false, null);
            }

            // Ein Empfänger, der die Nachricht ablehnt, wird sie beim nächsten Mal
            // ebenso ablehnen. Nur Überlastung und Serverfehler lohnen einen
            // weiteren Versuch.
            var status = response.StatusCode;
            var retry = (int)status >= 500 ||
                        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

            return (false, retry, $"HTTP {(int)status}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, true, $"keine Antwort nach {_options.Timeout.TotalSeconds:0} s");
        }
        catch (OperationCanceledException)
        {
            return (false, false, "Dienst wird beendet");
        }
        catch (HttpRequestException ex)
        {
            return (false, true, ex.Message);
        }
    }
}
