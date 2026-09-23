using ChangeMyVoice.Adapters.Storage;
using ChangeMyVoice.Application.Ports;
using ChangeMyVoice.Domain.Jobs;
using ChangeMyVoice.Domain.Voices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace ChangeMyVoice.Adapters.Tests;

public class FileSystemStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cmv-storage", Guid.NewGuid().ToString("n"));

    private readonly StorageOptions _options;
    private readonly FileSystemVoiceStorage _voices;
    private readonly FileSystemJobWorkspaceStore _workspaces;

    public FileSystemStorageTests()
    {
        _options = new StorageOptions { DataRoot = _root };
        var wrapped = Options.Create(_options);
        _voices = new FileSystemVoiceStorage(wrapped, NullLogger<FileSystemVoiceStorage>.Instance);
        _workspaces = new FileSystemJobWorkspaceStore(
            wrapped, NullLogger<FileSystemJobWorkspaceStore>.Instance);
    }

    private VoiceId GivenStoredVoice()
    {
        var id = VoiceId.New();
        var master = _voices.ReserveMaster(id);
        File.WriteAllBytes(master.Locator, [1, 2, 3]);
        return id;
    }

    [Fact]
    public void Ein_reservierter_Master_liegt_unter_voices()
    {
        var id = VoiceId.New();

        var master = _voices.ReserveMaster(id);

        master.Locator.ShouldStartWith(Path.Combine(_root, "voices"));
        Directory.Exists(Path.GetDirectoryName(master.Locator)).ShouldBeTrue();
    }

    [Fact]
    public void Ein_geschriebener_Master_wird_gefunden()
    {
        var id = GivenStoredVoice();

        _voices.MasterExists(id).ShouldBeTrue();
        _voices.MasterExists(VoiceId.New()).ShouldBeFalse();
    }

    [Fact]
    public async Task Ein_Master_laesst_sich_lesen_ein_fehlender_nicht()
    {
        var id = GivenStoredVoice();

        await using (var stream = await _voices.OpenMasterAsync(id))
        {
            stream.ShouldNotBeNull();
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            copy.ToArray().ShouldBe([1, 2, 3]);
        }

        (await _voices.OpenMasterAsync(VoiceId.New())).ShouldBeNull();
    }

    [Fact]
    public async Task Eine_geloeschte_Stimme_ist_vollstaendig_weg()
    {
        var id = GivenStoredVoice();

        await _voices.DeleteAsync(id);

        _voices.MasterExists(id).ShouldBeFalse();
    }

    [Fact]
    public void Ein_Arbeitsverzeichnis_enthaelt_die_drei_erwarteten_Dateien()
    {
        // Aufbau nach init.md §21.
        var jobId = JobId.New();

        var workspace = _workspaces.Create(jobId);

        workspace.Source.Locator.ShouldEndWith("source.wav");
        workspace.Reference.Locator.ShouldEndWith("reference.wav");
        workspace.Output.Locator.ShouldEndWith("output.wav");
        Directory.Exists(Path.Combine(_root, "jobs", jobId.ToString())).ShouldBeTrue();
    }

    [Fact]
    public async Task Ein_fehlendes_Ergebnis_liefert_keinen_Datenstrom()
    {
        var jobId = JobId.New();
        _workspaces.Create(jobId);

        (await _workspaces.OpenOutputAsync(jobId)).ShouldBeNull();
    }

    [Fact]
    public async Task Ein_vorhandenes_Ergebnis_laesst_sich_lesen()
    {
        var jobId = JobId.New();
        var workspace = _workspaces.Create(jobId);
        await File.WriteAllBytesAsync(workspace.Output.Locator, [9, 8, 7]);

        await using var stream = await _workspaces.OpenOutputAsync(jobId);

        stream.ShouldNotBeNull();
        stream.Length.ShouldBe(3);
    }

    [Fact]
    public void Vorgefundene_Verzeichnisse_werden_mit_ihrer_Kennung_gemeldet()
    {
        var jobId = JobId.New();
        _workspaces.Create(jobId);

        var entries = _workspaces.Enumerate();

        entries.ShouldHaveSingleItem().JobId.ShouldBe(jobId);
    }

    [Fact]
    public void Ein_unlesbarer_Verzeichnisname_wird_ohne_Kennung_gemeldet()
    {
        // Genau das bleibt nach einem Eingriff von aussen zurueck; der
        // Aufraeumlauf muss es trotzdem entfernen koennen.
        Directory.CreateDirectory(Path.Combine(_root, "jobs", "kein-gueltiger-name"));

        var entry = _workspaces.Enumerate().ShouldHaveSingleItem();

        entry.JobId.ShouldBeNull();
        entry.Location.Locator.ShouldEndWith("kein-gueltiger-name");
    }

    [Fact]
    public async Task Ein_verwaistes_Verzeichnis_laesst_sich_ueber_seinen_Ort_entfernen()
    {
        var path = Path.Combine(_root, "jobs", "kein-gueltiger-name");
        Directory.CreateDirectory(path);
        var entry = _workspaces.Enumerate().Single();

        await _workspaces.DeleteAsync(entry);

        Directory.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public async Task Alte_Zwischendateien_werden_entfernt_frische_bleiben()
    {
        var alt = Path.Combine(_options.TempRoot, "alt.wav");
        var neu = Path.Combine(_options.TempRoot, "neu.wav");
        await File.WriteAllBytesAsync(alt, [1]);
        await File.WriteAllBytesAsync(neu, [1]);
        File.SetLastWriteTimeUtc(alt, DateTime.UtcNow.AddHours(-5));

        var removed = await _workspaces.PurgeTemporaryAsync(DateTimeOffset.UtcNow.AddHours(-1));

        removed.ShouldBe(1);
        File.Exists(alt).ShouldBeFalse();
        File.Exists(neu).ShouldBeTrue();
    }

    [Fact]
    public async Task Der_Aufraeumlauf_kann_Referenzstimmen_nicht_erreichen()
    {
        // Die zentrale Zusicherung des Adapters: Ein Loeschversuch, der aus
        // welchem Grund auch immer auf voices/ zeigt, wird abgewiesen. Ohne
        // diese Sperre waere ein Fehler weiter oben ein dauerhafter Datenverlust.
        var voiceId = GivenStoredVoice();
        var voicesPath = Path.Combine(_root, "voices", voiceId.ToString());

        await _workspaces.DeleteAsync(
            new JobWorkspaceEntry(null, new AudioArtifactRef(voicesPath), DateTimeOffset.UtcNow));

        Directory.Exists(voicesPath).ShouldBeTrue();
        _voices.MasterExists(voiceId).ShouldBeTrue();
    }

    [Fact]
    public async Task Auch_das_Wurzelverzeichnis_ist_gegen_Loeschen_geschuetzt()
    {
        await _workspaces.DeleteAsync(
            new JobWorkspaceEntry(null, new AudioArtifactRef(_root), DateTimeOffset.UtcNow));

        Directory.Exists(_root).ShouldBeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
