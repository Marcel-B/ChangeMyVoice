using System.Globalization;
using System.Text.Json;
using ChangeMyVoice.Application.Ports;
using ChangeMyVoice.Domain.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChangeMyVoice.Adapters.Inference;

/// <summary>Ermittelt Audio-Eigenschaften mit ffprobe.</summary>
public sealed class FfprobeAudioProbe(
    IOptions<AudioToolingOptions> options,
    ILogger<FfprobeAudioProbe> logger) : IAudioProbe
{
    private readonly AudioToolingOptions _options = options.Value;

    /// <inheritdoc />
    public async Task<AudioProperties?> ProbeAsync(
        AudioArtifactRef artifact, CancellationToken cancellationToken = default)
    {
        var path = artifact.Locator;

        if (!File.Exists(path))
        {
            return null;
        }

        var result = await ProcessRunner.RunAsync(
            _options.FfprobePath,
            [
                "-v", "error",
                // Nur die erste Tonspur: Ein Video mit mehreren Spuren soll nicht
                // mehrdeutig werden.
                "-select_streams", "a:0",
                "-show_entries", "stream=codec_name,sample_rate,channels,duration",
                "-show_entries", "format=duration",
                "-of", "json",
                path,
            ],
            workingDirectory: null,
            environment: null,
            _options.Timeout,
            onStarted: null,
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0 || result.TimedOut)
        {
            logger.LogDebug(
                "ffprobe konnte {Path} nicht lesen: {Error}", path, result.StandardError.Trim());
            return null;
        }

        return Parse(result.StandardOutput);
    }

    private static AudioProperties? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("streams", out var streams) ||
            streams.GetArrayLength() == 0)
        {
            // Kein Audiostrom — etwa bei einer Textdatei mit .wav-Endung oder
            // einem Video ohne Ton.
            return null;
        }

        var stream = streams[0];

        var codec = stream.TryGetProperty("codec_name", out var codecElement)
            ? codecElement.GetString() ?? string.Empty
            : string.Empty;

        if (string.IsNullOrEmpty(codec))
        {
            return null;
        }

        var sampleRate = ReadInt(stream, "sample_rate");
        var channels = stream.TryGetProperty("channels", out var channelsElement)
            ? channelsElement.GetInt32()
            : 0;

        // Manche Formate tragen die Dauer nur im Container, nicht im Strom.
        var duration = ReadDouble(stream, "duration")
            ?? (root.TryGetProperty("format", out var format) ? ReadDouble(format, "duration") : null);

        return new AudioProperties(
            codec,
            TimeSpan.FromSeconds(duration ?? 0),
            sampleRate,
            channels);
    }

    private static int ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    private static double? ReadDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
}
