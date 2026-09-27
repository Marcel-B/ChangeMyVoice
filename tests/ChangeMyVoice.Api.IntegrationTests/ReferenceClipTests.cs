using System.Net;
using System.Net.Http.Json;
using ChangeMyVoice.Api.Contracts;
using Shouldly;

namespace ChangeMyVoice.Api.IntegrationTests;

public class ReferenceClipApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static MultipartFormDataContent Upload(byte[] audio, string? start, string? end)
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent($"Ausschnitt-{Guid.NewGuid():n}"), "label" },
            { new ByteArrayContent(audio), "file", "referenz.wav" },
        };

        if (start is not null)
        {
            content.Add(new StringContent(start), "startSeconds");
        }

        if (end is not null)
        {
            content.Add(new StringContent(end), "endSeconds");
        }

        return content;
    }

    [SkippableFact]
    public async Task Nur_der_gewaehlte_Ausschnitt_wird_abgelegt()
    {
        Skip.IfNot(TestAudio.Available, "ffmpeg ist nicht verfügbar.");
        var client = factory.CreateAuthenticatedClient();

        var response = await client.PostAsync(
            "/api/v1/voices", Upload(TestAudio.Tone(seconds: 40), "12.5", "22.5"));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var voice = await response.Content.ReadFromJsonAsync<ReferenceVoiceResponse>();
        voice!.Original.DurationSeconds.ShouldBe(40, tolerance: 0.1);
        voice.Stored.DurationSeconds.ShouldBe(10, tolerance: 0.1);
    }

    [SkippableFact]
    public async Task Ein_Ende_vor_dem_Beginn_wird_mit_400_abgelehnt()
    {
        Skip.IfNot(TestAudio.Available, "ffmpeg ist nicht verfügbar.");

        var response = await factory.CreateAuthenticatedClient().PostAsync(
            "/api/v1/voices", Upload(TestAudio.Tone(), "8", "4"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [SkippableFact]
    public async Task Ein_Beginn_hinter_dem_Ende_der_Aufnahme_wird_abgelehnt()
    {
        Skip.IfNot(TestAudio.Available, "ffmpeg ist nicht verfügbar.");

        var response = await factory.CreateAuthenticatedClient().PostAsync(
            "/api/v1/voices", Upload(TestAudio.Tone(seconds: 10), "30", null));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("INVALID_AUDIO");
    }
}
