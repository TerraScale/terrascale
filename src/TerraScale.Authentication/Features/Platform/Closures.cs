using System.Text.Json;
using System.Text.Json.Nodes;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Store;
using TerraScale.DotNet.TerraBase;
using ZLogger;

namespace TerraScale.Authentication.Features.Platform;

/// <summary>
/// Project closure (docs/terrabase/project-closure.md): an authorized intent first, then TerraBase's permanent fence over
/// the existing peer transport, and the project root only after TerraBase accepted it in every catalog holder. A refusal
/// leaves the project as it was; anything else (pending holders, an outage, a lost response, a 404 from a wrong listener)
/// keeps the intent for the background pass, which retries with the same closure id.
/// </summary>
public sealed class Closures(
    AuthStore store, PlatformDirectory directory, [FromKeyedServices(App.StorageAuthority)] TerraBaseManagementClient terraBase, TimeProvider clock, ILogger<Closures> logger)
{
    // Each deferral is a remote-durable write; a fixed short retry rewrote every pending intent every pass, forever.
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Closes a project for <paramref name="userId"/>, who needs its admin role: true once it is removed, false while
    /// TerraBase has not accepted the closure (it finishes in the background). 409 <c>not_empty</c> while TerraBase holds its
    /// databases, API keys or backup destination, changing nothing.
    /// </summary>
    public async Task<bool> DeleteProjectAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
        await this.AdvanceAsync(await directory.RequestProjectClosureAsync(userId, organizationId, projectId, null, cancellationToken).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// One step of a project closure: true when the root is removed, false while pending. A refusal cancels it and throws 409
    /// only while no TerraBase answer ever showed its fence; afterwards it stays pending.
    /// </summary>
    internal async Task<bool> AdvanceAsync(Versioned<ClosureJob> job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        TerraBaseProjectClosure? answer;
        try
        {
            answer = job.Value.ProjectClosureVersion == 2
                ? await this.EraseOrCloseAsync(job.Value, cancellationToken).ConfigureAwait(false)
                : await terraBase.CloseProjectAsync(job.Value.SubjectId, job.Value.OrganizationId!, job.Value.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (TerraBaseConflictException error) when (error.ErrorCode is "not_empty" or "closure_conflict" or "binding_conflict")
        {
            // TerraBase wrote nothing for this request. Once an earlier answer showed the fence, a machine that has not received
            // it yet can refuse, so the refusal no longer proves the fence was never written: stay pending.
            var refused = error.ErrorCode == "not_empty"
                ? PlatformDirectory.Conflict("not_empty", "Delete the project's databases, TerraBase API keys and backup destination first.")
                : PlatformDirectory.Conflict(error.ErrorCode, "TerraBase holds this project id under another closure or organization.");
            return await this.CancelAsync(job, cancellationToken).ConfigureAwait(false) switch
            {
                Cancellation.Cancelled => throw refused,
                // This intent is gone: done if its root went (another pass completed it), otherwise refused (another pass cancelled it).
                Cancellation.Gone => await directory.ProjectAsync(job.Value.SubjectId, cancellationToken).ConfigureAwait(false) is null ? true : throw refused,
                _ => false,
            };
        }
        catch (Exception error) when (error is TerraBaseException or HttpRequestException or TimeoutException or ApiException { Status: StatusCodes.Status503ServiceUnavailable }
            && !cancellationToken.IsCancellationRequested)
        {
            // No answer is evidence of absence or acceptance; a 404 can be a wrong listener or mapping.
            await this.DeferAsync(job, error is TerraBaseException { ErrorCode: { } code } && code.Length <= 64 && code.All(character => char.IsAsciiLetterLower(character) || character == '_') ? code : "terrabase_unavailable",
                cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (answer is null)
        {
            // The older contract refused contents or could not read a newer fence. Neither disproves an existing fence.
            await this.DeferAsync(job, "terrabase_erasure_unsupported", cancellationToken).ConfigureAwait(false);
            return false;
        }

        // TerraBase showed the fence: record it before anything else, so no later refusal can cancel this intent.
        if (await this.MarkFenceSeenAsync(job, cancellationToken).ConfigureAwait(false) is not { } seen)
        {
            return await this.WithoutIntentAsync(job, cancellationToken).ConfigureAwait(false);
        }

        if (!answer.Accepted || answer.EvidenceVersion != 2)
        {
            // Older engines can accept catalog emptiness while active purges remain. Retain the driver until upgraded.
            await this.DeferAsync(seen, answer.Accepted ? "terrabase_evidence_pending" : "terrabase_pending", cancellationToken).ConfigureAwait(false);
            return false;
        }

        return await directory.RemoveClosedProjectAsync(seen, cancellationToken).ConfigureAwait(false)
            || await this.WithoutIntentAsync(job, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TerraBaseProjectClosure?> EraseOrCloseAsync(ClosureJob job, CancellationToken cancellationToken)
    {
        try
        {
            return await terraBase.EraseProjectAsync(job.SubjectId, job.OrganizationId!, job.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (TerraBaseException error) when (error.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed or System.Net.HttpStatusCode.NotImplemented
            || error is TerraBaseValidationException && error.ErrorCode is "invalid_request" or "unsupported" or "unsupported_version")
        {
            try
            {
                // Same authority and closure id, using today's empty-only path. It never authorizes destructive completion.
                var legacy = await terraBase.CloseProjectAsync(job.SubjectId, job.OrganizationId!, job.Id, cancellationToken).ConfigureAwait(false);
                return legacy with { EvidenceVersion = 0 };
            }
            catch (TerraBaseConflictException refused) when (refused.ErrorCode is "not_empty" or "closure_conflict")
            {
                // An earlier v2 fence may exist even when its response was lost before auth recorded FenceSeen.
                return null;
            }
        }
    }

    // This intent vanished (a concurrent refusal cancelled it) after TerraBase showed the fence: done when the root is gone,
    // pending while a re-created intent carries it on, and otherwise nothing is in progress: 503, so the caller retries
    // and the next intent presents the project's stored closure id, which TerraBase recognizes.
    private async Task<bool> WithoutIntentAsync(Versioned<ClosureJob> job, CancellationToken cancellationToken) =>
        await directory.ProjectAsync(job.Value.SubjectId, cancellationToken).ConfigureAwait(false) is null
        || (await directory.ClosureAsync(job.Value.Scope, job.Value.SubjectId, cancellationToken).ConfigureAwait(false) is not null
            ? false
            : throw ApiException.Unavailable());

    private enum Cancellation
    {
        Cancelled,
        Kept,
        Gone,
    }

    // Records fenceSeen at the version read, read again after a lost race. Null when this intent is gone.
    private async Task<Versioned<ClosureJob>?> MarkFenceSeenAsync(Versioned<ClosureJob> job, CancellationToken cancellationToken)
    {
        var current = job;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (current.Value.FenceSeen)
            {
                return current;
            }

            var seen = current.Value with { FenceSeen = true };
            if (await store.ReplaceAsync(AuthStore.ClosureJobCollection, current.Value.InstanceId, ClosureJob.Key(current.Value.Scope, current.Value.SubjectId),
                JsonSerializer.SerializeToElement(seen, ClosureJson.Default.ClosureJob), current.Version, cancellationToken).ConfigureAwait(false) is { } version)
            {
                return new(seen, version);
            }

            if (await this.RereadAsync(job, cancellationToken).ConfigureAwait(false) is not { } reread)
            {
                return null;
            }

            current = reread;
        }

        throw ApiException.Unavailable();
    }

    // Cancels a refused intent at the version read, read again after a lost race (a pass deferred it). Kept when it showed
    // its fence meanwhile; Gone when this intent no longer exists (completed, or cancelled by another pass).
    private async Task<Cancellation> CancelAsync(Versioned<ClosureJob> job, CancellationToken cancellationToken)
    {
        var current = job;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (current.Value.FenceSeen)
            {
                await this.DeferAsync(current, "terrabase_refused_after_fence", cancellationToken).ConfigureAwait(false);
                return Cancellation.Kept;
            }

            if (await directory.CancelClosureAsync(current, cancellationToken).ConfigureAwait(false))
            {
                return Cancellation.Cancelled;
            }

            if (await this.RereadAsync(job, cancellationToken).ConfigureAwait(false) is not { } reread)
            {
                return Cancellation.Gone;
            }

            current = reread;
        }

        throw ApiException.Unavailable();
    }

    // The same intent (same creation) as stored now, or null.
    private async Task<Versioned<ClosureJob>?> RereadAsync(Versioned<ClosureJob> job, CancellationToken cancellationToken) =>
        await directory.ClosureAsync(job.Value.Scope, job.Value.SubjectId, cancellationToken).ConfigureAwait(false) is { } reread && reread.Value.Same(job.Value) ? reread : null;

    /// <summary>
    /// Turns an account's project intent into a standalone one (at the version read, re-read after a lost race), due now, so
    /// the background pass finishes it with its original authority. Null when it is gone, no longer the account's, or the
    /// account has a closure intent again (that account closure drives it).
    /// </summary>
    internal async Task<Versioned<ClosureJob>?> DetachAsync(Versioned<ClosureJob> job, CancellationToken cancellationToken)
    {
        var account = job.Value.AccountId ?? throw new ArgumentException("Only an account's intent detaches.", nameof(job));
        var current = job;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var detached = current.Value with { AccountId = null, NextAttemptAt = clock.GetUtcNow() };
            // Only while the account has no closure intent: a newer account closure drives its own intents.
            switch (await store.TransactAsync(
            [
                AuthStore.Replace(AuthStore.ClosureJobCollection, current.Value.InstanceId, ClosureJob.Key(current.Value.Scope, current.Value.SubjectId),
                    JsonSerializer.SerializeToElement(detached, ClosureJson.Default.ClosureJob), current.Version),
                AuthStore.Absent(AuthStore.ClosureJobCollection, current.Value.InstanceId, ClosureJob.Key("account", account)),
            ], cancellationToken).ConfigureAwait(false))
            {
                case null:
                    return await this.RereadAsync(job, cancellationToken).ConfigureAwait(false);
                case 1:
                    return null;
            }

            if (await this.RereadAsync(job, cancellationToken).ConfigureAwait(false) is not { } reread || reread.Value.AccountId != account)
            {
                return null;
            }

            current = reread;
        }

        throw ApiException.Unavailable();
    }

    /// <summary>Due closure intents of every scope, oldest first; account-owned ones too, so none can be stranded.</summary>
    internal async Task<IReadOnlyList<ClosureJob>> DueAsync(int limit, CancellationToken cancellationToken)
    {
        using var filter = JsonDocument.Parse(new JsonObject
        {
            ["lte"] = new JsonArray("/nextAttemptAt", JsonValue.Create(clock.GetUtcNow())),
        }.ToJsonString());
        return [.. (await store.QueryAsync(AuthStore.ClosureJobCollection, filter.RootElement, limit, [new TerraBaseSort("/nextAttemptAt")], cancellationToken).ConfigureAwait(false))
            .Select(document => document.Document.Deserialize(ClosureJson.Default.ClosureJob)).OfType<ClosureJob>()];
    }

    // 2 s, doubling per earlier attempt, at most a minute.
    internal static TimeSpan Backoff(int attempts) => attempts >= 5 ? MaxRetry : TimeSpan.FromTicks(Math.Min(Retry.Ticks << attempts, MaxRetry.Ticks));

    // Moves a pending intent behind the others; a lost race means another pass already did.
    internal async Task DeferAsync(Versioned<ClosureJob> job, string failure, CancellationToken cancellationToken, bool fenceSeen = false)
    {
        var attempts = Math.Clamp(job.Value.Attempts, 0, 999_999);
        var next = job.Value with
        {
            Attempts = attempts + 1,
            NextAttemptAt = clock.GetUtcNow() + Backoff(attempts),
            FailureCode = failure,
            FenceSeen = job.Value.FenceSeen || fenceSeen,
        };
        if (!await store.CompareAndPutAsync(AuthStore.ClosureJobCollection, job.Value.InstanceId, ClosureJob.Key(job.Value.Scope, job.Value.SubjectId),
            JsonSerializer.SerializeToElement(next, ClosureJson.Default.ClosureJob), job.Version, cancellationToken).ConfigureAwait(false))
        {
            logger.ZLogInformation($"A closure intent changed while it was deferred; the next pass reads it again");
        }
    }
}

/// <summary>Resumes pending closures at startup and every two seconds, so a crash or restart never strands an intent.</summary>
public sealed class ClosureWorker(Closures closures, PlatformDirectory directory, AccountDeletion accounts, TimeProvider clock, ILogger<ClosureWorker> logger) : BackgroundService
{
    private const int BatchSize = 20;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    /// <summary>Advances one batch of due closures; each failure is logged without detail and retried later.</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        foreach (var due in await closures.DueAsync(BatchSize, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                if (await directory.ClosureAsync(due.Scope, due.SubjectId, cancellationToken).ConfigureAwait(false) is not { } job || !job.Value.Same(due)
                    || job.Value.NextAttemptAt > clock.GetUtcNow())
                {
                    // Another pass, including an earlier account in this batch, may have rescheduled this snapshot entry.
                    continue;
                }

                if (job.Value.AccountId is { } account)
                {
                    // An account's own intents are its driver's while its account intent exists; once that is gone (it was
                    // cancelled while a concurrent step still created them), a project closure goes on alone and an
                    // organization fence goes.
                    if (await directory.ClosureAsync("account", account, cancellationToken).ConfigureAwait(false) is not null)
                    {
                        await closures.DeferAsync(job, "account_driven", cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (job.Value.Scope != "project")
                    {
                        _ = await directory.CancelClosureAsync(job, cancellationToken, unlessAccount: account).ConfigureAwait(false);
                        continue;
                    }

                    if (await closures.DetachAsync(job, cancellationToken).ConfigureAwait(false) is not { } detached)
                    {
                        continue;
                    }

                    job = detached;
                }

                _ = job.Value.Scope == "account"
                    ? await accounts.AdvanceAsync(job, cancellationToken).ConfigureAwait(false)
                    : await closures.AdvanceAsync(job, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Exceptions may carry identifiers or storage detail: log the fact only.
                try
                {
                    logger.ZLogWarning($"Resuming a {due.Scope:@Scope} closure failed; it stays pending");
                }
                catch (Exception logFailure) when (logFailure is not OutOfMemoryException)
                {
                    // A failed log provider must not stop this batch or its durable retries.
                    _ = logFailure;
                }
            }
        }
    }

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
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    // Storage may report an ordinary failure after the host has cancelled this pass.
                    // Its durable intent remains for the next process to resume.
                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }

                    try
                    {
                        logger.ZLogWarning($"The closure retry pass failed; retrying at the next interval");
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
}
