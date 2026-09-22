using ChangeMyVoice.Application.Common;
using ChangeMyVoice.Application.Ports;
using ChangeMyVoice.Domain.Jobs;

namespace ChangeMyVoice.Application.UseCases.Jobs;

/// <summary>Die Abfrage einer Auftragsliste.</summary>
/// <param name="Status">Auf diesen Zustand einschränken, oder <c>null</c> für alle.</param>
/// <param name="Limit">Wie viele Einträge höchstens.</param>
/// <param name="Offset">Wie viele Einträge übersprungen werden.</param>
public sealed record ListConversionJobsQuery(JobStatus? Status, int Limit, int Offset)
{
    /// <summary>Wie viele Einträge zurückkommen, wenn nichts angegeben ist.</summary>
    public const int DefaultLimit = 50;

    /// <summary>
    /// Die Obergrenze je Abfrage. Sie schützt den Dienst davor, dass eine
    /// Oberfläche versehentlich den gesamten Bestand auf einmal anfordert.
    /// </summary>
    public const int MaxLimit = 200;

    /// <summary>Prüft und begrenzt die Eingaben.</summary>
    public static bool TryCreate(
        string? status, int? limit, int? offset,
        out ListConversionJobsQuery query, out string? error)
    {
        query = new ListConversionJobsQuery(null, DefaultLimit, 0);
        error = null;

        JobStatus? parsed = null;

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<JobStatus>(status, ignoreCase: true, out var value))
            {
                error = "Unbekannter Zustand. Erlaubt sind: "
                    + string.Join(", ", Enum.GetNames<JobStatus>().Select(n => n.ToUpperInvariant()));
                return false;
            }

            parsed = value;
        }

        var take = limit ?? DefaultLimit;
        if (take is < 1 || take > MaxLimit)
        {
            error = $"limit muss zwischen 1 und {MaxLimit} liegen.";
            return false;
        }

        var skip = offset ?? 0;
        if (skip < 0)
        {
            error = "offset darf nicht negativ sein.";
            return false;
        }

        query = new ListConversionJobsQuery(parsed, take, skip);
        return true;
    }
}

/// <summary>Ein Ausschnitt aus der Auftragsliste.</summary>
/// <param name="Items">Die Aufträge dieser Seite.</param>
/// <param name="Total">Wie viele Aufträge insgesamt zum Filter passen.</param>
/// <param name="Limit">Die verwendete Seitengröße.</param>
/// <param name="Offset">Der verwendete Versatz.</param>
public sealed record ConversionJobPage(
    IReadOnlyList<ConversionJobView> Items,
    int Total,
    int Limit,
    int Offset);

/// <summary>Liefert eine Übersicht der Aufträge.</summary>
public interface IListConversionJobs
{
    /// <summary>Führt den Anwendungsfall aus.</summary>
    Task<Result<ConversionJobPage>> ExecuteAsync(
        ListConversionJobsQuery query, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class ListConversionJobs(IConversionJobRepository jobs) : IListConversionJobs
{
    /// <inheritdoc />
    public async Task<Result<ConversionJobPage>> ExecuteAsync(
        ListConversionJobsQuery query, CancellationToken cancellationToken = default)
    {
        var total = await jobs.CountAsync(query.Status, cancellationToken).ConfigureAwait(false);

        var items = await jobs
            .ListAsync(query.Status, query.Limit, query.Offset, cancellationToken)
            .ConfigureAwait(false);

        return Result<ConversionJobPage>.Success(new ConversionJobPage(
            items.Select(ConversionJobView.From).ToArray(), total, query.Limit, query.Offset));
    }
}
