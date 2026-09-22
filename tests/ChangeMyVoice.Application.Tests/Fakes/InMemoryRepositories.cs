using System.Collections.Concurrent;
using ChangeMyVoice.Application.Ports;
using ChangeMyVoice.Domain.Jobs;
using ChangeMyVoice.Domain.Voices;

namespace ChangeMyVoice.Application.Tests.Fakes;

/// <summary>Referenzstimmen im Arbeitsspeicher.</summary>
public sealed class InMemoryReferenceVoiceRepository : IReferenceVoiceRepository
{
    private readonly ConcurrentDictionary<VoiceId, ReferenceVoice> _voices = new();

    public Task SaveAsync(ReferenceVoice voice, CancellationToken cancellationToken = default)
    {
        _voices[voice.Id] = voice;
        return Task.CompletedTask;
    }

    public Task<ReferenceVoice?> FindAsync(VoiceId id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_voices.GetValueOrDefault(id));

    public Task<IReadOnlyList<ReferenceVoice>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ReferenceVoice>>(
            _voices.Values.OrderBy(v => v.Label.Value, StringComparer.OrdinalIgnoreCase).ToArray());

    public Task<bool> ExistsWithLabelAsync(VoiceLabel label, CancellationToken cancellationToken = default) =>
        Task.FromResult(_voices.Values.Any(v => v.Label == label));

    public Task DeleteAsync(VoiceId id, CancellationToken cancellationToken = default)
    {
        _voices.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    public int Count => _voices.Count;
}

/// <summary>Aufträge im Arbeitsspeicher.</summary>
public sealed class InMemoryConversionJobRepository : IConversionJobRepository
{
    private readonly ConcurrentDictionary<JobId, ConversionJob> _jobs = new();

    public Task SaveAsync(ConversionJob job, CancellationToken cancellationToken = default)
    {
        _jobs[job.Id] = job;
        return Task.CompletedTask;
    }

    public Task<ConversionJob?> FindAsync(JobId id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_jobs.GetValueOrDefault(id));

    public Task<IReadOnlyList<ConversionJob>> ListUnpurgedAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ConversionJob>>(
            _jobs.Values.Where(j => !j.ArtifactsPurged).ToArray());

    public Task<bool> HasActiveJobForVoiceAsync(VoiceId voiceId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_jobs.Values.Any(j => j.VoiceId == voiceId && !j.IsTerminal));

    public Task DeleteAsync(JobId id, CancellationToken cancellationToken = default)
    {
        _jobs.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    public IReadOnlyCollection<ConversionJob> All => _jobs.Values.ToArray();
}
