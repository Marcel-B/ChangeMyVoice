using System.Threading.Channels;
using ChangeMyVoice.Application.Ports;
using ChangeMyVoice.Domain.Jobs;

namespace ChangeMyVoice.Api.Hosting;

/// <summary>
/// Die Warteschlange der angenommenen Aufträge.
/// </summary>
/// <remarks>
/// Begrenzt und ohne Blockieren: Ist sie voll, lehnt die Annahme sofort ab. Das
/// ist ein ehrlicheres Signal an das Gateway als eine unbegrenzt wachsende
/// Schlange, deren Aufträge später reihenweise in den Zeitablauf laufen.
/// </remarks>
public sealed class ChannelJobQueue : IJobQueue
{
    private readonly Channel<JobId> _channel;

    /// <summary>Erzeugt die Warteschlange.</summary>
    public ChannelJobQueue(int capacity = 64) =>
        _channel = Channel.CreateBounded<JobId>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    /// <inheritdoc />
    public bool TryEnqueue(JobId jobId) => _channel.Writer.TryWrite(jobId);

    /// <inheritdoc />
    public IAsyncEnumerable<JobId> DequeueAllAsync(CancellationToken cancellationToken = default) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}

/// <summary>Die Kennung dieses Dienstlaufs.</summary>
/// <remarks>
/// Wird bei jedem Auftrag mitgeschrieben. Erst dadurch lässt sich beim Start
/// unterscheiden, welche laufenden Aufträge aus einem abgestürzten
/// Vorgängerprozess stammen.
/// </remarks>
public sealed class ServiceInstance : IServiceInstance
{
    /// <inheritdoc />
    public Guid InstanceId { get; } = Guid.NewGuid();
}
