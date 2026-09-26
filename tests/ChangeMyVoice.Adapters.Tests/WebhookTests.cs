using System.Net;
using System.Text.Json;
using ChangeMyVoice.Adapters.Notifications;
using ChangeMyVoice.Application.Ports;
using ChangeMyVoice.Application.UseCases.Jobs;
using ChangeMyVoice.Domain.Jobs;
using ChangeMyVoice.Domain.Voices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace ChangeMyVoice.Adapters.Tests;

/// <summary>Ein Empfänger, der vorgegebene Antworten liefert und mitschreibt.</summary>
public sealed class RecordingHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _answers = new();

    public List<(HttpRequestMessage Request, string Body)> Received { get; } = [];

    public TaskCompletionSource FirstRequest { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Answer(HttpStatusCode status) => _answers.Enqueue(() => new HttpResponseMessage(status));

    public void Hang() => _answers.Enqueue(() => throw new InvalidOperationException("hang"));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        Received.Add((request, body));
        FirstRequest.TrySetResult();

        var answer = _answers.Count > 0 ? _answers.Dequeue() : () => new HttpResponseMessage(HttpStatusCode.OK);

        try
        {
            return answer();
        }
        catch (InvalidOperationException)
        {
            // Wie ein Empfänger, der die Verbindung annimmt und nie antwortet.
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw;
        }
    }
}

public class WebhookDispatcherTests
{
    private readonly RecordingHandler _handler = new();

    private readonly WebhookOptions _options = new()
    {
        MaxAttempts = 3,
        RetryDelay = TimeSpan.Zero,
        Timeout = TimeSpan.FromSeconds(5),
    };

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private WebhookDispatcher Sut() => new(
        new Factory(_handler), Options.Create(_options), NullLogger<WebhookDispatcher>.Instance);

    private static JobFinishedNotification Completed()
    {
        var job = ConversionJob.Create(
            JobId.New(), VoiceId.New(), "Anna", ConversionOptions.Default,
            DateTimeOffset.UnixEpoch, Guid.NewGuid(),
            webhookUrl: WebhookUrl.Rehydrate("https://yue.example/hook"));
        job.Start(DateTimeOffset.UnixEpoch, Guid.NewGuid());
        job.Complete(DateTimeOffset.UnixEpoch.AddMinutes(3), 4096, "abc");

        return new JobFinishedNotification(job.WebhookUrl!, ConversionJobView.From(job));
    }

    [Fact]
    public async Task Das_Ende_wird_als_JSON_an_die_Adresse_geschickt()
    {
        var notification = Completed();

        (await Sut().DeliverAsync(notification, CancellationToken.None)).ShouldBeTrue();

        var (request, body) = _handler.Received.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.RequestUri.ShouldBe(new Uri("https://yue.example/hook"));
        request.Headers.GetValues(WebhookDispatcher.EventHeader).ShouldHaveSingleItem()
            .ShouldBe("job.finished");

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        root.GetProperty("event").GetString().ShouldBe("job.finished");
        root.GetProperty("jobId").GetString().ShouldBe(notification.Job.Id.ToString());
        root.GetProperty("status").GetString().ShouldBe("COMPLETED");
        root.GetProperty("statusUrl").GetString().ShouldBe($"/api/v1/jobs/{notification.Job.Id}");
        root.GetProperty("resultUrl").GetString().ShouldBe($"/api/v1/jobs/{notification.Job.Id}/result");
        root.GetProperty("resultSizeBytes").GetInt64().ShouldBe(4096);
        root.GetProperty("resultSha256").GetString().ShouldBe("abc");
    }

    [Fact]
    public void Ein_Fehler_erscheint_in_der_Schreibweise_der_Statusantwort()
    {
        var job = ConversionJob.Create(
            JobId.New(), VoiceId.New(), "Anna", ConversionOptions.Default,
            DateTimeOffset.UnixEpoch, Guid.NewGuid());
        job.Fail(DateTimeOffset.UnixEpoch, new JobError(ConversionErrorCode.OutOfMemory, "voll"));

        var payload = WebhookPayload.From(ConversionJobView.From(job));

        payload.Status.ShouldBe("FAILED");
        payload.Error!.Code.ShouldBe("OUT_OF_MEMORY");
        payload.ResultUrl.ShouldBeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task Bei_Ueberlastung_wird_wiederholt(HttpStatusCode status)
    {
        _handler.Answer(status);
        _handler.Answer(HttpStatusCode.NoContent);

        (await Sut().DeliverAsync(Completed(), CancellationToken.None)).ShouldBeTrue();

        _handler.Received.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Found)]
    public async Task Eine_Ablehnung_wird_nicht_wiederholt(HttpStatusCode status)
    {
        // Eine Weiterleitung zählt dazu: Ihr zu folgen hieße, ein Ziel außerhalb
        // der Freigabeliste aufzurufen.
        _handler.Answer(status);

        (await Sut().DeliverAsync(Completed(), CancellationToken.None)).ShouldBeFalse();

        _handler.Received.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Nach_der_Hoechstzahl_an_Versuchen_wird_aufgegeben()
    {
        for (var i = 0; i < 5; i++)
        {
            _handler.Answer(HttpStatusCode.BadGateway);
        }

        (await Sut().DeliverAsync(Completed(), CancellationToken.None)).ShouldBeFalse();

        _handler.Received.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Ein_Empfaenger_ohne_Antwort_zaehlt_als_Fehlversuch()
    {
        _options.Timeout = TimeSpan.FromMilliseconds(100);
        _handler.Hang();

        (await Sut().DeliverAsync(Completed(), CancellationToken.None)).ShouldBeTrue();

        _handler.Received.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Eingereihte_Nachrichten_werden_im_Hintergrund_zugestellt()
    {
        var sut = Sut();
        using var stop = new CancellationTokenSource();
        var running = sut.RunAsync(stop.Token);

        sut.Enqueue(Completed());

        await _handler.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _handler.Received.ShouldHaveSingleItem();

        await stop.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(running);
    }
}
