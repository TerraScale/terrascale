using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Hosting;
using TerraScale.Authentication.Storage;
using ZLogger;

namespace TerraScale.Authentication.Features.Platform;

public sealed class DeletionCleanup(
    AuthSchema schema, AuthData data, TimeProvider clock, ILogger<DeletionCleanup> logger) : BackgroundService
{
    private const int BatchSize = 20;
    private const int RecordBudget = 100;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    // A failing job backs off from one minute, doubling, to this; progress (another batch remains) retries at once.
    private static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(30);

    private static readonly Meter Meter = new(AuthMetrics.MeterName);

    // Logical cleanup outcomes per job attempt: verified, a failure code, or invalid.
    private static readonly Counter<long> Outcomes = Meter.CreateCounter<long>("ts_auth.deletion_cleanup", description: "Account and scope deletion cleanup attempts by outcome.");

    internal static TimeSpan Retry(string? failure, int attempts, double jitter) =>
        failure is null or "batch_remaining" or "records_remaining" ? Interval
            : TimeSpan.FromTicks((long)(Math.Min(Interval.Ticks << Math.Min(attempts, 5), MaxRetry.Ticks) * jitter));

    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var jobs = await data.ReadAsync((db, token) => db.DeletionJobs.Where(job => job.NextAttemptAt <= now && job.AuthLogicalVerifiedAt == null)
            .OrderBy(job => job.NextAttemptAt).Take(BatchSize).WithVersion().ToListAsync(token), cancellationToken).ConfigureAwait(false);
        foreach (var rowData in jobs)
        {
            var job = rowData.Value;
            if (!LogicallyValid(job))
            {
                Outcomes.Add(1, new KeyValuePair<string, object?>("outcome", "invalid"));
                logger.ZLogError($"Auth deletion job {job.Id:@RecordId} is malformed and cannot be processed; an operator must repair or remove it");
                continue;
            }

            string? failure;
            try
            {
                failure = await this.CleanAsync(job, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                // Persist only an allowlisted code: exceptions may contain PII or credentials.
                failure = error is HttpRequestException or TimeoutException ? "storage_unavailable" : "cleanup_failed";
                try
                {
                    logger.ZLogWarning(error, $"Auth logical deletion cleanup failed; the durable job remains pending");
                }
                catch (Exception logFailure) when (logFailure is not OutOfMemoryException)
                {
                    // A failed provider must not prevent the durable retry update.
                    _ = logFailure;
                }
            }

            var finished = clock.GetUtcNow();
            var attempts = Math.Clamp(job.Attempts, 0, 999_999);
            var wait = Retry(failure, attempts, 0.8 + (Random.Shared.NextDouble() * 0.4));
            Outcomes.Add(1, new KeyValuePair<string, object?>("outcome", failure ?? "verified"));
            if (failure is not null and not ("batch_remaining" or "records_remaining"))
            {
                // A blocked deletion (handoff required, scope mismatch, storage) is visible every attempt, with its deadline.
                logger.ZLogWarning($"Auth deletion job {job.Id:@JobId} not finished: {failure:@FailureCode} after {attempts + 1:@Attempts} attempts; retrying in {(long)wait.TotalSeconds:@RetrySeconds} s, due {job.DueAt:@DueAt}");
            }

            var updated = job with
            {
                Attempts = attempts + 1,
                NextAttemptAt = finished + wait,
                FailureCode = failure,
                AuthLogicalVerifiedAt = failure is null ? finished : null,
            };
            await data.SaveAsync(Change.Update(rowData, updated), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RunHistoryOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var jobs = await data.ReadAsync((db, token) => db.DeletionJobs.Where(job => job.NextAttemptAt <= now && job.AuthLogicalVerifiedAt != null && (job.Scope != "organization" || job.HistoryCompletedAt == null)
                && (job.HistoryCompletedAt == null || job.DashboardVerifiedAt != null || job.InstanceId != PlatformDirectory.Instance))
            .OrderBy(job => job.NextAttemptAt).Take(BatchSize).WithVersion().ToListAsync(token), cancellationToken).ConfigureAwait(false);
        foreach (var rowData in jobs)
        {
            var job = rowData.Value;
            if (!HistoryValid(job) || job.AuthLogicalVerifiedAt is null)
            {
                continue;
            }

            if (job.HistoryCompletedAt is not null)
            {
                if (clock.GetUtcNow() >= job.FenceExpiresAt
                    && (job.InstanceId != PlatformDirectory.Instance || job.DashboardVerifiedAt is not null))
                {
                    _ = await data.SaveAsync(Change.Delete(rowData), cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            var finished = clock.GetUtcNow();
            var updated = job with
            {
                NextAttemptAt = job.FenceExpiresAt!.Value,
                HistoryStatus = "complete",
                HistoryCompletedAt = finished,
            };
            await data.SaveAsync(Change.Update(rowData, updated), cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool LogicallyValid([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] DeletionJob? job) =>
        job is not null && job.Id == DeletionJob.Key(job.InstanceId, job.Scope, job.SubjectId) && job.DueAt == job.RequestedAt.AddDays(30);

    private static bool HistoryValid([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] DeletionJob? job) =>
        LogicallyValid(job) && job.FenceExpiresAt == job.RequestedAt + DeletionJob.CredentialBound && job.ErasureId is { Length: 26 } erasure && NUlid.Ulid.TryParse(erasure, out _);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await schema.Ready.WaitAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(Interval, clock);
        try
        {
            do
            {
                try
                {
                    await this.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                    await this.RunHistoryOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        // Safe logging keeps only the exception's types and frames, never its message.
                        logger.ZLogWarning(error, $"Auth deletion retry pass failed; retrying at the next interval");
                    }
                    catch (Exception logFailure) when (logFailure is not OutOfMemoryException)
                    {
                        // Logging must not stop the worker while storage remains unavailable.
                        _ = logFailure;
                    }
                }
                catch (Exception error) when (stoppingToken.IsCancellationRequested && error is not OutOfMemoryException)
                {
                    // Shutdown already asked this worker to stop: a storage fault racing it must not fault the worker.
                    logger.ZLogDebug(error, $"Auth deletion cleanup interrupted by shutdown");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping this worker is normal even while the host remains available.
        }
    }

    private Task<string?> CleanAsync(DeletionJob job, CancellationToken cancellationToken) =>
        data.ReadAsync(async (db, token) =>
        {
            var (instance, subject) = (job.InstanceId, job.SubjectId);
            var root = job.Scope switch
            {
                "organization" when instance == PlatformDirectory.Instance => (bool?)await db.Organizations.AnyAsync(row => row.Id == subject, token).ConfigureAwait(false),
                "project" when instance == PlatformDirectory.Instance => await db.Projects.AnyAsync(row => row.Id == subject, token).ConfigureAwait(false),
                "account" => await db.Users.AnyAsync(row => row.Id == subject, token).ConfigureAwait(false),
                _ => null,
            };
            if (root is not false)
            {
                return "scope_not_deleted";
            }

            switch (job.Scope)
            {
                case "organization":
                    return await db.Projects.AnyAsync(row => row.OrganizationId == subject, token).ConfigureAwait(false) ? "project_handoff_required" : null;
                case "project":
                    return null;
            }

            await db.Passcodes.Where(row => row.InstanceId == instance && row.UserId == subject).ExecuteDeleteAsync(token).ConfigureAwait(false);
            await db.Set<OidcToken>().Where(row => row.InstanceId == instance && row.Subject == subject).ExecuteDeleteAsync(token).ConfigureAwait(false);
            await db.Set<OidcAuthorization>().Where(row => row.InstanceId == instance && row.Subject == subject).ExecuteDeleteAsync(token).ConfigureAwait(false);
            return await db.Memberships.AnyAsync(row => row.UserId == subject, token).ConfigureAwait(false) ? "organization_handoff_required" : null;
        }, cancellationToken);
}
