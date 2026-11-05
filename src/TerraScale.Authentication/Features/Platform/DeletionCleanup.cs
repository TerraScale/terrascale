using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Nodes;
using TerraScale.Authentication.Hosting;
using TerraScale.Authentication.Store;
using ZLogger;

namespace TerraScale.Authentication.Features.Platform;

public sealed class DeletionCleanup(
    AuthStore store, PlatformDirectory directory, TimeProvider clock, ILogger<DeletionCleanup> logger) : BackgroundService
{
    private const int BatchSize = 20;
    private const int RecordBudget = 100;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    // A failing job backs off from one minute, doubling, to this; progress (another batch remains) retries at once.
    private static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(30);

    private static readonly Meter Meter = new(AuthMetrics.MeterName);

    // Logical cleanup outcomes per job attempt: verified, a failure code, or invalid.
    private static readonly Counter<long> Outcomes = Meter.CreateCounter<long>("ts_auth.deletion_cleanup", description: "Account and scope deletion cleanup attempts by outcome.");

    /// <summary>The wait before a job's next attempt: <paramref name="jitter"/> (0.8–1.2) spreads failing jobs apart.</summary>
    internal static TimeSpan Retry(string? failure, int attempts, double jitter) =>
        failure is null or "batch_remaining" or "records_remaining" ? Interval
            : TimeSpan.FromTicks((long)(Math.Min(Interval.Ticks << Math.Min(attempts, 5), MaxRetry.Ticks) * jitter));

    /// <summary>Runs one fair batch. A failing job moves behind waiting jobs; deadlines are never extended.</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        using var filter = JsonDocument.Parse(new JsonObject
        {
            ["and"] = new JsonArray(
                new JsonObject { ["lte"] = new JsonArray("/nextAttemptAt", JsonValue.Create(now)) },
                new JsonObject { ["eq"] = new JsonArray("/authLogicalVerifiedAt", null) }),
        }.ToJsonString());
        var jobs = await store.QueryAsync(AuthStore.DeletionJobTable, filter.RootElement, BatchSize,
            [new StoreSort("/nextAttemptAt")], cancellationToken).ConfigureAwait(false);
        foreach (var record in jobs)
        {
            var job = record.Body.Deserialize(DeletionJson.Default.DeletionJob);
            // Logical cleanup needs only the request's own identity and deadline (jobs written before the history fields too).
            if (!LogicallyValid(job))
            {
                // Never silent: its key does not match its own fields, so it cannot be rewritten safely; an operator must act.
                Outcomes.Add(1, new KeyValuePair<string, object?>("outcome", "invalid"));
                logger.ZLogError($"Auth deletion job {record.Id:@RecordId} is malformed and cannot be processed; an operator must repair or remove it");
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
            // Retain the job as the write fence. This timestamp certifies only the verified auth logical scope.
            await store.CompareAndPutAsync(AuthStore.DeletionJobTable, job.InstanceId, job.Id,
                JsonSerializer.SerializeToElement(updated, DeletionJson.Default.DeletionJob), record.Version, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RunHistoryOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        using var filter = JsonDocument.Parse(new JsonObject
        {
            ["and"] = new JsonArray(
                new JsonObject { ["lte"] = new JsonArray("/nextAttemptAt", JsonValue.Create(now)) },
                new JsonObject { ["ne"] = new JsonArray("/authLogicalVerifiedAt", null) },
                new JsonObject
                {
                    ["or"] = new JsonArray(
                    new JsonObject { ["eq"] = new JsonArray("/historyCompletedAt", null) },
                    new JsonObject { ["ne"] = new JsonArray("/dashboardVerifiedAt", null) },
                    new JsonObject { ["ne"] = new JsonArray("/instanceId", PlatformDirectory.Instance) })
                }),
        }.ToJsonString());
        var jobs = await store.QueryAsync(AuthStore.DeletionJobTable, filter.RootElement, BatchSize,
            [new StoreSort("/nextAttemptAt")], cancellationToken).ConfigureAwait(false);
        foreach (var record in jobs)
        {
            var job = record.Body.Deserialize(DeletionJson.Default.DeletionJob);
            if (!HistoryValid(job) || job.AuthLogicalVerifiedAt is null)
            {
                continue;
            }

            if (job.HistoryCompletedAt is not null)
            {
                if (clock.GetUtcNow() >= job.FenceExpiresAt
                    && (job.InstanceId != PlatformDirectory.Instance || job.DashboardVerifiedAt is not null))
                {
                    _ = await store.RetireDeletionFenceAsync(job, record.Version, cancellationToken).ConfigureAwait(false);
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
            await store.CompareAndPutAsync(AuthStore.DeletionJobTable, job.InstanceId, job.Id,
                JsonSerializer.SerializeToElement(updated, DeletionJson.Default.DeletionJob), record.Version, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool LogicallyValid([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] DeletionJob? job) =>
        job is not null && job.Id == DeletionJob.Key(job.InstanceId, job.Scope, job.SubjectId) && job.DueAt == job.RequestedAt.AddDays(30);

    private static bool HistoryValid([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] DeletionJob? job) =>
        LogicallyValid(job) && job.FenceExpiresAt == job.RequestedAt + DeletionJob.CredentialBound && job.ErasureId is { Length: 26 } erasure && NUlid.Ulid.TryParse(erasure, out _);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping this worker is normal even while the host remains available.
        }
    }

    private async Task<string?> CleanAsync(DeletionJob job, CancellationToken cancellationToken)
    {
        var root = job.Scope switch
        {
            "organization" => AuthStore.OrganizationTable,
            "project" => AuthStore.ProjectTable,
            "account" => AuthStore.UserTable,
            _ => null,
        };
        if (root is null || job.Scope != "account" && job.InstanceId != PlatformDirectory.Instance
            || await store.GetAsync(root, job.InstanceId, job.SubjectId, cancellationToken).ConfigureAwait(false) is not null)
        {
            return "scope_not_deleted";
        }

        (string Table, string Field)[] scopes = job.Scope switch
        {
            "organization" =>
            [
                (AuthStore.ProjectTable, "/organizationId"), (AuthStore.ProjectMemberTable, "/organizationId"),
                (AuthStore.MembershipTable, "/organizationId"), (AuthStore.ApiKeyTable, "/organizationId"), (AuthStore.ApiKeyUsageTable, "/organizationId"),
                (AuthStore.InvitationTable, "/organizationId"), (AuthStore.AuditEventTable, "/organizationId"),
            ],
            "project" => [(AuthStore.ProjectMemberTable, "/projectId"), (AuthStore.ApiKeyTable, "/projectId"), (AuthStore.ApiKeyUsageTable, "/projectId"), (AuthStore.InvitationTable, "/projectId")],
            _ =>
            [
                (AuthStore.SessionTable, "/userId"), (AuthStore.PasscodeTable, "/userId"), (AuthStore.MfaTable, "/userId"),
                (AuthStore.StepUpProofTable, "/userId"), (AuthStore.StepUpDeletionGrantTable, "/userId"),
                (AuthStore.StepUpPasscodeTable, "/userId"), (AuthStore.EmailChangeTable, "/userId"),
                (AuthStore.OidcTokenTable, "/subject"), (AuthStore.OidcAuthorizationTable, "/subject"),
                (AuthStore.ProjectMemberTable, "/userId"), (AuthStore.MembershipTable, "/userId"),
                (AuthStore.ChallengeTable, "/subjectId"), (AuthStore.UniqueTable, "/ownerId"),
            ],
        };
        var remaining = RecordBudget;
        foreach (var (table, field) in scopes)
        {
            await foreach (var record in store.QueryAsync(table, job.InstanceId, field, job.SubjectId, cancellationToken).ConfigureAwait(false))
            {
                if (remaining-- <= 0)
                {
                    return "batch_remaining";
                }

                var value = record.Body;
                if (Text(value, "instanceId") != job.InstanceId || Text(value, field[1..]) != job.SubjectId)
                {
                    return "scope_mismatch";
                }

                // An organization with a surviving project needs cross-service orchestration, never a blind cascade.
                if (table == AuthStore.ProjectTable)
                {
                    return "project_handoff_required";
                }

                if (job.Scope == "account" && table == AuthStore.MembershipTable && Text(value, "role") == Roles.Owner
                    && await store.GetAsync(AuthStore.OrganizationTable, job.InstanceId, Text(value, "organizationId")!, cancellationToken).ConfigureAwait(false) is not null)
                {
                    return "organization_handoff_required";
                }

                if (table is AuthStore.UniqueTable or AuthStore.ChallengeTable)
                {
                    await store.DeleteOwnedInfrastructureAsync(table, job.InstanceId, job.SubjectId, record, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (job.Scope == "project" && table == AuthStore.ProjectMemberTable)
                {
                    if (Text(value, "organizationId") != job.OrganizationId)
                    {
                        return "scope_mismatch";
                    }

                    await directory.RetireGrantAsync(job.OrganizationId!, job.SubjectId, Text(value, "userId")!, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var key = table switch
                {
                    AuthStore.SessionTable => Text(value, "tokenHash"),
                    AuthStore.ApiKeyUsageTable => Text(value, "keyId"),
                    AuthStore.PasscodeTable or AuthStore.MfaTable or AuthStore.EmailChangeTable => Text(value, "userId"),
                    AuthStore.StepUpDeletionGrantTable => Text(value, "receiptHash"),
                    AuthStore.StepUpPasscodeTable => Text(value, "stepUpRequestId"),
                    AuthStore.MembershipTable => Text(value, "organizationId") + "/" + Text(value, "userId"),
                    AuthStore.ProjectMemberTable => Text(value, "projectId") + "/" + Text(value, "userId"),
                    _ => Text(value, "id"),
                };
                if (key is null || record.Id != AuthStore.NaturalKeyId(job.InstanceId, table, key))
                {
                    return "scope_mismatch";
                }

                if (table is AuthStore.InvitationTable or AuthStore.OidcTokenTable or AuthStore.OidcAuthorizationTable)
                {
                    await store.DeleteWithClaimsAsync(table, job.InstanceId, key, record.Version, key, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await store.CompareAndDeleteAsync(table, job.InstanceId, key, record.Version, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        // Deletion races and lost conditional writes are not success. Verify every scope again after the pass.
        foreach (var (table, field) in scopes)
        {
            if (await store.QueryAsync(table, job.InstanceId, field, job.SubjectId, cancellationToken).AnyAsync(cancellationToken).ConfigureAwait(false))
            {
                return "records_remaining";
            }
        }

        return null;
    }

    private static string? Text(JsonElement value, string field) =>
        value.TryGetProperty(field, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
}
