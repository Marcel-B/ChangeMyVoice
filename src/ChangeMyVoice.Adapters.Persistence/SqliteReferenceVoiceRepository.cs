using ChangeMyVoice.Application.Ports;
using ChangeMyVoice.Domain.Audio;
using ChangeMyVoice.Domain.Voices;
using Dapper;

namespace ChangeMyVoice.Adapters.Persistence;

/// <summary>Speichert Referenzstimmen in SQLite.</summary>
public sealed class SqliteReferenceVoiceRepository(SqliteConnectionFactory factory)
    : IReferenceVoiceRepository
{
    /// <inheritdoc />
    public async Task SaveAsync(ReferenceVoice voice, CancellationToken cancellationToken = default)
    {
        await using var connection = factory.Open();

        await connection.ExecuteAsync(
            """
            INSERT INTO reference_voices (
                id, label, label_key, created_at_utc,
                stored_codec, stored_duration_ms, stored_sample_rate, stored_channels,
                original_codec, original_duration_ms, original_sample_rate, original_channels)
            VALUES (
                @Id, @Label, @LabelKey, @CreatedAt,
                @StoredCodec, @StoredDuration, @StoredSampleRate, @StoredChannels,
                @OriginalCodec, @OriginalDuration, @OriginalSampleRate, @OriginalChannels)
            ON CONFLICT(id) DO UPDATE SET
                label = excluded.label,
                label_key = excluded.label_key;
            """,
            new
            {
                Id = voice.Id.ToString(),
                Label = voice.Label.Value,
                LabelKey = voice.Label.ComparisonKey,
                CreatedAt = voice.CreatedAtUtc.ToString("O"),
                StoredCodec = voice.StoredAudio.Codec,
                StoredDuration = (long)voice.StoredAudio.Duration.TotalMilliseconds,
                StoredSampleRate = voice.StoredAudio.SampleRate,
                StoredChannels = voice.StoredAudio.Channels,
                OriginalCodec = voice.OriginalAudio.Codec,
                OriginalDuration = (long)voice.OriginalAudio.Duration.TotalMilliseconds,
                OriginalSampleRate = voice.OriginalAudio.SampleRate,
                OriginalChannels = voice.OriginalAudio.Channels,
            }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ReferenceVoice?> FindAsync(
        VoiceId id, CancellationToken cancellationToken = default)
    {
        await using var connection = factory.Open();

        var row = await connection.QuerySingleOrDefaultAsync<VoiceRow>(
            "SELECT * FROM reference_voices WHERE id = @Id;",
            new { Id = id.ToString() }).ConfigureAwait(false);

        return row?.ToDomain();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReferenceVoice>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = factory.Open();

        var rows = await connection.QueryAsync<VoiceRow>(
            "SELECT * FROM reference_voices ORDER BY label_key;").ConfigureAwait(false);

        return rows.Select(r => r.ToDomain()).ToArray();
    }

    /// <inheritdoc />
    public async Task<bool> ExistsWithLabelAsync(
        VoiceLabel label, CancellationToken cancellationToken = default)
    {
        await using var connection = factory.Open();

        return await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM reference_voices WHERE label_key = @Key;",
            new { Key = label.ComparisonKey }).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(VoiceId id, CancellationToken cancellationToken = default)
    {
        await using var connection = factory.Open();

        await connection.ExecuteAsync(
            "DELETE FROM reference_voices WHERE id = @Id;",
            new { Id = id.ToString() }).ConfigureAwait(false);
    }

    private sealed class VoiceRow
    {
        public string Id { get; init; } = string.Empty;
        public string Label { get; init; } = string.Empty;
        public string Created_At_Utc { get; init; } = string.Empty;
        public string Stored_Codec { get; init; } = string.Empty;
        public long Stored_Duration_Ms { get; init; }
        public int Stored_Sample_Rate { get; init; }
        public int Stored_Channels { get; init; }
        public string Original_Codec { get; init; } = string.Empty;
        public long Original_Duration_Ms { get; init; }
        public int Original_Sample_Rate { get; init; }
        public int Original_Channels { get; init; }

        public ReferenceVoice ToDomain() => ReferenceVoice.Rehydrate(
            new VoiceId(Guid.Parse(Id)),
            VoiceLabel.Create(Label),
            DateTimeOffset.Parse(Created_At_Utc, System.Globalization.CultureInfo.InvariantCulture),
            new AudioProperties(
                Stored_Codec, TimeSpan.FromMilliseconds(Stored_Duration_Ms),
                Stored_Sample_Rate, Stored_Channels),
            new AudioProperties(
                Original_Codec, TimeSpan.FromMilliseconds(Original_Duration_Ms),
                Original_Sample_Rate, Original_Channels));
    }
}
