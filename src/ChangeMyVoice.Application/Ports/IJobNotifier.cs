using ChangeMyVoice.Application.UseCases.Jobs;
using ChangeMyVoice.Domain.Jobs;

namespace ChangeMyVoice.Application.Ports;

/// <summary>Die Nachricht, dass ein Auftrag geendet hat.</summary>
/// <param name="Target">Die Adresse, die der Aufrufer beim Anlegen genannt hat.</param>
/// <param name="Job">Der Auftrag in seinem Endzustand.</param>
public sealed record JobFinishedNotification(WebhookUrl Target, ConversionJobView Job);

/// <summary>Meldet das Ende eines Auftrags an den, der ihn angelegt hat.</summary>
/// <remarks>
/// Das Einreihen darf nicht warten: Es geschieht mitten im Arbeiter, und ein
/// langsamer oder unerreichbarer Empfänger darf die Warteschlange nicht
/// aufhalten. Zustellung und Wiederholungen übernimmt der Adapter.
/// </remarks>
public interface IJobNotifier
{
    /// <summary>Reiht die Nachricht zur Zustellung ein.</summary>
    void Enqueue(JobFinishedNotification notification);
}

/// <summary>Hilfen rund um <see cref="IJobNotifier" />.</summary>
public static class JobNotifierExtensions
{
    /// <summary>
    /// Meldet das Ende, falls der Auftrag beendet ist und eine Adresse trägt.
    /// </summary>
    public static void NotifyFinished(this IJobNotifier notifier, ConversionJob job)
    {
        if (job.IsTerminal && job.WebhookUrl is { } target)
        {
            notifier.Enqueue(new JobFinishedNotification(target, ConversionJobView.From(job)));
        }
    }
}
