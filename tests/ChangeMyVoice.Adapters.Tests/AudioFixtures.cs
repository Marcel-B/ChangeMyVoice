using System.Diagnostics;

namespace ChangeMyVoice.Adapters.Tests;

/// <summary>
/// Erzeugt echte Audiodateien für die Tests der Audio-Adapter.
/// </summary>
/// <remarks>
/// Bewusst echte Dateien statt vorgetäuschter Werte: Die Zusicherung, dass nach
/// der Normalisierung genau mono und genau die Zielrate herauskommt, lässt sich
/// nur an einer tatsächlich umgewandelten Datei nachmessen.
/// </remarks>
public sealed class AudioFixtures : IDisposable
{
    /// <summary>Das Verzeichnis, in dem die Dateien liegen.</summary>
    public string Directory { get; } = Path.Combine(
        Path.GetTempPath(), "cmv-tests", Guid.NewGuid().ToString("n"));

    /// <summary>Ob ffmpeg auf diesem Rechner zur Verfügung steht.</summary>
    public static bool FfmpegAvailable { get; } = CheckTool("ffmpeg");

    public AudioFixtures() => System.IO.Directory.CreateDirectory(Directory);

    /// <summary>Erzeugt einen Sinuston in den gewünschten Eigenschaften.</summary>
    public string CreateTone(
        string fileName,
        double seconds = 10,
        int sampleRate = 44100,
        int channels = 1,
        string? codec = null)
    {
        var path = Path.Combine(Directory, fileName);

        var arguments = new List<string>
        {
            "-hide_banner", "-v", "error", "-y",
            "-f", "lavfi",
            "-i", $"sine=frequency=440:sample_rate={sampleRate}:duration={seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            "-ac", channels.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        if (codec is not null)
        {
            arguments.AddRange(["-c:a", codec]);
        }

        arguments.Add(path);

        Run("ffmpeg", arguments);
        return path;
    }

    /// <summary>Legt eine Textdatei mit irreführender Endung an.</summary>
    public string CreateTextFileNamedWav(string fileName = "kaputt.wav")
    {
        var path = Path.Combine(Directory, fileName);
        File.WriteAllText(path, "Das hier ist ganz sicher kein Audio.");
        return path;
    }

    /// <summary>Legt eine leere Datei an.</summary>
    public string CreateEmptyFile(string fileName = "leer.wav")
    {
        var path = Path.Combine(Directory, fileName);
        File.WriteAllBytes(path, []);
        return path;
    }

    /// <summary>Einen Pfad im Testverzeichnis bilden, ohne etwas anzulegen.</summary>
    public string PathFor(string fileName) => Path.Combine(Directory, fileName);

    /// <summary>Liest die Eigenschaften einer Datei direkt mit ffprobe nach.</summary>
    public static (string Codec, int SampleRate, int Channels) Measure(string path)
    {
        var output = Run("ffprobe",
        [
            "-v", "error",
            "-select_streams", "a:0",
            "-show_entries", "stream=codec_name,sample_rate,channels",
            "-of", "csv=p=0",
            path,
        ]);

        var parts = output.Trim().Split(',');
        return (parts[0], int.Parse(parts[1]), int.Parse(parts[2]));
    }

    private static string Run(string tool, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = tool,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return process.ExitCode == 0
            ? stdout
            : throw new InvalidOperationException($"{tool} scheiterte: {stderr}");
    }

    private static bool CheckTool(string tool)
    {
        try
        {
            Run(tool, ["-version"]);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (System.IO.Directory.Exists(Directory))
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
