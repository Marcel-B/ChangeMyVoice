using ChangeMyVoice.Adapters.Persistence;
using ChangeMyVoice.Domain.Audio;
using ChangeMyVoice.Domain.Jobs;
using ChangeMyVoice.Domain.Voices;
using Microsoft.Extensions.Options;
using Shouldly;

namespace ChangeMyVoice.Adapters.Tests;

/// <summary>Eine frische Datenbankdatei je Test.</summary>
public sealed class SqliteFixture : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "cmv-db", Guid.NewGuid().ToString("n"));

    public SqliteConnectionFactory Factory { get; }

    public SqliteFixture()
    {
        Directory.CreateDirectory(_directory);
        Factory = new SqliteConnectionFactory(Options.Create(new PersistenceOptions
        {
            DatabasePath = Path.Combine(_directory, "test.db"),
        }));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}

public class SqliteReferenceVoiceRepositoryTests : IDisposable
{
    private readonly SqliteFixture _fixture = new();
    private readonly SqliteReferenceVoiceRepository _sut;

    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    public SqliteReferenceVoiceRepositoryTests() => _sut = new SqliteReferenceVoiceRepository(_fixture.Factory);

    private static ReferenceVoice Voice(string label) => ReferenceVoice.Create(
        VoiceId.New(), VoiceLabel.Create(label), Now,
        new AudioProperties("pcm_s16le", TimeSpan.FromSeconds(12.5), 44100, 1),
        new AudioProperties("mp3", TimeSpan.FromSeconds(12.5), 22050, 2));

    [Fact]
    public async Task Eine_gespeicherte_Stimme_kommt_unveraendert_zurueck()
    {
        var voice = Voice("Anna");

        await _sut.SaveAsync(voice);
        var loaded = await _sut.FindAsync(voice.Id);

        loaded.ShouldNotBeNull();
        loaded.Label.Value.ShouldBe("Anna");
        loaded.CreatedAtUtc.ShouldBe(Now);
        loaded.StoredAudio.SampleRate.ShouldBe(44100);
        loaded.StoredAudio.Duration.TotalSeconds.ShouldBe(12.5, tolerance: 0.001);
        // Die urspruenglichen Eigenschaften muessen erhalten bleiben, damit
        // spaeter nachvollziehbar ist, was hochgeladen wurde.
        loaded.OriginalAudio.Codec.ShouldBe("mp3");
        loaded.OriginalAudio.Channels.ShouldBe(2);
    }

    [Fact]
    public async Task Eine_unbekannte_Stimme_liefert_nichts()
    {
        (await _sut.FindAsync(VoiceId.New())).ShouldBeNull();
    }

    [Fact]
    public async Task Die_Liste_ist_nach_Bezeichnung_sortiert()
    {
        await _sut.SaveAsync(Voice("Zora"));
        await _sut.SaveAsync(Voice("Anna"));
        await _sut.SaveAsync(Voice("Malte"));

        var all = await _sut.ListAsync();

        all.Select(v => v.Label.Value).ShouldBe(["Anna", "Malte", "Zora"]);
    }

    [Fact]
    public async Task Eine_belegte_Bezeichnung_wird_unabhaengig_von_der_Schreibweise_erkannt()
    {
        await _sut.SaveAsync(Voice("Anna"));

        (await _sut.ExistsWithLabelAsync(VoiceLabel.Create("ANNA"))).ShouldBeTrue();
        (await _sut.ExistsWithLabelAsync(VoiceLabel.Create("Berta"))).ShouldBeFalse();
    }

    [Fact]
    public async Task Eine_geloeschte_Stimme_verschwindet()
    {
        var voice = Voice("Anna");
        await _sut.SaveAsync(voice);

        await _sut.DeleteAsync(voice.Id);

        (await _sut.FindAsync(voice.Id)).ShouldBeNull();
        (await _sut.ListAsync()).ShouldBeEmpty();
    }

    public void Dispose() => _fixture.Dispose();
}

public class SqliteConversionJobRepositoryTests : IDisposable
{
    private readonly SqliteFixture _fixture = new();
    private readonly SqliteConversionJobRepository _sut;

    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Instance = Guid.NewGuid();

    public SqliteConversionJobRepositoryTests() => _sut = new SqliteConversionJobRepository(_fixture.Factory);

    private static ConversionJob Job(VoiceId? voiceId = null) => ConversionJob.Create(
        JobId.New(), voiceId ?? VoiceId.New(), "Anna", ConversionOptions.Default, Now, Instance);

    [Fact]
    public async Task Ein_gespeicherter_Auftrag_kommt_unveraendert_zurueck()
    {
        var job = Job();

        await _sut.SaveAsync(job);
        var loaded = await _sut.FindAsync(job.Id);

        loaded.ShouldNotBeNull();
        loaded.Status.ShouldBe(JobStatus.Queued);
        loaded.VoiceLabel.ShouldBe("Anna");
        loaded.InstanceId.ShouldBe(Instance);
        loaded.CreatedAtUtc.ShouldBe(Now);
    }

    [Fact]
    public async Task Die_Webhook_Adresse_bleibt_erhalten()
    {
        WebhookUrl.TryCreate("https://yue.example/api/voice/done?song=3", [], out var webhook, out _)
            .ShouldBeTrue();
        var job = ConversionJob.Create(
            JobId.New(), VoiceId.New(), "Anna", ConversionOptions.Default, Now, Instance,
            webhookUrl: webhook);

        await _sut.SaveAsync(job);
        job.Start(Now, Instance);
        await _sut.SaveAsync(job);

        (await _sut.FindAsync(job.Id))!.WebhookUrl.ShouldBe(webhook);
    }

    [Fact]
    public async Task Ohne_Webhook_Adresse_bleibt_sie_leer()
    {
        var job = Job();

        await _sut.SaveAsync(job);

        (await _sut.FindAsync(job.Id))!.WebhookUrl.ShouldBeNull();
    }

    [Fact]
    public async Task Der_gesamte_Lebenslauf_bleibt_erhalten()
    {
        var job = Job();
        await _sut.SaveAsync(job);

        job.Start(Now.AddSeconds(5), Instance);
        job.AttachInferenceProcess(4711);
        await _sut.SaveAsync(job);

        job.Complete(Now.AddMinutes(3), 8192, "abc123");
        await _sut.SaveAsync(job);

        var loaded = await _sut.FindAsync(job.Id);

        loaded!.Status.ShouldBe(JobStatus.Completed);
        loaded.StartedAtUtc.ShouldBe(Now.AddSeconds(5));
        loaded.FinishedAtUtc.ShouldBe(Now.AddMinutes(3));
        loaded.OutputSizeBytes.ShouldBe(8192);
        loaded.OutputSha256.ShouldBe("abc123");
        loaded.InferenceProcessId.ShouldBeNull();
    }

    [Fact]
    public async Task Die_Fehlerursache_ueberlebt_das_Speichern()
    {
        var job = Job();
        job.Fail(Now, new JobError(ConversionErrorCode.MpsError, "Metal streikt"));

        await _sut.SaveAsync(job);
        var loaded = await _sut.FindAsync(job.Id);

        loaded!.Error!.Code.ShouldBe(ConversionErrorCode.MpsError);
        loaded.Error.Message.ShouldBe("Metal streikt");
    }

    [Fact]
    public async Task Die_Prozesskennung_wird_festgehalten()
    {
        // Nach einem Absturz ist sie der einzige Weg, den zurueckgebliebenen
        // Python-Prozess wiederzufinden.
        var job = Job();
        job.Start(Now, Instance);
        job.AttachInferenceProcess(4711);

        await _sut.SaveAsync(job);
        var loaded = await _sut.FindAsync(job.Id);

        loaded!.InferenceProcessId.ShouldBe(4711);
    }

    [Fact]
    public async Task Aufgeraeumte_Auftraege_tauchen_in_der_Arbeitsliste_nicht_mehr_auf()
    {
        var kept = Job();
        var purged = Job();
        purged.MarkArtifactsPurged();

        await _sut.SaveAsync(kept);
        await _sut.SaveAsync(purged);

        var list = await _sut.ListUnpurgedAsync();

        list.ShouldHaveSingleItem().Id.ShouldBe(kept.Id);
        // Abfragbar bleibt er trotzdem.
        (await _sut.FindAsync(purged.Id)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Ein_wartender_Auftrag_haelt_seine_Stimme_fest()
    {
        var voiceId = VoiceId.New();
        await _sut.SaveAsync(Job(voiceId));

        (await _sut.HasActiveJobForVoiceAsync(voiceId)).ShouldBeTrue();
        (await _sut.HasActiveJobForVoiceAsync(VoiceId.New())).ShouldBeFalse();
    }

    [Fact]
    public async Task Ein_abgeschlossener_Auftrag_haelt_seine_Stimme_nicht_mehr_fest()
    {
        var voiceId = VoiceId.New();
        var job = Job(voiceId);
        job.Start(Now, Instance);
        job.Complete(Now, 1, "x");
        await _sut.SaveAsync(job);

        (await _sut.HasActiveJobForVoiceAsync(voiceId)).ShouldBeFalse();
    }

    [Fact]
    public async Task Die_Einstellungen_des_Laufs_bleiben_erhalten()
    {
        ConversionOptions.TryCreate(80, 0.5, 1.2, true, false, null, out var options, out _);
        var job = ConversionJob.Create(
            JobId.New(), VoiceId.New(), "Anna", options, Now, Instance);

        await _sut.SaveAsync(job);
        var loaded = await _sut.FindAsync(job.Id);

        loaded!.Options.DiffusionSteps.ShouldBe(80);
        loaded.Options.F0Condition.ShouldBeTrue();
        loaded.Options.Fp16.ShouldBeFalse();
        // Und damit auch die Zielrate des Gesangspfads.
        loaded.Options.TargetFormat.SampleRate.ShouldBe(44100);
    }

    public void Dispose() => _fixture.Dispose();
}

public class SqliteJobListingTests : IDisposable
{
    private readonly SqliteFixture _fixture = new();
    private readonly SqliteConversionJobRepository _sut;

    private static readonly DateTimeOffset Start = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    public SqliteJobListingTests() => _sut = new SqliteConversionJobRepository(_fixture.Factory);

    private async Task<ConversionJob> Given(int minute, JobStatus status = JobStatus.Queued)
    {
        var at = Start.AddMinutes(minute);
        var job = ConversionJob.Create(
            JobId.New(), VoiceId.New(), "Anna", ConversionOptions.Default, at, Guid.NewGuid());

        if (status == JobStatus.Completed)
        {
            job.Start(at, Guid.NewGuid());
            job.Complete(at, 10, "sha");
        }
        else if (status == JobStatus.Failed)
        {
            job.Fail(at, new JobError(ConversionErrorCode.Timeout, "zu lang"));
        }

        await _sut.SaveAsync(job);
        return job;
    }

    [Fact]
    public async Task Die_Sortierung_liefert_die_juengsten_zuerst()
    {
        // Die Reihenfolge entsteht in SQL; ein Test in der Anwendungsschicht
        // allein wuerde eine falsche ORDER-BY-Klausel nicht bemerken.
        await Given(0);
        await Given(20);
        var neuester = await Given(40);

        var list = await _sut.ListAsync(null, 10, 0);

        list[0].Id.ShouldBe(neuester.Id);
        list.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Seitengroesse_und_Versatz_greifen()
    {
        for (var i = 0; i < 6; i++)
        {
            await Given(i);
        }

        var erste = await _sut.ListAsync(null, 2, 0);
        var dritte = await _sut.ListAsync(null, 2, 4);

        erste.Count.ShouldBe(2);
        dritte.Count.ShouldBe(2);
        erste.Select(j => j.Id).ShouldNotBe(dritte.Select(j => j.Id));
    }

    [Fact]
    public async Task Der_Zustandsfilter_greift()
    {
        await Given(0, JobStatus.Completed);
        await Given(1, JobStatus.Failed);
        await Given(2, JobStatus.Completed);

        var fertige = await _sut.ListAsync(JobStatus.Completed, 10, 0);

        fertige.Count.ShouldBe(2);
        fertige.ShouldAllBe(j => j.Status == JobStatus.Completed);
    }

    [Fact]
    public async Task Die_Gesamtzahl_beruecksichtigt_den_Filter()
    {
        await Given(0, JobStatus.Completed);
        await Given(1, JobStatus.Failed);
        await Given(2, JobStatus.Completed);

        (await _sut.CountAsync(null)).ShouldBe(3);
        (await _sut.CountAsync(JobStatus.Completed)).ShouldBe(2);
        (await _sut.CountAsync(JobStatus.Cancelled)).ShouldBe(0);
    }

    [Fact]
    public async Task Aufgeraeumte_Auftraege_erscheinen_weiterhin_in_der_Uebersicht()
    {
        // Anders als ListUnpurgedAsync, das nur die mit Dateien liefert.
        var job = await Given(0, JobStatus.Completed);
        job.MarkArtifactsPurged();
        await _sut.SaveAsync(job);

        (await _sut.ListAsync(null, 10, 0)).Count.ShouldBe(1);
        (await _sut.ListUnpurgedAsync()).ShouldBeEmpty();
    }

    public void Dispose() => _fixture.Dispose();
}

public class SqliteSchemaTests
{
    [Fact]
    public async Task Eine_Datenbank_ohne_Webhook_Spalte_bekommt_sie_nachgetragen()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cmv-db", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "alt.db");

        try
        {
            // Der Stand vor der Webhook-Spalte, wie ihn laufende Dienste haben.
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE conversion_jobs (
                        id TEXT PRIMARY KEY, voice_id TEXT NOT NULL, voice_label TEXT NOT NULL,
                        status TEXT NOT NULL, diffusion_steps INTEGER NOT NULL,
                        inference_cfg_rate REAL NOT NULL, length_adjust REAL NOT NULL,
                        f0_condition INTEGER NOT NULL, fp16 INTEGER NOT NULL,
                        created_at_utc TEXT NOT NULL, started_at_utc TEXT NULL,
                        finished_at_utc TEXT NULL, downloaded_at_utc TEXT NULL,
                        error_code TEXT NULL, error_message TEXT NULL,
                        output_size_bytes INTEGER NULL, output_sha256 TEXT NULL,
                        instance_id TEXT NOT NULL, inference_process_id INTEGER NULL,
                        artifacts_purged INTEGER NOT NULL DEFAULT 0,
                        source_length_ms INTEGER NOT NULL DEFAULT 0,
                        output_sample_rate INTEGER NOT NULL DEFAULT 48000);
                    """;
                command.ExecuteNonQuery();
            }

            var factory = new SqliteConnectionFactory(Options.Create(new PersistenceOptions
            {
                DatabasePath = path,
            }));
            var repository = new SqliteConversionJobRepository(factory);
            var job = ConversionJob.Create(
                JobId.New(), VoiceId.New(), "Anna", ConversionOptions.Default,
                DateTimeOffset.UnixEpoch, Guid.NewGuid(),
                webhookUrl: WebhookUrl.Rehydrate("http://127.0.0.1:5091/hook"));

            await repository.SaveAsync(job);

            (await repository.FindAsync(job.Id))!.WebhookUrl!.ToString()
                .ShouldBe("http://127.0.0.1:5091/hook");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
