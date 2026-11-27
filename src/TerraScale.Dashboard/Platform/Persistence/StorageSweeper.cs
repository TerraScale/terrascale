using System.Globalization;
using Dapper;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Mcp;

namespace TerraScale.Dashboard.Platform.Persistence;

public sealed class StorageSweeper(DashboardDb storage, DashboardDbUpRunner initializer, DashboardSessions sessions, TimeProvider time,
    ILogger<StorageSweeper> logger, BackgroundJobs jobs, BillingSchemaGate gate) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    private const int BatchSize = 500;
    private const int AuthFlowStage = 0;
    private const int CredentialFirstStage = 1;
    private const int McpFirstStage = CredentialFirstStage + 3;
    private const int BrowserStage = McpFirstStage + McpRegistrations.ReclaimStepCount;
    private const int StageCount = BrowserStage + 1;

    private static readonly TimeSpan DrainBudget = BackgroundJobs.Lease / 2;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinRetryDelay = TimeSpan.FromSeconds(1);

    private const int RowBatch = 256;
    private const int ByteBudget = 2 * 1024 * 1024;
    private const string ExpiredCredential = "SELECT s.id FROM sessions s WHERE s.kind = @Credential AND s.expires_at <= @Now";
    private const string FlowBytes = "octet_length(id) + octet_length(ciphertext) + octet_length(created_at) + octet_length(expires_at) "
        + "+ octet_length(nonce) + octet_length(purpose) + octet_length(tag) + octet_length(token_hash)";
    internal const string AccessTokenBytes = "octet_length(id) + octet_length(created_at) + octet_length(credential_id) + octet_length(expires_at) "
        + "+ octet_length(last_four) + coalesce(octet_length(mcp_client_id), 0) + octet_length(name) + coalesce(octet_length(revoked_at), 0) "
        + "+ octet_length(secret_hash) + octet_length(user_id)";
    internal const string McpCodeBytes = "octet_length(id) + octet_length(client_id) + octet_length(client_name) + octet_length(code_challenge) "
        + "+ octet_length(code_hash) + octet_length(credential_id) + octet_length(expires_at) + octet_length(redirect_uri) "
        + "+ octet_length(resource) + octet_length(token_expires_at) + octet_length(user_id)";
    private const string SessionBytes = "octet_length(s.id) + octet_length(s.ciphertext) + octet_length(s.device) + octet_length(s.expires_at) "
        + "+ octet_length(s.kind) + octet_length(s.nonce) + octet_length(s.tag) + coalesce(octet_length(s.token_hash), 0) + octet_length(s.user_id) "
        + "+ octet_length(s.version)";

    private static readonly string AuthFlowStep = "DELETE FROM auth_flows WHERE id IN (" + Budgeted(
        "SELECT id, " + FlowBytes + " AS size FROM auth_flows WHERE expires_at <= @Now") + ")";

    private static readonly string[] CredentialSteps =
    [
        "DELETE FROM access_tokens WHERE id IN (" + Budgeted(
            $"SELECT id, {AccessTokenBytes} AS size FROM access_tokens WHERE credential_id IN ({ExpiredCredential})") + ")",
        "DELETE FROM mcp_codes WHERE id IN (" + Budgeted(
            $"SELECT id, {McpCodeBytes} AS size FROM mcp_codes WHERE expires_at <= @Now OR credential_id IN ({ExpiredCredential})") + ")",
        "DELETE FROM sessions WHERE id IN (" + Budgeted(
            "SELECT s.id, " + SessionBytes + " AS size FROM sessions s WHERE s.kind = @Credential AND s.expires_at <= @Now "
            + "AND NOT EXISTS (SELECT 1 FROM access_tokens a WHERE a.credential_id = s.id) "
            + "AND NOT EXISTS (SELECT 1 FROM mcp_codes c WHERE c.credential_id = s.id)") + ")",
    ];

    private const string SweepDue = """
        SELECT EXISTS (SELECT 1 FROM auth_flows WHERE expires_at <= @Now)
            OR EXISTS (SELECT 1 FROM sessions WHERE kind IN (@Credential, @Browser) AND expires_at <= @Now)
            OR EXISTS (SELECT 1 FROM mcp_codes WHERE expires_at <= @Now)
            OR EXISTS (SELECT 1 FROM mcp_clients WHERE first_sign_in_completed_at IS NULL AND closed_at IS NULL AND created_at <= @Unfinished)
        """;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await initializer.WhenInitialized.WaitAsync(stoppingToken);
            (string? Cursor, int NextStage)? failedRotation = null;
            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = Interval;
                try
                {
                    if (await gate.AllowsAsync(stoppingToken))
                    {
                        await jobs.RunAsync("storage-sweep", Interval, async (claim, token) =>
                        {
                            var now = time.GetUtcNow().UtcDateTime;
                            var started = time.GetTimestamp();
                            var originalCursor = claim.State.Cursor;
                            var prior = ReadCursor(originalCursor);
                            if (failedRotation is { } fallback && originalCursor == fallback.Cursor)
                            {
                                prior = prior with { NextStage = fallback.NextStage };
                            }

                            failedRotation = null;
                            var browserAfterId = prior.BrowserAfterId;
                            var browserPriorFailures = prior.BrowserHadRetryableFailures;
                            var browserRetryAfterTicks = prior.BrowserRetryAfterTicks;
                            if (browserRetryAfterTicks > 0 && now.Ticks >= browserRetryAfterTicks)
                            {
                                browserRetryAfterTicks = 0;
                            }

                            var browserBackedOff = browserRetryAfterTicks > now.Ticks;
                            var remainingStages = browserBackedOff ? StageCount - 1 : StageCount;
                            int? firstSkippedStage = null;
                            var anyUnfinished = false;
                            var activeStage = prior.NextStage;
                            var stagesFinished = false;

                            try
                            {
                                for (var offset = 0; offset < StageCount; offset++)
                                {
                                    var stage = (prior.NextStage + offset) % StageCount;
                                    if (stage == BrowserStage && browserBackedOff)
                                    {
                                        continue;
                                    }

                                    var remaining = DrainBudget - time.GetElapsedTime(started);
                                    if (remaining <= TimeSpan.Zero)
                                    {
                                        firstSkippedStage = stage;
                                        break;
                                    }

                                    activeStage = stage;
                                    var slice = remaining / remainingStages;
                                    remainingStages--;

                                    if (stage == AuthFlowStage)
                                    {
                                        await using var connection = await storage.OpenAsync(token);
                                        if (!await DrainAsync(time, time.GetTimestamp(), slice,
                                            () => connection.ExecuteAsync(AuthFlowStep, new { Now = now }), token))
                                        {
                                            anyUnfinished = true;
                                        }
                                    }
                                    else if (stage < McpFirstStage)
                                    {
                                        var step = CredentialSteps[stage - CredentialFirstStage];
                                        await using var connection = await storage.OpenAsync(token);
                                        if (!await DrainAsync(time, time.GetTimestamp(), slice,
                                            () => connection.ExecuteAsync(step, new { Now = now, SessionRow.Credential }), token))
                                        {
                                            anyUnfinished = true;
                                        }
                                    }
                                    else if (stage < BrowserStage)
                                    {
                                        if (!await McpRegistrations.ReclaimUnfinishedStepAsync(storage, time, time.GetTimestamp(), slice,
                                            stage - McpFirstStage, now, token))
                                        {
                                            anyUnfinished = true;
                                        }
                                    }
                                    else
                                    {
                                        var browserOutcome = await sessions.ExpireBrowserSessionsAsync(BatchSize, time.GetTimestamp(), slice, browserAfterId, token);
                                        var (browserDrained, nextAfterId, nextPriorFailures, scheduleRetry, retryAfter) = browserOutcome switch
                                        {
                                            BrowserSessionsDrained => (true, "", false, browserPriorFailures, (TimeSpan?)null),
                                            BrowserSessionsBudgetExhausted exhausted => (false, exhausted.AfterId, browserPriorFailures || exhausted.RetryableFailures, false, null),
                                            BrowserSessionsRateLimited limited => (true, limited.AfterId, browserPriorFailures || limited.RetryableFailures, true, limited.RetryAfter),
                                            BrowserSessionsClientRejected rejected => (true, rejected.AfterId, browserPriorFailures || rejected.RetryableFailures, true, null),
                                            BrowserSessionsRetryableFailures => (true, "", false, true, null),
                                        };
                                        browserAfterId = nextAfterId;
                                        browserPriorFailures = nextPriorFailures;
                                        if (scheduleRetry)
                                        {
                                            browserRetryAfterTicks = (time.GetUtcNow().UtcDateTime + BrowserBackoff(retryAfter)).Ticks;
                                        }
                                        else if (browserDrained)
                                        {
                                            browserRetryAfterTicks = 0;
                                        }

                                        if (!browserDrained)
                                        {
                                            anyUnfinished = true;
                                        }
                                    }
                                }

                                stagesFinished = true;
                            }
                            finally
                            {
                                if (!stagesFinished)
                                {
                                    var failedNextStage = (activeStage + 1) % StageCount;
                                    failedRotation = (originalCursor, failedNextStage);
                                    claim.Delay = RetryDelay;
                                    delay = RetryDelay;
                                    if (!token.IsCancellationRequested)
                                    {
                                        var failedCursor = FormatCursor(new SweepCursor(failedNextStage, browserAfterId, browserPriorFailures, browserRetryAfterTicks));
                                        if (claim.State.Cursor != failedCursor)
                                        {
                                            claim.State.Cursor = failedCursor;
                                            await claim.SaveAsync(token);
                                        }

                                        failedRotation = null;
                                    }
                                }
                            }

                            var hasWorkLeft = firstSkippedStage is not null || anyUnfinished;
                            var nextStage = firstSkippedStage is { } skipped
                                ? skipped
                                : anyUnfinished
                                    ? (prior.NextStage + 1) % StageCount
                                    : 0;

                            if (hasWorkLeft)
                            {
                                claim.Delay = TimeSpan.Zero;
                                delay = TimeSpan.Zero;
                            }
                            else if (browserRetryAfterTicks > 0)
                            {
                                var afterSweepNow = time.GetUtcNow().UtcDateTime;
                                var wait = TimeSpan.FromTicks(browserRetryAfterTicks - afterSweepNow.Ticks);
                                if (wait <= TimeSpan.Zero)
                                {
                                    browserRetryAfterTicks = 0;
                                    nextStage = BrowserStage;
                                    claim.Delay = TimeSpan.Zero;
                                    delay = TimeSpan.Zero;
                                }
                                else
                                {
                                    if (wait < MinRetryDelay)
                                    {
                                        wait = MinRetryDelay;
                                    }

                                    nextStage = BrowserStage;
                                    claim.Delay = wait;
                                    delay = wait;
                                }
                            }

                            var nextCursor = FormatCursor(new SweepCursor(nextStage, browserAfterId, browserPriorFailures, browserRetryAfterTicks));
                            if (claim.State.Cursor != nextCursor)
                            {
                                failedRotation = (originalCursor, nextStage);
                                claim.State.Cursor = nextCursor;
                                await claim.SaveAsync(token);
                                failedRotation = null;
                            }
                        }, stoppingToken, due: DueAsync);
                    }
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    delay = RetryDelay;
                    logger.ExpiredRowsWereNotSwept(error.GetType().Name);
                }

                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, time, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private readonly record struct SweepCursor(int NextStage, string BrowserAfterId, bool BrowserHadRetryableFailures, long BrowserRetryAfterTicks)
    {
        public static readonly SweepCursor Empty = new(0, "", false, 0);

        public bool IsEmpty => NextStage == 0 && BrowserAfterId.Length == 0 && !BrowserHadRetryableFailures && BrowserRetryAfterTicks == 0;
    }

    private static TimeSpan BrowserBackoff(TimeSpan? retryAfter) =>
        retryAfter is { } value && value > RetryDelay
            ? value < Interval ? value : Interval
            : RetryDelay;

    private static SweepCursor ReadCursor(string? cursor)
    {
        if (cursor is not { Length: > 0 } value)
        {
            return SweepCursor.Empty;
        }

        var parts = value.Split('|', 4);
        if (parts.Length == 4
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var nextStage)
            && nextStage < StageCount
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var retryFlag)
            && retryFlag <= 1
            && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var retryAfterTicks))
        {
            return new SweepCursor(nextStage, parts[3], retryFlag == 1, retryAfterTicks);
        }

        return SweepCursor.Empty;
    }

    private static string? FormatCursor(SweepCursor cursor) =>
        cursor.IsEmpty
            ? null
            : string.Create(CultureInfo.InvariantCulture, $"{cursor.NextStage}|{(cursor.BrowserHadRetryableFailures ? 1 : 0)}|{cursor.BrowserRetryAfterTicks}|{cursor.BrowserAfterId}");

    private async Task<bool> DueAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await using var connection = await storage.OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(SweepDue,
            new { Now = now, SessionRow.Credential, SessionRow.Browser, Unfinished = now - McpRegistrations.UnfinishedLifetime }) > 0;
    }

    public static async Task<bool> DrainAsync(TimeProvider time, long started, TimeSpan budget, Func<Task<int>> step, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(step);
        while (time.GetElapsedTime(started) < budget)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await step() == 0)
            {
                return true;
            }
        }

        return false;
    }

    internal static string Budgeted(string rows) =>
        "SELECT id FROM (SELECT id, size, SUM(size) OVER (ORDER BY id ROWS UNBOUNDED PRECEDING) AS total "
        + $"FROM ({rows} ORDER BY id LIMIT {RowBatch})) WHERE total = size OR total <= {ByteBudget}";
}
