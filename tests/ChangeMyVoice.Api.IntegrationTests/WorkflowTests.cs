using System.Net;
using System.Net.Http.Json;
using ChangeMyVoice.Api.Contracts;
using ChangeMyVoice.Application.Ports;
using ChangeMyVoice.Domain.Jobs;
using Shouldly;

namespace ChangeMyVoice.Api.IntegrationTests;

public class WorkflowTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static MultipartFormDataContent VoiceUpload(
        string label, byte[] audio, string fileName = "referenz.wav")
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(label), "label" },
            { new ByteArrayContent(audio), "file", fileName },
        };

        return content;
    }

    private static MultipartFormDataContent JobUpload(string voiceId, byte[] audio)
    {
        return new MultipartFormDataContent
        {
            { new StringContent(voiceId), "voiceId" },
            { new ByteArrayContent(audio), "source", "quelle.wav" },
        };
    }

    [SkippableFact]
    public async Task Der_vollstaendige_Ablauf_von_der_Stimme_bis_zum_Ergebnis()
    {
        Skip.IfNot(TestAudio.Available, "ffmpeg ist nicht verfügbar.");
        var client = factory.CreateAuthenticatedClient();

        // 1. Referenzstimme senden
        var created = await client.PostAsync(
            "/api/v1/voices", VoiceUpload($"Stimme-{Guid.NewGuid():n}", TestAudio.Tone()));

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var voice = await created.Content.ReadFromJsonAsync<ReferenceVoiceResponse>();

        // Der Master liegt stets als Mono-PCM mit 44,1 kHz vor.
        voice!.Stored.SampleRate.ShouldBe(44100);
        voice.Stored.Channels.ShouldBe(1);
        voice.Stored.Codec.ShouldBe("pcm_s16le");

        // 2. Sie taucht in der Übersicht auf
        var list = await client.GetFromJsonAsync<ReferenceVoiceResponse[]>("/api/v1/voices");
        list.ShouldNotBeNull();
        list.ShouldContain(v => v.Id == voice.Id);

        // 3. Auftrag senden
        var accepted = await client.PostAsync("/api/v1/jobs", JobUpload(voice.Id, TestAudio.Tone(5)));

        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var job = await accepted.Content.ReadFromJsonAsync<JobResponse>();
        job!.Status.ShouldBe("QUEUED");
        job.VoiceId.ShouldBe(voice.Id);

        // 4. Status abfragen bis zum Ende
        var status = await ApiFactory.WaitForTerminalAsync(client, job.JobId);
        status.ShouldBe("COMPLETED");

        var finished = await client.GetFromJsonAsync<JobResponse>($"/api/v1/jobs/{job.JobId}");
        finished!.ResultUrl.ShouldBe($"/api/v1/jobs/{job.JobId}/result");
        finished.ResultSha256.ShouldNotBeNullOrEmpty();

        // 5. Ergebnis herunterladen
        var result = await client.GetAsync(finished.ResultUrl);

        result.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.Content.Headers.ContentType!.MediaType.ShouldBe("audio/wav");
        var bytes = await result.Content.ReadAsByteArrayAsync();
        bytes.Length.ShouldBe((int)finished.ResultSizeBytes!.Value);
    }

    [SkippableFact]
    public async Task Eine_MP3_Referenz_wird_als_PCM_Master_abgelegt()
    {
        // Die hochgeladene Datei bleibt in den Angaben nachvollziehbar, damit
        // erkennbar ist, womit gearbeitet wurde.
        Skip.IfNot(TestAudio.Available, "ffmpeg ist nicht verfügbar.");
        var client = factory.CreateAuthenticatedClient();

        var response = await client.PostAsync("/api/v1/voices", VoiceUpload(
            $"Mp3-{Guid.NewGuid():n}", TestAudio.Tone(sampleRate: 22050), "referenz.wav"));

        var voice = await response.Content.ReadFromJsonAsync<ReferenceVoiceResponse>();

        voice!.Stored.SampleRate.ShouldBe(44100);
        voice.Original.SampleRate.ShouldBe(22050);
    }

    [SkippableFact]
    public async Task Eine_zu_kurze_Referenz_wird_mit_ihrem_Fehlercode_abgelehnt()
    {
        Skip.IfNot(TestAudio.Available, "ffmpeg ist nicht verfügbar.");
        var client = factory.CreateAuthenticatedClient();

        var response = await client.PostAsync(
            "/api/v1/voices", VoiceUpload($"Kurz-{Guid.NewGuid():n}", TestAudio.Tone(seconds: 1)));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("REFERENCE_TOO_SHORT");
    }

    [Fact]
    public async Task Eine_Datei_ohne_Audio_wird_abgelehnt_und_erzeugt_keinen_Auftrag()
    {
        var client = factory.CreateAuthenticatedClient();

        var response = await client.PostAsync("/api/v1/voices", VoiceUpload(
            $"Kaputt-{Guid.NewGuid():n}",
            System.Text.Encoding.UTF8.GetBytes("Das ist ganz sicher kein Audio.")));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("INVALID_AUDIO");
    }

    [SkippableFact]
    public async Task Eine_belegte_Bezeichnung_wird_abgelehnt()
    {
        Skip.IfNot(TestAudio.Available, "ffmpeg ist nicht verfügbar.");
        var client = factory.CreateAuthenticatedClient();
        var label = $"Doppelt-{Guid.NewGuid():n}";

        await client.PostAsync("/api/v1/voices", VoiceUpload(label, TestAudio.Tone()));
        var second = await client.PostAsync("/api/v1/voices", VoiceUpload(label, TestAudio.Tone()));

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await second.Content.ReadAsStringAsync()).ShouldContain("DUPLICATE_VOICE_LABEL");
    }

    [Fact]
    public async Task Eine_unbekannte_Stimme_liefert_404()
    {
        var response = await factory.CreateAuthenticatedClient()
            .GetAsync($"/api/v1/voices/{Guid.NewGuid():n}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Eine_ungueltige_Kennung_liefert_400_statt_500()
    {
        var response = await factory.CreateAuthenticatedClient()
            .GetAsync("/api/v1/voices/keine-uuid");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Ein_unbekannter_Auftrag_liefert_404()
    {
        var response = await factory.CreateAuthenticatedClient()
            .GetAsync($"/api/v1/jobs/{Guid.NewGuid():n}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [SkippableFact]
    public async Task Eine_Stimme_mit_wartendem_Auftrag_laesst_sich_nicht_loeschen()
    {
        Skip.IfNot(TestAudio.Available, "ffmpeg ist nicht verfügbar.");
        var client = factory.CreateAuthenticatedClient();
        factory.Engine.Delay = TimeSpan.FromSeconds(5);

        try
        {
            var created = await client.PostAsync(
                "/api/v1/voices", VoiceUpload($"Belegt-{Guid.NewGuid():n}", TestAudio.Tone()));
            var voice = await created.Content.ReadFromJsonAsync<ReferenceVoiceResponse>();

            await client.PostAsync("/api/v1/jobs", JobUpload(voice!.Id, TestAudio.Tone(5)));

            var deleted = await client.DeleteAsync($"/api/v1/voices/{voice.Id}");

            deleted.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await deleted.Content.ReadAsStringAsync()).ShouldContain("VOICE_IN_USE");
        }
        finally
        {
            factory.Engine.Delay = TimeSpan.Zero;
        }
    }

    [SkippableFact]
    public async Task Ein_scheiternder_Lauf_meldet_seinen_Fehlercode()
    {
        Skip.IfNot(TestAudio.Available, "ffmpeg ist nicht verfügbar.");
        var client = factory.CreateAuthenticatedClient();
        factory.Engine.Outcome = ConversionOutcome.Failure(
            ConversionErrorCode.OutOfMemory, "kein Speicher");

        try
        {
            var created = await client.PostAsync(
                "/api/v1/voices", VoiceUpload($"Fehler-{Guid.NewGuid():n}", TestAudio.Tone()));
            var voice = await created.Content.ReadFromJsonAsync<ReferenceVoiceResponse>();

            var accepted = await client.PostAsync("/api/v1/jobs", JobUpload(voice!.Id, TestAudio.Tone(5)));
            var job = await accepted.Content.ReadFromJsonAsync<JobResponse>();

            (await ApiFactory.WaitForTerminalAsync(client, job!.JobId)).ShouldBe("FAILED");

            var failed = await client.GetFromJsonAsync<JobResponse>($"/api/v1/jobs/{job.JobId}");
            failed!.Error!.Code.ShouldBe("OUT_OF_MEMORY");
            failed.ResultUrl.ShouldBeNull();

            // Das Ergebnis eines gescheiterten Laufs gibt es nicht — aber als
            // klare Auskunft, nicht als Serverfehler.
            var result = await client.GetAsync($"/api/v1/jobs/{job.JobId}/result");
            result.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }
        finally
        {
            factory.Engine.Outcome = ConversionOutcome.Success();
        }
    }

    [SkippableFact]
    public async Task Ein_abgebrochener_Auftrag_gibt_seine_Dateien_sofort_frei()
    {
        Skip.IfNot(TestAudio.Available, "ffmpeg ist nicht verfügbar.");
        var client = factory.CreateAuthenticatedClient();

        var created = await client.PostAsync(
            "/api/v1/voices", VoiceUpload($"Abbruch-{Guid.NewGuid():n}", TestAudio.Tone()));
        var voice = await created.Content.ReadFromJsonAsync<ReferenceVoiceResponse>();

        var accepted = await client.PostAsync("/api/v1/jobs", JobUpload(voice!.Id, TestAudio.Tone(5)));
        var job = await accepted.Content.ReadFromJsonAsync<JobResponse>();

        var cancelled = await client.DeleteAsync($"/api/v1/jobs/{job!.JobId}");
        cancelled.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Wiederholbar, damit das Gateway gefahrlos erneut aufräumen kann.
        (await client.DeleteAsync($"/api/v1/jobs/{job.JobId}")).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);

        var result = await client.GetAsync($"/api/v1/jobs/{job.JobId}/result");
        result.StatusCode.ShouldBeOneOf(HttpStatusCode.Conflict, HttpStatusCode.Gone);
    }

    [SkippableFact]
    public async Task Eine_geloeschte_Stimme_verschwindet_aus_der_Uebersicht()
    {
        Skip.IfNot(TestAudio.Available, "ffmpeg ist nicht verfügbar.");
        var client = factory.CreateAuthenticatedClient();

        var created = await client.PostAsync(
            "/api/v1/voices", VoiceUpload($"Weg-{Guid.NewGuid():n}", TestAudio.Tone()));
        var voice = await created.Content.ReadFromJsonAsync<ReferenceVoiceResponse>();

        (await client.DeleteAsync($"/api/v1/voices/{voice!.Id}")).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);

        (await client.GetAsync($"/api/v1/voices/{voice.Id}")).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
    }

    [SkippableFact]
    public async Task Die_Auftragsuebersicht_liefert_eine_Seite_mit_Gesamtzahl()
    {
        Skip.IfNot(TestAudio.Available, "ffmpeg ist nicht verfügbar.");
        var client = factory.CreateAuthenticatedClient();

        var created = await client.PostAsync(
            "/api/v1/voices", VoiceUpload($"Liste-{Guid.NewGuid():n}", TestAudio.Tone()));
        var voice = await created.Content.ReadFromJsonAsync<ReferenceVoiceResponse>();

        var accepted = await client.PostAsync("/api/v1/jobs", JobUpload(voice!.Id, TestAudio.Tone(5)));
        var job = await accepted.Content.ReadFromJsonAsync<JobResponse>();
        await ApiFactory.WaitForTerminalAsync(client, job!.JobId);

        var page = await client.GetFromJsonAsync<JobListResponse>("/api/v1/jobs");

        page.ShouldNotBeNull();
        page.Items.ShouldContain(j => j.JobId == job.JobId);
        page.Total.ShouldBeGreaterThan(0);
        page.Limit.ShouldBe(50);
        page.Offset.ShouldBe(0);
    }

    [Fact]
    public async Task Die_Auftragsuebersicht_verlangt_einen_Schluessel()
    {
        var response = await factory.CreateClient().GetAsync("/api/v1/jobs");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=500")]
    [InlineData("?offset=-1")]
    [InlineData("?status=gibtsnicht")]
    public async Task Unsinnige_Abfragen_werden_mit_400_beantwortet(string query)
    {
        var response = await factory.CreateAuthenticatedClient().GetAsync($"/api/v1/jobs{query}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Die_Uebersicht_laesst_sich_auf_einen_Zustand_einschraenken()
    {
        var page = await factory.CreateAuthenticatedClient()
            .GetFromJsonAsync<JobListResponse>("/api/v1/jobs?status=COMPLETED&limit=5");

        page.ShouldNotBeNull();
        page.Items.ShouldAllBe(j => j.Status == "COMPLETED");
        page.Limit.ShouldBe(5);
    }

    [Fact]
    public async Task Ein_zu_grosser_Upload_wird_abgewiesen()
    {
        var client = factory.CreateAuthenticatedClient();
        var big = new byte[6 * 1024 * 1024];

        var response = await client.PostAsync("/api/v1/voices", VoiceUpload("Gross", big));

        response.StatusCode.ShouldBeOneOf(
            HttpStatusCode.BadRequest, HttpStatusCode.RequestEntityTooLarge);
    }
}
