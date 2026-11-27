using System.Threading.Channels;
using Dapper;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Storage;
using TerraScale.DotNet.TerraBase;
using ZLogger;

namespace TerraScale.Authentication.Features.Platform;

public sealed class Closures(
    AuthData data, PlatformDirectory directory, Users users, [FromKeyedServices(App.StorageAuthority)] TerraBaseManagementClient terraBase, TimeProvider clock, ILogger<Closures> logger)
{
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan ParkedRetry = TimeSpan.FromMinutes(15);

    internal const int ParkAfter = 65;

    private readonly Channel<bool> _deferrals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public async Task<bool> DeleteProjectAsync(string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
        await this.AdvanceAsync(await directory.RequestProjectClosureAsync(userId, organizationId, projectId, null, cancellationToken), cancellationToken);

    internal async Task<bool> AdvanceAsync(Versioned<ClosureJob> job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        TerraBaseProjectClosure? answer;
        try
        {
            answer = job.Value.ProjectClosureVersion == 2
                ? await this.EraseOrCloseAsync(job.Value, cancellationToken)
                : await terraBase.CloseProjectAsync(job.Value.SubjectId, job.Value.OrganizationId!, job.Value.Id, cancellationToken);
        }
        catch (TerraBaseConflictException error) when (error.ErrorCode is "not_empty" or "closure_conflict" or "binding_conflict" or "platform_project_protected")
        {
            var refused = error.ErrorCode switch
            {
                "not_empty" => PlatformDirectory.Conflict("not_empty", "Delete the project's databases, TerraBase API keys and backup destination first."),
                "platform_project_protected" => PlatformDirectory.PlatformProjectProtected(),
                _ => PlatformDirectory.Conflict(error.ErrorCode, "TerraBase holds this project id under another closure or organization."),
            };
            return await this.CancelAsync(job, cancellationToken) switch
            {
                Cancellation.Cancelled => throw refused,
                Cancellation.Gone => await directory.ProjectAsync(job.Value.SubjectId, cancellationToken) is null ? true : throw refused,
                _ => false,
            };
        }
        catch (Exception error) when (error is TerraBaseException or HttpRequestException or TimeoutException or ApiException { Status: StatusCodes.Status503ServiceUnavailable }
            && !cancellationToken.IsCancellationRequested)
        {
            await this.DeferAsync(job, error is TerraBaseException { ErrorCode: { } code } && code.Length <= 64 && code.All(character => char.IsAsciiLetterLower(character) || character == '_') ? code : "terrabase_unavailable",
                cancellationToken);
            return false;
        }

        if (answer is null)
        {
            await this.DeferAsync(job, "terrabase_erasure_unsupported", cancellationToken);
            return false;
        }

        if (await this.MarkFenceSeenAsync(job, cancellationToken) is not { } seen)
        {
            return await this.WithoutIntentAsync(job, cancellationToken);
        }

        if (!answer.Accepted || answer.EvidenceVersion < job.Value.ProjectClosureVersion)
        {
            await this.DeferAsync(seen, answer.Accepted ? "terrabase_evidence_pending" : "terrabase_pending", cancellationToken);
            return false;
        }

        return await directory.RemoveClosedProjectAsync(seen, cancellationToken)
            || await this.WithoutIntentAsync(job, cancellationToken);
    }

    private async Task<TerraBaseProjectClosure?> EraseOrCloseAsync(ClosureJob job, CancellationToken cancellationToken)
    {
        try
        {
            return await terraBase.EraseProjectAsync(job.SubjectId, job.OrganizationId!, job.Id, cancellationToken);
        }
        catch (TerraBaseException error) when (error.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed or System.Net.HttpStatusCode.NotImplemented
            || error is TerraBaseValidationException && error.ErrorCode is "invalid_request" or "unsupported" or "unsupported_version")
        {
            try
            {
                var legacy = await terraBase.CloseProjectAsync(job.SubjectId, job.OrganizationId!, job.Id, cancellationToken);
                return legacy with { EvidenceVersion = 0 };
            }
            catch (TerraBaseConflictException refused) when (refused.ErrorCode is "not_empty" or "closure_conflict")
            {
                return null;
            }
        }
    }

    private async Task<bool> WithoutIntentAsync(Versioned<ClosureJob> job, CancellationToken cancellationToken) =>
        await directory.ProjectAsync(job.Value.SubjectId, cancellationToken) is null
        || (await directory.ClosureAsync(job.Value.Scope, job.Value.SubjectId, cancellationToken) is not null
            ? false
            : throw ApiException.Unavailable());

    private enum Cancellation
    {
        Cancelled,
        Kept,
        Gone,
    }

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
            var marked = Change.Update(current, seen);
            if (await data.SaveAsync(marked, cancellationToken))
            {
                return new(seen, marked.Version!);
            }

            if (await this.RereadAsync(job, cancellationToken) is not { } reread)
            {
                return null;
            }

            current = reread;
        }

        throw ApiException.Unavailable();
    }

    private async Task<Cancellation> CancelAsync(Versioned<ClosureJob> job, CancellationToken cancellationToken)
    {
        var current = job;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (current.Value.FenceSeen)
            {
                await this.DeferAsync(current, "terrabase_refused_after_fence", cancellationToken);
                return Cancellation.Kept;
            }

            if (await directory.CancelClosureAsync(current, cancellationToken))
            {
                return Cancellation.Cancelled;
            }

            if (await this.RereadAsync(job, cancellationToken) is not { } reread)
            {
                return Cancellation.Gone;
            }

            current = reread;
        }

        throw ApiException.Unavailable();
    }

    private async Task<Versioned<ClosureJob>?> RereadAsync(Versioned<ClosureJob> job, CancellationToken cancellationToken) =>
        await directory.ClosureAsync(job.Value.Scope, job.Value.SubjectId, cancellationToken) is { } reread && reread.Value.Same(job.Value) ? reread : null;

    internal async Task<Versioned<ClosureJob>?> DetachAsync(Versioned<ClosureJob> job, CancellationToken cancellationToken)
    {
        var account = job.Value.AccountId ?? throw new ArgumentException("Only an account's intent detaches.", nameof(job));
        var current = job;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var detached = current.Value with { AccountId = null, NextAttemptAt = clock.GetUtcNow() };
            if (await directory.ClosureAsync("account", account, cancellationToken) is not null)
            {
                return null;
            }

            List<Change> changes = [Change.Update(current, detached)];
            if (await users.GetAsync(PlatformDirectory.Instance, account, cancellationToken) is { } owner)
            {
                changes.Add(Change.Assert(owner));
            }

            if (await data.SaveAsync(changes, cancellationToken))
            {
                return await this.RereadAsync(job, cancellationToken);
            }

            if (await this.RereadAsync(job, cancellationToken) is not { } reread || reread.Value.AccountId != account)
            {
                return null;
            }

            current = reread;
        }

        throw ApiException.Unavailable();
    }

    internal async Task<IReadOnlyList<ClosureJob>> EarliestAsync(int limit, CancellationToken cancellationToken) =>
        (await data.ReadAsync(connection => connection.QueryAsync<ClosureJob>(
            "SELECT * FROM closure_jobs ORDER BY next_attempt_at LIMIT @limit", new { limit }), cancellationToken)).AsList();

    internal void ClearDeferrals() => _ = _deferrals.Reader.TryRead(out _);

    internal async Task WaitForDeferralAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        using var rest = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await Task.WhenAny(Task.Delay(delay, clock, rest.Token), _deferrals.Reader.WaitToReadAsync(rest.Token).AsTask());
        await rest.CancelAsync();
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal static TimeSpan Backoff(int attempts) => attempts + 1 >= ParkAfter ? ParkedRetry
        : attempts >= 5 ? MaxRetry : TimeSpan.FromTicks(Math.Min(Retry.Ticks << attempts, MaxRetry.Ticks));

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
        if (!await data.SaveAsync(Change.Update(job, next), cancellationToken))
        {
            logger.ZLogInformation($"A closure intent changed while it was deferred; the next pass reads it again");
        }
        else if (attempts + 1 == ParkAfter)
        {
            logger.ZLogWarning($"A {job.Value.Scope:@Scope} closure intent is parked after {ParkAfter} attempts ({failure:@FailureCode}); it now retries every 15 minutes and needs attention");
        }

        _ = _deferrals.Writer.TryWrite(true);
    }
}

public sealed class ClosureWorker(AuthSchema schema, Closures closures, PlatformDirectory directory, AccountDeletion accounts, TimeProvider clock, ILogger<ClosureWorker> logger) : BackgroundService
{
    private const int BatchSize = 20;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(30);

    public async Task<ClosurePass> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var earliest = await closures.EarliestAsync(BatchSize, cancellationToken);
        ClosureJob[] pending = [.. earliest.Where(job => job.NextAttemptAt <= now)];
        foreach (var due in pending)
        {
            try
            {
                if (await directory.ClosureAsync(due.Scope, due.SubjectId, cancellationToken) is not { } job || !job.Value.Same(due)
                    || job.Value.NextAttemptAt > clock.GetUtcNow())
                {
                    continue;
                }

                if (job.Value.AccountId is { } account)
                {
                    if (await directory.ClosureAsync("account", account, cancellationToken) is not null)
                    {
                        await closures.DeferAsync(job, "account_driven", cancellationToken);
                        continue;
                    }

                    if (job.Value.Scope != "project")
                    {
                        _ = await directory.CancelClosureAsync(job, cancellationToken, unlessAccount: account);
                        continue;
                    }

                    if (await closures.DetachAsync(job, cancellationToken) is not { } detached)
                    {
                        continue;
                    }

                    job = detached;
                }

                _ = job.Value.Scope == "account"
                    ? await accounts.AdvanceAsync(job, cancellationToken)
                    : await closures.AdvanceAsync(job, cancellationToken);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    logger.ZLogWarning(error, $"Resuming a {due.Scope:@Scope} closure failed; it stays pending");
                }
                catch (Exception logFailure) when (logFailure is not OutOfMemoryException)
                {
                    _ = logFailure;
                }
            }
        }

        return new ClosurePass(
            pending.Length > 0,
            earliest.FirstOrDefault(job => job.NextAttemptAt > now) is { } next ? next.NextAttemptAt - now : null);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await schema.Ready.WaitAsync(stoppingToken);
        var backoff = Interval;
        try
        {
            while (true)
            {
                closures.ClearDeferrals();
                var pass = new ClosurePass(true, null);
                try
                {
                    pass = await RunOnceAsync(stoppingToken);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }

                    try
                    {
                        logger.ZLogWarning(error, $"The closure retry pass failed; retrying at the next interval");
                    }
                    catch (Exception logFailure) when (logFailure is not OutOfMemoryException)
                    {
                        _ = logFailure;
                    }
                }

                backoff = pass.Advanced ? Interval : TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, IdleInterval.Ticks));
                var wait = pass.NextIn is { } nextIn && nextIn < backoff ? nextIn : backoff;
                await (pass.Advanced ? Task.Delay(wait, clock, stoppingToken) : closures.WaitForDeferralAsync(wait, stoppingToken));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}

public sealed record ClosurePass(bool Advanced, TimeSpan? NextIn);
