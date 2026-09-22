using ChangeMyVoice.Domain.Jobs;

namespace ChangeMyVoice.Domain.Audio;

/// <summary>Die Rolle, in der eine Datei hochgeladen wurde.</summary>
public enum AudioRole
{
    /// <summary>Die Zielstimme, deren Timbre übernommen werden soll.</summary>
    Reference,

    /// <summary>Die Aufnahme, deren Gesang konvertiert werden soll.</summary>
    Source,
}

/// <summary>Grenzwerte der Audio-Prüfung.</summary>
/// <param name="MinReferenceDuration">Kürzeste zulässige Referenzaufnahme.</param>
/// <param name="MaxReferenceDuration">
/// Längste verwertete Referenzaufnahme. Längere Aufnahmen werden gekürzt, weil
/// Seed-VC sie ohnehin mit <c>ref_audio[: sr * 25]</c> abschneidet.
/// </param>
/// <param name="MaxSourceDuration">Längste zulässige Quellaufnahme.</param>
/// <param name="MinSampleRate">
/// Kleinste zulässige Abtastrate. Material darunter lässt sich zwar
/// hochrechnen, klingt aber vorhersehbar schlecht.
/// </param>
public sealed record AudioLimits(
    TimeSpan MinReferenceDuration,
    TimeSpan MaxReferenceDuration,
    TimeSpan MaxSourceDuration,
    int MinSampleRate)
{
    /// <summary>Die Voreinstellung.</summary>
    public static AudioLimits Default { get; } = new(
        MinReferenceDuration: TimeSpan.FromSeconds(3),
        MaxReferenceDuration: TimeSpan.FromSeconds(25),
        MaxSourceDuration: TimeSpan.FromMinutes(10),
        MinSampleRate: 16000);
}

/// <summary>Das Ergebnis einer Prüfung.</summary>
/// <param name="ErrorCode">Der Fehlercode, oder <c>null</c>, wenn die Datei brauchbar ist.</param>
/// <param name="Message">Eine für Aufrufer gedachte Begründung.</param>
/// <param name="WillBeTruncatedTo">
/// Gesetzt, wenn die Datei zwar angenommen, aber gekürzt wird.
/// </param>
public sealed record AudioValidationResult(
    ConversionErrorCode? ErrorCode,
    string? Message,
    TimeSpan? WillBeTruncatedTo = null)
{
    /// <summary>Ob die Datei verwendet werden kann.</summary>
    public bool IsValid => ErrorCode is null;

    /// <summary>Eine gültige Datei ohne Beanstandung.</summary>
    public static AudioValidationResult Valid { get; } = new(null, null);

    /// <summary>Eine gültige Datei, die auf die angegebene Länge gekürzt wird.</summary>
    public static AudioValidationResult Truncated(TimeSpan to) =>
        new(null, $"Die Aufnahme wird auf {to.TotalSeconds:0.###} Sekunden gekürzt.", to);

    /// <summary>Eine abgelehnte Datei.</summary>
    public static AudioValidationResult Invalid(ConversionErrorCode code, string message) =>
        new(code, message);
}

/// <summary>
/// Entscheidet, ob eine hochgeladene Datei für die Konvertierung taugt.
/// </summary>
/// <remarks>
/// Die Prüfung gibt Fehlercodes zurück, statt zu werfen: Ein ungeeigneter Upload
/// ist ein erwarteter Normalfall, kein Ausnahmezustand. Dadurch bleibt die Regel
/// ohne ffmpeg testbar, und der aufrufende Anwendungsfall kann den Code direkt in
/// eine Antwort übersetzen.
/// </remarks>
public static class AudioValidationPolicy
{
    /// <summary>
    /// Codecs, die angenommen werden. ffmpeg dekodiert sie alle, sodass Clients
    /// kein WAV liefern müssen — die Liste ist bewusst eng gehalten und enthält
    /// nur verbreitete Audioformate.
    /// </summary>
    public static IReadOnlySet<string> SupportedCodecs { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "pcm_s16le", "pcm_s24le", "pcm_s32le", "pcm_f32le", "pcm_u8",
            "mp3", "flac", "aac", "alac", "vorbis", "opus",
        };

    /// <summary>Prüft eine Datei in ihrer jeweiligen Rolle.</summary>
    public static AudioValidationResult Validate(
        AudioProperties? properties,
        AudioRole role,
        AudioLimits? limits = null)
    {
        limits ??= AudioLimits.Default;

        if (properties is null)
        {
            return AudioValidationResult.Invalid(
                ConversionErrorCode.InvalidAudio,
                "Die Datei enthält keinen lesbaren Audiostrom.");
        }

        if (!SupportedCodecs.Contains(properties.Codec))
        {
            return AudioValidationResult.Invalid(
                ConversionErrorCode.UnsupportedFormat,
                $"Der Codec '{properties.Codec}' wird nicht unterstützt.");
        }

        if (properties.Duration <= TimeSpan.Zero)
        {
            return AudioValidationResult.Invalid(
                ConversionErrorCode.InvalidAudio,
                "Die Aufnahme hat keine Länge.");
        }

        if (properties.Channels is < 1 or > 2)
        {
            return AudioValidationResult.Invalid(
                ConversionErrorCode.UnsupportedFormat,
                $"Es werden ein oder zwei Kanäle erwartet, gefunden: {properties.Channels}.");
        }

        if (properties.SampleRate < limits.MinSampleRate)
        {
            return AudioValidationResult.Invalid(
                ConversionErrorCode.UnsupportedFormat,
                $"Die Abtastrate liegt mit {properties.SampleRate} Hz unter dem Mindestwert "
                + $"von {limits.MinSampleRate} Hz.");
        }

        return role switch
        {
            AudioRole.Reference => ValidateReference(properties, limits),
            AudioRole.Source => ValidateSource(properties, limits),
            _ => AudioValidationResult.Valid,
        };
    }

    private static AudioValidationResult ValidateReference(AudioProperties properties, AudioLimits limits)
    {
        if (properties.Duration < limits.MinReferenceDuration)
        {
            return AudioValidationResult.Invalid(
                ConversionErrorCode.ReferenceTooShort,
                $"Die Referenz muss mindestens {limits.MinReferenceDuration.TotalSeconds:0.###} "
                + "Sekunden lang sein.");
        }

        // Längere Referenzen werden angenommen, aber gekürzt: Seed-VC verwendet
        // ohnehin nur die ersten 25 Sekunden. Das offen zu melden ist ehrlicher,
        // als den Aufrufer glauben zu lassen, seine ganze Datei werde genutzt.
        return properties.Duration > limits.MaxReferenceDuration
            ? AudioValidationResult.Truncated(limits.MaxReferenceDuration)
            : AudioValidationResult.Valid;
    }

    private static AudioValidationResult ValidateSource(AudioProperties properties, AudioLimits limits)
    {
        return properties.Duration > limits.MaxSourceDuration
            ? AudioValidationResult.Invalid(
                ConversionErrorCode.InvalidAudio,
                $"Die Quellaufnahme überschreitet die erlaubte Länge von "
                + $"{limits.MaxSourceDuration.TotalMinutes:0.###} Minuten.")
            : AudioValidationResult.Valid;
    }
}
