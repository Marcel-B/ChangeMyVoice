using System.Globalization;
using ChangeMyVoice.Application.Ports;
using ChangeMyVoice.Domain.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChangeMyVoice.Adapters.Inference;

/// <summary>
/// Bringt Audiodateien mit ffmpeg in das Format, das der Modelllauf erwartet.
/// </summary>
/// <remarks>
/// Es wird immer genau auf die Abtastrate umgerechnet, die der Lauf tatsächlich
/// verwendet (22050 oder 44100). Seed-VC lädt seine Eingaben selbst mit
/// <c>librosa.load(..., sr=…)</c> und <c>mono=True</c>; würde hier auf eine
/// andere Rate normalisiert, liefe die Datei zweimal durch eine Umrechnung. So
/// bleibt es bei genau einer.
/// </remarks>
public sealed class FfmpegAudioNormalizer(
    IOptions<AudioToolingOptions> options,
    ILogger<FfmpegAudioNormalizer> logger) : IAudioNormalizer
{
    private readonly AudioToolingOptions _options = options.Value;

    /// <inheritdoc />
    public async Task NormalizeAsync(
        AudioArtifactRef source,
        AudioArtifactRef destination,
        TargetAudioFormat format,
        TimeSpan? maxDuration = null,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(destination.Locator);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var arguments = new List<string>
        {
            "-hide_banner",
            "-v", "error",
            // Vorhandene Zieldatei überschreiben, statt auf eine Rückfrage zu warten.
            "-y",
            "-i", source.Locator,
        };

        if (maxDuration is { } limit)
        {
            arguments.AddRange(["-t", limit.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)]);
        }

        arguments.AddRange(
        [
            // Nur die erste Tonspur, kein Bild, keine Untertitel.
            "-map", "a:0",
            "-vn",
            "-ac", format.Channels.ToString(CultureInfo.InvariantCulture),
            "-ar", format.SampleRate.ToString(CultureInfo.InvariantCulture),
            "-c:a", format.Codec,
            destination.Locator,
        ]);

        var result = await ProcessRunner.RunAsync(
            _options.FfmpegPath,
            arguments,
            workingDirectory: null,
            environment: null,
            _options.Timeout,
            onStarted: null,
            cancellationToken).ConfigureAwait(false);

        if (result.TimedOut)
        {
            throw new AudioConversionException(
                $"Die Umwandlung von '{source.Locator}' hat das Zeitlimit überschritten.");
        }

        if (result.ExitCode != 0)
        {
            logger.LogWarning(
                "ffmpeg scheiterte an {Source}: {Error}", source.Locator, result.StandardError.Trim());

            throw new AudioConversionException(
                $"Die Datei konnte nicht umgewandelt werden: {result.StandardError.Trim()}");
        }
    }
}

/// <summary>Eine fehlgeschlagene Audioumwandlung.</summary>
public sealed class AudioConversionException(string message) : Exception(message);
