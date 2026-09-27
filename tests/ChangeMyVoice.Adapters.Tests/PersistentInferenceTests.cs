using System.Diagnostics;
using ChangeMyVoice.Adapters.Inference;
using ChangeMyVoice.Application.Ports;
using ChangeMyVoice.Domain.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace ChangeMyVoice.Adapters.Tests;

/// <summary>
/// Prüft den Dauerbetrieb gegen das echte Skript, dessen Modell durch eine
/// Dateikopie ersetzt ist (<c>CHANGEMYVOICE_FAKE_MODEL</c>).
/// </summary>
/// <remarks>
/// So läuft das Protokoll zwischen Dienst und Python-Prozess wirklich, auch
/// ohne Seed-VC, Torch und Apple Silicon — genau die Stelle, an der ein
/// stehender Prozess sich sonst erst auf dem Mac verklemmen würde.
/// </remarks>
public sealed class PersistentInferenceTests : IAsyncLifetime
{
    private static readonly string? Python = FindPython();

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "cmv-serve", Guid.NewGuid().ToString("n"));

    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-09-26T12:00:00Z"));
    private readonly List<MlxVcConversionEngine> _engines = [];

    static PersistentInferenceTests()
    {
        // Erbt jeder Python-Prozess, den die Tests starten; der Dienst selbst
        // setzt die Variable nie.
        Environment.SetEnvironmentVariable("CHANGEMYVOICE_FAKE_MODEL", "1");
    }

    private MlxVcConversionEngine Sut(TimeSpan? keepFor = null, TimeSpan? timeout = null)
    {
        var engine = new MlxVcConversionEngine(
            Options.Create(new InferenceOptions
            {
                PythonExecutable = Python!,
                ScriptPath = ScriptPath(),
                WorkingDirectory = _directory,
                KeepModelLoadedFor = keepFor ?? TimeSpan.FromMinutes(5),
                Timeout = timeout ?? TimeSpan.FromMinutes(1),
            }),
            _clock,
            NullLogger<MlxVcConversionEngine>.Instance);

        _engines.Add(engine);
        return engine;
    }

    private ConversionRequest Request(string name, ConversionOptions? options = null, bool missingSource = false)
    {
        var source = Path.Combine(_directory, name + "-quelle.wav");
        var reference = Path.Combine(_directory, "referenz.wav");

        if (!missingSource)
        {
            File.WriteAllBytes(source, new byte[512]);
        }

        File.WriteAllBytes(reference, new byte[512]);

        return new ConversionRequest(
            new AudioArtifactRef(source),
            new AudioArtifactRef(reference),
            new AudioArtifactRef(Path.Combine(_directory, name + "-ergebnis.wav")),
            options ?? ConversionOptions.Default);
    }

    [SkippableFact]
    public async Task Der_zweite_Lauf_verwendet_denselben_Prozess()
    {
        Skip.If(Python is null, "python3 ist nicht verfügbar.");
        var engine = Sut();
        var pids = new List<int>();

        var first = await engine.ConvertAsync(Request("eins"), pids.Add);
        var second = await engine.ConvertAsync(Request("zwei"), pids.Add);

        first.IsSuccess.ShouldBeTrue(first.Error?.Message);
        second.IsSuccess.ShouldBeTrue(second.Error?.Message);
        File.Exists(Path.Combine(_directory, "zwei-ergebnis.wav")).ShouldBeTrue();

        pids.Count.ShouldBe(2);
        pids[1].ShouldBe(pids[0]);
        second.ModelLoadDuration.ShouldBe(TimeSpan.Zero);

        var state = engine.GetState();
        state.IsLoaded.ShouldBeTrue();
        state.IsBusy.ShouldBeFalse();
        state.UnloadAtUtc.ShouldBe(_clock.GetUtcNow() + TimeSpan.FromMinutes(5));
    }

    [SkippableFact]
    public async Task Ein_anderer_Pfad_startet_einen_neuen_Prozess()
    {
        Skip.If(Python is null, "python3 ist nicht verfügbar.");
        var engine = Sut();
        var pids = new List<int>();
        ConversionOptions.TryCreate(null, null, null, false, null, null, null, null, out var speech, out _);

        await engine.ConvertAsync(Request("gesang"), pids.Add);
        var outcome = await engine.ConvertAsync(Request("sprache", speech), pids.Add);

        outcome.IsSuccess.ShouldBeTrue(outcome.Error?.Message);
        pids[1].ShouldNotBe(pids[0]);
        ProcessIsGone(pids[0]).ShouldBeTrue();
    }

    [SkippableFact]
    public async Task Nach_der_Leerlaufzeit_wird_das_Modell_entladen()
    {
        Skip.If(Python is null, "python3 ist nicht verfügbar.");
        var engine = Sut(keepFor: TimeSpan.FromMinutes(5));
        var pid = 0;

        await engine.ConvertAsync(Request("eins"), p => pid = p);

        _clock.Advance(TimeSpan.FromMinutes(4));
        engine.GetState().IsLoaded.ShouldBeTrue();

        _clock.Advance(TimeSpan.FromMinutes(1));
        await WaitUntil(() => !engine.GetState().IsLoaded);

        await WaitUntil(() => ProcessIsGone(pid));
    }

    [SkippableFact]
    public async Task Auf_Anfrage_wird_das_Modell_entladen()
    {
        Skip.If(Python is null, "python3 ist nicht verfügbar.");
        var engine = Sut();
        var pid = 0;

        (await engine.ReleaseAsync()).ShouldBe(ModelReleaseResult.NotLoaded);
        await engine.ConvertAsync(Request("eins"), p => pid = p);

        (await engine.ReleaseAsync()).ShouldBe(ModelReleaseResult.Released);

        engine.GetState().IsLoaded.ShouldBeFalse();
        ProcessIsGone(pid).ShouldBeTrue();
        (await engine.ReleaseAsync()).ShouldBe(ModelReleaseResult.NotLoaded);

        // Der nächste Auftrag lädt einfach neu.
        var again = await engine.ConvertAsync(Request("zwei"));
        again.IsSuccess.ShouldBeTrue(again.Error?.Message);
    }

    [SkippableFact]
    public async Task Nach_einem_Fehler_rechnet_der_naechste_Lauf_in_einem_frischen_Prozess()
    {
        Skip.If(Python is null, "python3 ist nicht verfügbar.");
        var engine = Sut();
        var pids = new List<int>();

        var failed = await engine.ConvertAsync(Request("fehlt", missingSource: true), pids.Add);
        var next = await engine.ConvertAsync(Request("da"), pids.Add);

        failed.Error!.Code.ShouldBe(ConversionErrorCode.InvalidAudio);
        next.IsSuccess.ShouldBeTrue(next.Error?.Message);
        pids[1].ShouldNotBe(pids[0]);
    }

    [SkippableFact]
    public async Task Ein_Zeitablauf_beendet_den_Prozess()
    {
        Skip.If(Python is null, "python3 ist nicht verfügbar.");
        var engine = Sut(timeout: TimeSpan.FromMilliseconds(1));
        var pid = 0;

        var outcome = await engine.ConvertAsync(Request("eins"), p => pid = p);

        outcome.Error!.Code.ShouldBe(ConversionErrorCode.Timeout);
        engine.GetState().IsLoaded.ShouldBeFalse();
        await WaitUntil(() => ProcessIsGone(pid));
    }

    [SkippableFact]
    public async Task Ohne_Leerlaufzeit_bekommt_jeder_Lauf_einen_eigenen_Prozess()
    {
        Skip.If(Python is null, "python3 ist nicht verfügbar.");
        var engine = Sut(keepFor: TimeSpan.Zero);
        var pids = new List<int>();

        var first = await engine.ConvertAsync(Request("eins"), pids.Add);
        var second = await engine.ConvertAsync(Request("zwei"), pids.Add);

        first.IsSuccess.ShouldBeTrue(first.Error?.Message);
        second.IsSuccess.ShouldBeTrue(second.Error?.Message);
        pids[1].ShouldNotBe(pids[0]);
        engine.GetState().ShouldBe(new InferenceModelState(false, false, TimeSpan.Zero));
    }

    private static bool ProcessIsGone(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Die Bedingung trat nicht ein.");
            }

            await Task.Delay(50);
        }
    }

    private static string ScriptPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ChangeMyVoice.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory!.FullName, "scripts", "changemyvoice_infer.py");
    }

    private static string? FindPython()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, "python3");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        foreach (var engine in _engines)
        {
            await engine.DisposeAsync();
        }

        Directory.Delete(_directory, recursive: true);
    }
}
