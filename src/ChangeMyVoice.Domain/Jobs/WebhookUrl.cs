namespace ChangeMyVoice.Domain.Jobs;

/// <summary>
/// Die Adresse, die ein Auftrag aufruft, sobald er endet.
/// </summary>
/// <remarks>
/// Der Aufruf ersetzt das Abfragen im Abstand weniger Sekunden, nicht die
/// Statusabfrage selbst: Er ist ein Anstoß, nachzusehen. Deshalb braucht er
/// keine Signatur — ein gefälschter Aufruf führt nur zu einer Abfrage, die den
/// wahren Zustand liefert.
/// </remarks>
public sealed record WebhookUrl
{
    /// <summary>Längstmögliche Adresse.</summary>
    public const int MaxLength = 2048;

    private WebhookUrl(Uri value) => Value = value;

    /// <summary>Die geprüfte, absolute Adresse.</summary>
    public Uri Value { get; }

    /// <summary>
    /// Versucht, eine Adresse zu bilden.
    /// </summary>
    /// <param name="input">Die Eingabe des Aufrufers.</param>
    /// <param name="allowedHosts">
    /// Die Rechner, die aufgerufen werden dürfen. Leer heißt: jeder. Der Dienst
    /// ruft die Adresse von sich aus auf; wer ihn in einem Netz betreibt, in dem
    /// das nicht jeder Rechner sein soll, schränkt es hier ein.
    /// </param>
    /// <param name="url">Die gebildete Adresse.</param>
    /// <param name="error">Der erste Verstoß, falls ungültig.</param>
    public static bool TryCreate(
        string? input,
        IReadOnlyCollection<string> allowedHosts,
        out WebhookUrl url,
        out string? error)
    {
        url = null!;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "webhookUrl darf nicht leer sein.";
            return false;
        }

        var trimmed = input.Trim();

        if (trimmed.Length > MaxLength)
        {
            error = $"webhookUrl darf höchstens {MaxLength} Zeichen lang sein.";
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "webhookUrl muss eine absolute http- oder https-Adresse sein.";
            return false;
        }

        // Zugangsdaten in der Adresse landen in Protokollen und der Datenbank.
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "webhookUrl darf keine Zugangsdaten enthalten.";
            return false;
        }

        if (allowedHosts.Count > 0 &&
            !allowedHosts.Any(h => string.Equals(h, uri.Host, StringComparison.OrdinalIgnoreCase)))
        {
            error = $"Der Rechner '{uri.Host}' ist für webhookUrl nicht freigegeben.";
            return false;
        }

        url = new WebhookUrl(uri);
        error = null;
        return true;
    }

    /// <summary>Stellt eine gespeicherte Adresse ohne erneute Prüfung wieder her.</summary>
    public static WebhookUrl Rehydrate(string value) => new(new Uri(value, UriKind.Absolute));

    /// <inheritdoc />
    public override string ToString() => Value.AbsoluteUri;
}
