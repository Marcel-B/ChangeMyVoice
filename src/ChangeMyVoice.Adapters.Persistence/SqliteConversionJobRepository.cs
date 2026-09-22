using System.Globalization;
using ChangeMyVoice.Application.Ports;
using ChangeMyVoice.Domain.Jobs;
using ChangeMyVoice.Domain.Voices;
using Dapper;

namespace ChangeMyVoice.Adapters.Persistence;

/// <summary>Speichert Konvertierungsaufträge in SQLite.</summary>
public sealed class SqliteConversionJobRepository(SqliteConnectionFactory factory)
    : IConversionJobRepository
{
    /// <inheritdoc />
    public async Task SaveAsync(ConversionJob job, CancellationToken cancellationToken = default)
    {
        await using var connection = factory.Open();

        await connection.ExecuteAsync(
            """
            INSERT INTO conversion_jobs (
                id, voice_id, voice_label, status,
                diffusion_steps, inference_cfg_rate, length_adjust, f0_condition, fp16,
                created_at_utc, started_at_utc, finished_at_utc, downloaded_at_utc,
                error_code, error_message, output_size_bytes, output_sha256,
                instance_id, inference_process_id, artifacts_purged)
            VALUES (
                @Id, @VoiceId, @VoiceLabel, @Status,
                @DiffusionSteps, @InferenceCfgRate, @LengthAdjust, @F0Condition, @Fp16,
                @CreatedAt, @StartedAt, @FinishedAt, @DownloadedAt,
                @ErrorCode, @ErrorMessage, @OutputSize, @OutputSha,
                @InstanceId, @ProcessId, @Purged)
            ON CONFLICT(id) DO UPDATE SET
                status = excluded.status,
                started_at_utc = excluded.started_at_utc,
                finished_at_utc = excluded.finished_at_utc,
                downloaded_at_utc = excluded.downloaded_at_utc,
                error_code = excluded.error_code,
                error_message = excluded.error_message,
                output_size_bytes = excluded.output_size_bytes,
                output_sha256 = excluded.output_sha256,
                instance_id = excluded.instance_id,
                inference_process_id = excluded.inference_process_id,
                artifacts_purged = excluded.artifacts_purged;
            """,
            new
            {
                Id = job.Id.ToString(),
                VoiceId = job.VoiceId.ToString(),
                job.VoiceLabel,
                Status = job.Status.ToString(),
                job.Options.DiffusionSteps,
                job.Options.InferenceCfgRate,
                job.Options.LengthAdjust,
                F0Condition = job.Options.F0Condition ? 1 : 0,
                Fp16 = job.Options.Fp16 ? 1 : 0,
                CreatedAt = job.CreatedAtUtc.ToString("O"),
                StartedAt = job.StartedAtUtc?.ToString("O"),
                FinishedAt = job.FinishedAtUtc?.ToString("O"),
                DownloadedAt = job.DownloadedAtUtc?.ToString("O"),
                ErrorCode = job.Error?.Code.ToString(),
                ErrorMessage = job.Error?.Message,
                OutputSize = job.OutputSizeBytes,
                OutputSha = job.OutputSha256,
                InstanceId = job.InstanceId.ToString(),
                ProcessId = job.InferenceProcessId,
                Purged = job.ArtifactsPurged ? 1 : 0,
            }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ConversionJob?> FindAsync(
        JobId id, CancellationToken cancellationToken = default)
    {
        await using var connection = factory.Open();

        var row = await connection.QuerySingleOrDefaultAsync<JobRow>(
            "SELECT * FROM conversion_jobs WHERE id = @Id;",
            new { Id = id.ToString() }).ConfigureAwait(false);

        return row?.ToDomain();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConversionJob>> ListUnpurgedAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = factory.Open();

        var rows = await connection.QueryAsync<JobRow>(
            "SELECT * FROM conversion_jobs WHERE artifacts_purged = 0 ORDER BY created_at_utc;")
            .ConfigureAwait(false);

        return rows.Select(r => r.ToDomain()).ToArray();
    }

    /// <inheritdoc />
    public async Task<bool> HasActiveJobForVoiceAsync(
        VoiceId voiceId, CancellationToken cancellationToken = default)
    {
        await using var connection = factory.Open();

        return await connection.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(1) FROM conversion_jobs
            WHERE voice_id = @VoiceId AND status IN ('Queued', 'Running');
            """,
            new { VoiceId = voiceId.ToString() }).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(JobId id, CancellationToken cancellationToken = default)
    {
        await using var connection = factory.Open();

        await connection.ExecuteAsync(
            "DELETE FROM conversion_jobs WHERE id = @Id;",
            new { Id = id.ToString() }).ConfigureAwait(false);
    }

    private sealed class JobRow
    {
        public string Id { get; init; } = string.Empty;
        public string Voice_Id { get; init; } = string.Empty;
        public string Voice_Label { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public int Diffusion_Steps { get; init; }
        public double Inference_Cfg_Rate { get; init; }
        public double Length_Adjust { get; init; }
        public long F0_Condition { get; init; }
        public long Fp16 { get; init; }
        public string Created_At_Utc { get; init; } = string.Empty;
        public string? Started_At_Utc { get; init; }
        public string? Finished_At_Utc { get; init; }
        public string? Downloaded_At_Utc { get; init; }
        public string? Error_Code { get; init; }
        public string? Error_Message { get; init; }
        public long? Output_Size_Bytes { get; init; }
        public string? Output_Sha256 { get; init; }
        public string Instance_Id { get; init; } = string.Empty;
        public long? Inference_Process_Id { get; init; }
        public long Artifacts_Purged { get; init; }

        public ConversionJob ToDomain()
        {
            ConversionOptions.TryCreate(
                Diffusion_Steps, Inference_Cfg_Rate, Length_Adjust,
                F0_Condition != 0, Fp16 != 0, out var options, out _);

            JobError? error = null;
            if (Error_Code is not null &&
                Enum.TryParse<ConversionErrorCode>(Error_Code, out var code))
            {
                error = new JobError(code, Error_Message ?? string.Empty);
            }

            return ConversionJob.Rehydrate(
                new JobId(Guid.Parse(Id)),
                new VoiceId(Guid.Parse(Voice_Id)),
                Voice_Label,
                options,
                Enum.Parse<JobStatus>(Status),
                Parse(Created_At_Utc)!.Value,
                Parse(Started_At_Utc),
                Parse(Finished_At_Utc),
                Parse(Downloaded_At_Utc),
                error,
                Output_Size_Bytes,
                Output_Sha256,
                Guid.Parse(Instance_Id),
                (int?)Inference_Process_Id,
                Artifacts_Purged != 0);
        }

        private static DateTimeOffset? Parse(string? value) =>
            value is null ? null : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    }
}
