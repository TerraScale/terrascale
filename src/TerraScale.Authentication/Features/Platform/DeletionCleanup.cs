using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Nodes;
using TerraScale.Authentication.Hosting;
using TerraScale.Authentication.Store;
using TerraScale.DotNet.TerraBase;
using ZLogger;

namespace TerraScale.Authentication.Features.Platform;

/// <summary>
/// Restart-safe, bounded cleanup of auth logical records, then TerraBase's shared-history erasure of the auth database and
/// SYSTEM over the authority's peer transport (docs/terrabase/history-erasure.md), then fence retirement. Physical completion is
/// only ever TerraBase's status computed from every holder; this service never certifies it itself.
/// </summary>
public sealed class DeletionCleanup(
    AuthStore store, PlatformDirectory directory, TimeProvider clock, ILogger<DeletionCleanup> logger,
    [FromKeyedServices(App.StorageAuthority)] TerraBaseManagementClient terrabase) : BackgroundService
{
    private static readonly TimeSpan HistoryInterval = TimeSpan.FromMinutes(10);

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
        var jobs = await store.QueryAsync(AuthStore.DeletionJobCollection, filter.RootElement, BatchSize,
            [new TerraBaseSort("/nextAttemptAt")], cancellationToken).ConfigureAwait(false);
        foreach (var document in jobs)
        {
            var job = document.Document.Deserialize(DeletionJson.Default.DeletionJob);
            // Logical cleanup needs only the request's own identity and deadline (jobs written before the history fields too).
            if (!LogicallyValid(job))
            {
                // Never silent: its key does not match its own fields, so it cannot be rewritten safely; an operator must act.
                Outcomes.Add(1, new KeyValuePair<string, object?>("outcome", "invalid"));
                logger.ZLogError($"Auth deletion job {document.Id:@DocumentId} is malformed and cannot be processed; an operator must repair or remove it");
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
            await store.CompareAndPutAsync(AuthStore.DeletionJobCollection, job.InstanceId, job.Id,
                JsonSerializer.SerializeToElement(updated, DeletionJson.Default.DeletionJob), document.Version, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One fair batch of logically verified jobs: advances each one's shared-history erasure with TerraBase, re-running the scoped
    /// cleanup when TerraBase covered every origin's pre-fence writes; retires a fence only when TerraBase reports the erasure
    /// complete, every credential issued before it has expired, and platform dashboard cleanup was acknowledged.
    /// Deadlines are TerraBase's, from the original request time.
    /// </summary>
    public async Task RunHistoryOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        using var filter = JsonDocument.Parse(new JsonObject
        {
            ["and"] = new JsonArray(
                new JsonObject { ["lte"] = new JsonArray("/nextAttemptAt", JsonValue.Create(now)) },
                new JsonObject { ["ne"] = new JsonArray("/authLogicalVerifiedAt", null) },
                // Completed platform proofs wait durably without starving other jobs during a dashboard outage.
                new JsonObject
                {
                    ["or"] = new JsonArray(
                    new JsonObject { ["eq"] = new JsonArray("/historyCompletedAt", null) },
                    new JsonObject { ["ne"] = new JsonArray("/dashboardVerifiedAt", null) },
                    new JsonObject { ["ne"] = new JsonArray("/instanceId", PlatformDirectory.Instance) })
                }),
        }.ToJsonString());
        var jobs = await store.QueryAsync(AuthStore.DeletionJobCollection, filter.RootElement, BatchSize,
            [new TerraBaseSort("/nextAttemptAt")], cancellationToken).ConfigureAwait(false);
        foreach (var document in jobs)
        {
            var job = document.Document.Deserialize(DeletionJson.Default.DeletionJob);
            if (!HistoryValid(job) || job.AuthLogicalVerifiedAt is null)
            {
                continue;
            }

            if (job.HistoryCompletedAt is not null)
            {
                if (clock.GetUtcNow() >= job.FenceExpiresAt
                    && (job.InstanceId != PlatformDirectory.Instance || job.DashboardVerifiedAt is not null))
                {
                    _ = await store.RetireDeletionFenceAsync(job, document.Version, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            string status;
            try
            {
                status = await this.AdvanceHistoryAsync(job, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                // A wrong listener, a wrong peer credential, an outage or a stale token prove nothing: the job stays pending.
                status = error is TerraBaseConflictException { ErrorCode: "erasure_conflict" } ? "erasure_conflict" : "storage_unavailable";
            }

            var finished = clock.GetUtcNow();
            var updated = job with
            {
                NextAttemptAt = finished + (Certified(status) ? job.FenceExpiresAt!.Value - finished : HistoryInterval),
                HistoryStatus = status,
                HistoryCompletedAt = Certified(status) ? finished : null,
            };
            await store.CompareAndPutAsync(AuthStore.DeletionJobCollection, job.InstanceId, job.Id,
                JsonSerializer.SerializeToElement(updated, DeletionJson.Default.DeletionJob), document.Version, cancellationToken).ConfigureAwait(false);
        }
    }

    // The settlement token comes only from the TerraBase this store writes to, after it covered every holder's fence marker, so
    // re-running the scoped cleanup now also removes late records any origin wrote before it held the fence.
    private async Task<string> AdvanceHistoryAsync(DeletionJob job, CancellationToken cancellationToken)
    {
        await store.EnsureHistoryRequestAsync(job, cancellationToken).ConfigureAwait(false);
        var erasure = await terrabase.RequestHistoryErasureAsync(job.ErasureId!, job.RequestedAt, null, cancellationToken).ConfigureAwait(false);
        if (erasure.Status != "reverify" || erasure.SettlementToken is not { } token)
        {
            return erasure.Status;
        }

        if (await this.CleanAsync(job, cancellationToken).ConfigureAwait(false) is { } failure)
        {
            return "reverify_" + failure;
        }

        return (await terrabase.RequestHistoryErasureAsync(job.ErasureId!, job.RequestedAt, token, cancellationToken).ConfigureAwait(false)).Status;
    }

    // TerraBase certified the erasure (every holder rewrote, residue and backups closed): the fence may retire once every credential
    // issued before it expired. "complete" means another region already retired it.
    private static bool Certified(string status) => status is "retire" or "complete";

    private static bool LogicallyValid([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] DeletionJob? job) =>
        job is not null && job.Id == DeletionJob.Key(job.InstanceId, job.Scope, job.SubjectId) && job.DueAt == job.RequestedAt.AddDays(30);

    // Greenfield: a job written before the history fields never gets a history erasure (bootstrap or wipe, no migration).
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
            "organization" => AuthStore.OrganizationCollection,
            "project" => AuthStore.ProjectCollection,
            "account" => AuthStore.UserCollection,
            _ => null,
        };
        if (root is null || job.Scope != "account" && job.InstanceId != PlatformDirectory.Instance
            || await store.GetAsync(root, job.InstanceId, job.SubjectId, cancellationToken).ConfigureAwait(false) is not null)
        {
            return "scope_not_deleted";
        }

        (string Collection, string Field)[] scopes = job.Scope switch
        {
            "organization" =>
            [
                (AuthStore.ProjectCollection, "/organizationId"), (AuthStore.ProjectMemberCollection, "/organizationId"),
                (AuthStore.MembershipCollection, "/organizationId"), (AuthStore.ApiKeyCollection, "/organizationId"), (AuthStore.ApiKeyUsageCollection, "/organizationId"),
                (AuthStore.InvitationCollection, "/organizationId"), (AuthStore.AuditEventCollection, "/organizationId"),
            ],
            "project" => [(AuthStore.ProjectMemberCollection, "/projectId"), (AuthStore.ApiKeyCollection, "/projectId"), (AuthStore.ApiKeyUsageCollection, "/projectId"), (AuthStore.InvitationCollection, "/projectId")],
            _ =>
            [
                (AuthStore.SessionCollection, "/userId"), (AuthStore.PasscodeCollection, "/userId"), (AuthStore.MfaCollection, "/userId"),
                (AuthStore.StepUpProofCollection, "/userId"), (AuthStore.StepUpDeletionGrantCollection, "/userId"),
                (AuthStore.StepUpPasscodeCollection, "/userId"), (AuthStore.EmailChangeCollection, "/userId"),
                (AuthStore.OidcTokenCollection, "/subject"), (AuthStore.OidcAuthorizationCollection, "/subject"),
                (AuthStore.ProjectMemberCollection, "/userId"), (AuthStore.MembershipCollection, "/userId"),
                (AuthStore.ChallengeCollection, "/subjectId"), (AuthStore.UniqueCollection, "/ownerId"),
            ],
        };
        var remaining = RecordBudget;
        foreach (var (collection, field) in scopes)
        {
            await foreach (var record in store.QueryAsync(collection, job.InstanceId, field, job.SubjectId, cancellationToken).ConfigureAwait(false))
            {
                if (remaining-- <= 0)
                {
                    return "batch_remaining";
                }

                var value = record.Document;
                if (Text(value, "instanceId") != job.InstanceId || Text(value, field[1..]) != job.SubjectId)
                {
                    return "scope_mismatch";
                }

                // An organization with a surviving project needs cross-service orchestration, never a blind cascade.
                if (collection == AuthStore.ProjectCollection)
                {
                    return "project_handoff_required";
                }

                if (job.Scope == "account" && collection == AuthStore.MembershipCollection && Text(value, "role") == Roles.Owner
                    && await store.GetAsync(AuthStore.OrganizationCollection, job.InstanceId, Text(value, "organizationId")!, cancellationToken).ConfigureAwait(false) is not null)
                {
                    return "organization_handoff_required";
                }

                if (collection is AuthStore.UniqueCollection or AuthStore.ChallengeCollection)
                {
                    await store.DeleteOwnedInfrastructureAsync(collection, job.InstanceId, job.SubjectId, record, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (job.Scope == "project" && collection == AuthStore.ProjectMemberCollection)
                {
                    if (Text(value, "organizationId") != job.OrganizationId)
                    {
                        return "scope_mismatch";
                    }

                    await directory.RetireGrantAsync(job.OrganizationId!, job.SubjectId, Text(value, "userId")!, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var key = collection switch
                {
                    AuthStore.SessionCollection => Text(value, "tokenHash"),
                    AuthStore.ApiKeyUsageCollection => Text(value, "keyId"),
                    AuthStore.PasscodeCollection or AuthStore.MfaCollection or AuthStore.EmailChangeCollection => Text(value, "userId"),
                    AuthStore.StepUpDeletionGrantCollection => Text(value, "receiptHash"),
                    AuthStore.StepUpPasscodeCollection => Text(value, "stepUpRequestId"),
                    AuthStore.MembershipCollection => Text(value, "organizationId") + "/" + Text(value, "userId"),
                    AuthStore.ProjectMemberCollection => Text(value, "projectId") + "/" + Text(value, "userId"),
                    _ => Text(value, "id"),
                };
                if (key is null || record.Id != AuthStore.NaturalKeyId(job.InstanceId, collection, key))
                {
                    return "scope_mismatch";
                }

                if (collection is AuthStore.InvitationCollection or AuthStore.OidcTokenCollection or AuthStore.OidcAuthorizationCollection)
                {
                    await store.DeleteWithClaimsAsync(collection, job.InstanceId, key, record.Version, key, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await store.CompareAndDeleteAsync(collection, job.InstanceId, key, record.Version, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        // Deletion races and lost conditional writes are not success. Verify every scope again after the pass.
        foreach (var (collection, field) in scopes)
        {
            if (await store.QueryAsync(collection, job.InstanceId, field, job.SubjectId, cancellationToken).AnyAsync(cancellationToken).ConfigureAwait(false))
            {
                return "records_remaining";
            }
        }

        return null;
    }

    private static string? Text(JsonElement value, string field) =>
        value.TryGetProperty(field, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
}
