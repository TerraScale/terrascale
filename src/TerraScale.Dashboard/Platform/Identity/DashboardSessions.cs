using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Dapper;
using TerraScale.Dashboard.Platform.Mcp;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.TsAuth;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Platform.Identity;

public sealed class DashboardSessions(DashboardDb storage, IDashboardIdentity identity, IAuthGateway auth,
    IAuthRefreshGateway refresh, TimeProvider time, ILogger<DashboardSessions> logger)
{
    private const string ActiveBrowsers = "SELECT count(*) FROM sessions WHERE user_id = @UserId AND kind = @Browser AND expires_at > @Now";
    private const string RemoveCredential = "DELETE FROM sessions WHERE id = @Id AND kind = @Credential";
    private const string CredentialOwner = "SELECT user_id FROM sessions WHERE id = @Id AND kind = @Credential";
    private const string RemoveOwned = "DELETE FROM sessions WHERE id = @Id AND user_id = @UserId";
    private const string RemoveUser = "DELETE FROM sessions WHERE user_id = @UserId";
    private const string Browsers = DashboardSql.Sessions.Select + " WHERE user_id = @UserId AND kind = @Browser AND expires_at > @Now";
    private const string Expired = DashboardSql.Sessions.Select + " WHERE kind = @Browser AND expires_at <= @Now AND id > @After ORDER BY id LIMIT @Limit";
    private const string RemoveToken = "DELETE FROM sessions WHERE token_hash = @TokenHash AND expires_at > @Now";
    private const string RevokeOther = "DELETE FROM sessions WHERE id = @Id AND user_id = @UserId AND kind = @Browser AND token_hash <> @Current";
    private const string RevokeBrowsers = "DELETE FROM sessions WHERE user_id = @UserId AND kind = @Browser";
    private const string ByToken = DashboardSql.Sessions.Select + " WHERE token_hash = @TokenHash AND kind = @Browser";
    private const string ById = DashboardSql.Sessions.Select + " WHERE id = @Id AND kind = @Credential";

    internal const string Cookie = "__Host-ts-dashboard-session";
    private const int MaxBrowserSessions = 1000;
    private static readonly TimeSpan RenewBeforeExpiry = TimeSpan.FromSeconds(30);

    public async Task StartAsync(HttpContext context, SessionRecord record)
    {
        if (DashboardCookies.Read(context, Cookie) is not null)
        {
            await EndAsync(context);
        }

        var sessionExpiresAt = record.Actor is null ? time.GetUtcNow().AddHours(12) : record.ExpiresAt;
        var device = DeviceLabel.Parse(context.Request.Headers.UserAgent);
        record = record with { SessionExpiresAt = sessionExpiresAt, RefreshLease = "", RefreshLeaseUntil = null, Device = device };
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var credentials = await identity.ReadAsync(context.RequestAborted);
        var now = time.GetUtcNow().UtcDateTime;
        await using (var connection = await storage.OpenAsync(context.RequestAborted))
        {
            if (await connection.ExecuteScalarAsync<long>(ActiveBrowsers, new { record.UserId, SessionRow.Browser, Now = now }) >= MaxBrowserSessions)
            {
                throw new IOException("Too many active dashboard sessions.");
            }

            await connection.ExecuteAsync(DashboardSql.Sessions.Insert, NewRow(SessionRow.Browser, Key(token), record, device, sessionExpiresAt, credentials.SessionEncryptionKey));
        }

        await AccountEnrollment.ThrowIfErasedAsync(storage, record.UserId, context.RequestAborted);
        Csrf.Bind(context, token);
        context.Response.Cookies.Append(Cookie, token, DashboardCookies.Private(context, SameSiteMode.Lax, record.SessionExpiresAt));
    }

    public async Task<SessionResolutionOutcome> ReadAsync(HttpContext context)
    {
        var outcome = await ReadWithAsync<object?>(context, static _ => Task.FromResult<object?>(null));
        return Resolution(outcome);
    }

    private static SessionResolutionOutcome Resolution(SessionReadOutcome<object?> outcome) => outcome switch
    {
        SessionRead<object?> read => read.Session,
        SessionAbsent absent => absent,
        SessionUnavailable unavailable => unavailable,
    };

    public async Task<SessionReadOutcome<T>> ReadWithAsync<T>(HttpContext context, Func<SessionRecord, Task<T>> alongside)
    {
        if (BrowserToken(context) is not { } token)
        {
            return new SessionAbsent();
        }

        var resolved = await ResolveAsync(Lookup.Browser(Key(token)), alongside, context.RequestAborted);
        return resolved switch
        {
            SessionRead<T> read => read,
            SessionAbsent absent => ClearAbsent(context, absent),
            SessionUnavailable unavailable => unavailable,
        };
    }

    private static SessionAbsent ClearAbsent(HttpContext context, SessionAbsent absent)
    {
        Clear(context);
        return absent;
    }

    public async Task<string> CreateCredentialAsync(SessionRecord record, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        if (record.SessionExpiresAt is not null || record.RefreshLease.Length != 0)
        {
            throw new InvalidOperationException("A new credential requires a fresh ts-auth grant.");
        }

        if (expiresAt <= time.GetUtcNow() || expiresAt > time.GetUtcNow().AddDays(365))
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt));
        }

        var credentials = await identity.ReadAsync(cancellationToken);
        record = record with { SessionExpiresAt = expiresAt, RefreshLease = "", RefreshLeaseUntil = null };
        var row = NewRow(SessionRow.Credential, null, record, "", expiresAt, credentials.SessionEncryptionKey);
        await using (var connection = await storage.OpenAsync(cancellationToken))
        {
            await connection.ExecuteAsync(DashboardSql.Sessions.Insert, row);
        }

        await AccountEnrollment.ThrowIfErasedAsync(storage, record.UserId, cancellationToken);
        return row.Id;
    }

    public async Task<SessionResolutionOutcome> ReadCredentialAsync(string id, CancellationToken cancellationToken)
    {
        if (!TerraScaleIds.IsValid(id))
        {
            return new SessionAbsent();
        }

        var outcome = await ResolveAsync<object?>(Lookup.Credential(id), static _ => Task.FromResult<object?>(null), cancellationToken);
        return Resolution(outcome);
    }

    public async Task<SessionRecord?> PeekCredentialAsync(string id, CancellationToken cancellationToken)
    {
        if (!TerraScaleIds.IsValid(id))
        {
            return null;
        }

        var row = await FindAsync(Lookup.Credential(id), cancellationToken);
        if (row is null)
        {
            return null;
        }

        var credentials = await identity.ReadAsync(cancellationToken);
        return TryOpen(row, credentials.SessionEncryptionKey);
    }

    public async Task DeleteCredentialAsync(string id, CancellationToken cancellationToken)
    {
        if (!TerraScaleIds.IsValid(id))
        {
            return;
        }

        await using var connection = await storage.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(RemoveCredential, new { Id = id, SessionRow.Credential });
    }

    public async Task DeleteCredentialAsync(string id, string subject, CancellationToken cancellationToken)
    {
        if (!TerraScaleIds.IsValid(id))
        {
            throw new IOException("Credential identity is invalid.");
        }

        await using var connection = await storage.OpenAsync(cancellationToken);
        var owner = await connection.QuerySingleOrDefaultAsync<string>(CredentialOwner, new { Id = id, SessionRow.Credential });
        if (owner is null)
        {
            return;
        }

        if (owner != subject)
        {
            throw new IOException("Credential ownership is invalid.");
        }

        await connection.ExecuteAsync(RemoveOwned, new { Id = id, UserId = subject });
    }

    public async Task ForgetUserAsync(string userId, CancellationToken cancellationToken)
    {
        await using var connection = await storage.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(RemoveUser, new { UserId = userId });
    }

    public async Task<IReadOnlyList<DashboardSessionInfo>> ListAsync(HttpContext context, SessionRecord session)
    {
        var current = BrowserToken(context) is { } token ? Key(token) : "";
        var now = time.GetUtcNow().UtcDateTime;
        await using var connection = await storage.OpenAsync(context.RequestAborted);
        var rows = await connection.QueryAsync<SessionRow>(Browsers, new { session.UserId, SessionRow.Browser, Now = now });
        return [.. rows.Select(row => new DashboardSessionInfo(row.Id, row.TokenHash == current, row.Device is { Length: > 0 } device ? device : "Unknown device",
                new DateTimeOffset(DateTime.SpecifyKind(row.ExpiresAt, DateTimeKind.Utc))))
            .OrderByDescending(item => item.ExpiresAt).ThenBy(item => item.Id, StringComparer.Ordinal)];
    }

    private async Task<SessionReadOutcome<T>> ResolveAsync<T>(Lookup lookup, Func<SessionRecord, Task<T>> alongside, CancellationToken cancellationToken)
    {
        var credentials = await identity.ReadAsync(cancellationToken);
        var waitDeadline = Stopwatch.StartNew();
        var renewBefore = RenewBeforeExpiry;
        while (true)
        {
            var row = await FindAsync(lookup, cancellationToken);
            if (row is null)
            {
                return new SessionAbsent();
            }

            var record = Open(row, credentials.SessionEncryptionKey);
            var now = time.GetUtcNow();
            if ((record.SessionExpiresAt ?? record.ExpiresAt) <= now)
            {
                if (row.Kind == SessionRow.Credential)
                {
                    await RemoveAsync(row, "expired", cancellationToken);
                }

                return new SessionAbsent();
            }

            var refreshable = !string.IsNullOrWhiteSpace(record.RefreshToken);
            if (!refreshable && record.ExpiresAt <= now)
            {
                await RemoveAsync(row, "no refresh token", cancellationToken);
                return new SessionAbsent();
            }

            if (refreshable && record.ExpiresAt <= now + renewBefore)
            {
                var leaseEnded = record.RefreshLeaseUntil is null || record.RefreshLeaseUntil <= now;
                if (record.RefreshLease.Length != 0 && !(leaseEnded && lookup.Kind == SessionRow.Credential))
                {
                    if (leaseEnded)
                    {
                        await RemoveAsync(row, "abandoned refresh lease", cancellationToken);
                        return new SessionAbsent();
                    }

                    if (waitDeadline.Elapsed > TimeSpan.FromSeconds(20))
                    {
                        return new SessionUnavailable(nameof(TimeoutException), "Session refresh is still in progress.");
                    }

                    await Task.Delay(100, cancellationToken);
                    continue;
                }

                using var rotation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var rotating = rotation.Token;
                var lease = record with { RefreshLease = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), RefreshLeaseUntil = now + TimeSpan.FromMinutes(2) };
                if (await ReplaceAsync(row, lease, credentials.SessionEncryptionKey, rotating) is null)
                {
                    continue;
                }

                var refreshOutcome = await refresh.TryRefreshAsync(lease, TimeSpan.FromSeconds(30), CancellationToken.None);
                using var persistence = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var persisting = persistence.Token;
                var refreshStep = refreshOutcome switch
                {
                    SessionRecord updated => RefreshStep.Succeeded(updated),
                    SessionRefreshRejected => RefreshStep.Rejected(),
                    SessionRefreshNotStarted notStarted => RefreshStep.Retryable(notStarted.ErrorType, notStarted.StatusCode, notStarted.ErrorCode, notStarted.Message, LogLevel.Debug),
                    SessionRefreshFailed failed => lookup.Kind == SessionRow.Credential
                        ? RefreshStep.Retryable(failed.ErrorType, failed.StatusCode, failed.ErrorCode, failed.Message, LogLevel.Warning)
                        : RefreshStep.Fatal(failed.ErrorType, failed.StatusCode, failed.ErrorCode, failed.Message),
                };
                if (refreshStep.Record is not { } refreshed)
                {
                    if (refreshStep.Error is not { } error)
                    {
                        if (await FindAsync(lookup, persisting) is { } refused)
                        {
                            await RemoveAsync(refused, "refresh rejected by ts-auth", persisting);
                        }

                        return new SessionAbsent();
                    }

                    if (logger.IsEnabled(error.Level))
                    {
                        logger.RefreshOfAStoredTsAuth(error.Level, lookup.Kind, error.ErrorType, error.StatusCode, error.ErrorCode);
                    }

                    if (error.ShouldRetry)
                    {
                        if (await FindAsync(lookup, persisting) is { } unchanged && Open(unchanged, credentials.SessionEncryptionKey).RefreshLease == lease.RefreshLease)
                        {
                            await ReplaceAsync(unchanged, record, credentials.SessionEncryptionKey, persisting);
                        }

                        if (record.ExpiresAt > time.GetUtcNow())
                        {
                            renewBefore = TimeSpan.Zero;
                            continue;
                        }
                    }

                    return new SessionUnavailable(error.ErrorType, error.Message);
                }

                if (refreshed.UserId != record.UserId || refreshed.Email != record.Email || refreshed.ExpiresAt <= now)
                {
                    return new SessionUnavailable(nameof(IOException), "Refreshed session identity is invalid.");
                }

                refreshed = refreshed with { SessionExpiresAt = record.SessionExpiresAt, RefreshLease = "", RefreshLeaseUntil = null, Device = record.Device };
                for (var attempt = 0; ; attempt++)
                {
                    SessionRow? latest = null;
                    var unreadable = await storage.TryReadAsync(async connection => latest = await FindAsync(connection, lookup, persisting), persisting);
                    var conflicted = false;
                    string unsaved;
                    if (unreadable is { } failure)
                    {
                        unsaved = failure.Failure;
                    }
                    else
                    {
                        if (latest is null)
                        {
                            return new SessionAbsent();
                        }

                        var current = Open(latest, credentials.SessionEncryptionKey);
                        if (attempt > 0 && current.AccessToken == refreshed.AccessToken && current.RefreshLease.Length == 0)
                        {
                            break;
                        }

                        if (current.RefreshLease != lease.RefreshLease)
                        {
                            return new SessionUnavailable(nameof(IOException), "Session refresh ownership changed.");
                        }

                        var saved = await TryReplaceAsync(latest, refreshed, credentials.SessionEncryptionKey, persisting);
                        (bool Saved, bool Conflicted, string Reason) outcome = saved switch
                        {
                            StorageWritten => (true, false, ""),
                            StorageConflicted => (false, true, "concurrent session change"),
                            StorageDuplicate => (false, true, "duplicate session record"),
                            StorageFailed failed => (false, false, failed.Failure),
                        };
                        if (outcome.Saved)
                        {
                            break;
                        }

                        conflicted = outcome.Conflicted;
                        unsaved = outcome.Reason;
                    }

                    if (attempt >= 5)
                    {
                        return new SessionUnavailable(
                            conflicted ? nameof(DBConcurrencyException) : nameof(IOException),
                            "The refreshed session could not be saved: " + unsaved + ".");
                    }

                    logger.SavingARefreshedTsAuthGrant(lookup.Kind, unsaved);
                    await (conflicted
                        ? DashboardDb.ConflictBackoffAsync(attempt, time, persisting)
                        : Task.Delay(TimeSpan.FromMilliseconds(500 << attempt), time, persisting));
                }
                record = refreshed;
            }
            var active = auth.IsActiveAsync(record, cancellationToken);
            var work = alongside(record);
            var activity = await active;
            var step = activity switch
            {
                SessionStillActive => ActivityStep.Active(),
                SessionEnded => ActivityStep.Ended(),
                SessionActivityUnavailable unavailable => ActivityStep.Unavailable(unavailable.ErrorType, unavailable.Message),
            };
            if (step.IsActive)
            {
                return new SessionRead<T>(record, await work);
            }

            _ = work.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);

            if (step.UnavailableOutcome is { } sessionUnavailable)
            {
                return sessionUnavailable;
            }

            if (await FindAsync(lookup, cancellationToken) is { } rejected)
            {
                var latestRecord = Open(rejected, credentials.SessionEncryptionKey);
                if (latestRecord.AccessToken != record.AccessToken)
                {
                    continue;
                }

                if (record.ExpiresAt <= time.GetUtcNow())
                {
                    continue;
                }

                await RemoveAsync(rejected, "inactive at ts-auth", cancellationToken);
            }

            return new SessionAbsent();
        }
    }

    public async Task<ExpireBrowserSessionsOutcome> ExpireBrowserSessionsAsync(
        int batchSize,
        long started,
        TimeSpan budget,
        string afterId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentNullException.ThrowIfNull(afterId);
        if (budget <= TimeSpan.Zero || time.GetElapsedTime(started) >= budget)
        {
            return new BrowserSessionsBudgetExhausted(afterId, false);
        }

        var key = (await identity.ReadAsync(cancellationToken)).SessionEncryptionKey;
        var now = time.GetUtcNow().UtcDateTime;
        var after = afterId;
        var retryableFailures = false;
        while (time.GetElapsedTime(started) < budget)
        {
            List<SessionRow> expired;
            await using (var connection = await storage.OpenAsync(cancellationToken))
            {
                expired = (await connection.QueryAsync<SessionRow>(Expired, new { SessionRow.Browser, Now = now, After = after, Limit = batchSize })).AsList();
            }

            foreach (var row in expired)
            {
                if (time.GetElapsedTime(started) >= budget)
                {
                    return new BrowserSessionsBudgetExhausted(after, retryableFailures);
                }

                if (TryOpen(row, key) is not { } record)
                {
                    await RemoveAsync(row, "unreadable", cancellationToken);
                    after = row.Id;
                    continue;
                }

                var revoked = await auth.TryRevokeAsync(record, cancellationToken);
                var step = revoked switch
                {
                    TokenRevoked => RevokeStep.Remove("expired"),
                    TokenAlreadyRevoked => RevokeStep.Remove("already-revoked"),
                    TokenRevocationRateLimited limited => LogRateLimited(row.Id, limited),
                    TokenRevocationClientRejected rejected => LogClientRejected(row.Id, rejected),
                    TokenRevocationUnavailable unavailable => LogUnavailable(row.Id, unavailable),
                };
                if (step.Stop)
                {
                    return step.ClientRejected
                        ? new BrowserSessionsClientRejected(after, retryableFailures)
                        : new BrowserSessionsRateLimited(after, retryableFailures, step.RetryAfter);
                }

                if (step.RemoveReason is { } reason)
                {
                    await RemoveAsync(row, reason, cancellationToken);
                }
                else
                {
                    retryableFailures = true;
                }

                after = row.Id;
            }

            if (expired.Count < batchSize)
            {
                return retryableFailures ? new BrowserSessionsRetryableFailures() : new BrowserSessionsDrained();
            }
        }

        return new BrowserSessionsBudgetExhausted(after, retryableFailures);
    }

    private RevokeStep LogRateLimited(string sessionId, TokenRevocationRateLimited limited)
    {
        logger.RevokingAnExpiredSessionFailed(sessionId, "TsAuthRateLimited", HttpStatusCode.TooManyRequests, limited.ErrorCode);
        return RevokeStep.RateLimited(limited.RetryAfter);
    }

    private RevokeStep LogClientRejected(string sessionId, TokenRevocationClientRejected rejected)
    {
        logger.RevokingAnExpiredSessionFailed(sessionId, "TsAuthClientRejected", rejected.StatusCode, rejected.ErrorCode);
        return RevokeStep.Rejected();
    }

    private RevokeStep LogUnavailable(string sessionId, TokenRevocationUnavailable unavailable)
    {
        logger.RevokingAnExpiredSessionFailed(sessionId, unavailable.ErrorType, unavailable.StatusCode, unavailable.ErrorCode);
        return RevokeStep.RetryLater();
    }

    private readonly record struct RevokeStep(string? RemoveReason, bool Stop, bool ClientRejected, TimeSpan? RetryAfter)
    {
        public static RevokeStep Remove(string reason) => new(reason, false, false, null);

        public static RevokeStep RateLimited(TimeSpan? retryAfter) => new(null, true, false, retryAfter);

        public static RevokeStep Rejected() => new(null, true, true, null);

        public static RevokeStep RetryLater() => new(null, false, false, null);
    }

    public async Task EndAsync(HttpContext context)
    {
        if (BrowserToken(context) is { } token)
        {
            var hash = Key(token);
            var now = time.GetUtcNow().UtcDateTime;
            await using var connection = await storage.OpenAsync(context.RequestAborted);
            await connection.ExecuteAsync(RemoveToken, new { TokenHash = hash, Now = now });
        }
        Clear(context);
    }

    public async Task RevokeAsync(HttpContext context, SessionRecord session, string id)
    {
        if (!TerraScaleIds.IsValid(id))
        {
            return;
        }

        var current = BrowserToken(context) is { } token ? Key(token) : "";
        await using var connection = await storage.OpenAsync(context.RequestAborted);
        await connection.ExecuteAsync(RevokeOther, new { Id = id, session.UserId, SessionRow.Browser, Current = current });
    }

    public async Task RevokeUserAsync(string subject, CancellationToken cancellationToken)
    {
        await using var connection = await storage.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(RevokeBrowsers, new { UserId = subject, SessionRow.Browser });
    }

    private static string? BrowserToken(HttpContext context)
    {
        return DashboardCookies.Read(context, Cookie) is { Length: 64 } token && token.All(char.IsAsciiHexDigitLower) ? token : null;
    }

    private static void Clear(HttpContext context)
    {
        if (!context.Response.HasStarted)
        {
            DashboardCookies.Delete(context, Cookie, SameSiteMode.Lax);
        }

        Csrf.Bind(context, null);
    }

    private async Task<SessionRow?> FindAsync(Lookup lookup, CancellationToken cancellationToken)
    {
        await using var connection = await storage.OpenAsync(cancellationToken);
        return await FindAsync(connection, lookup, cancellationToken);
    }

    private static async Task<SessionRow?> FindAsync(TerraBaseDbConnection connection, Lookup lookup, CancellationToken cancellationToken)
    {
        var (sql, parameters) = lookup.Kind == SessionRow.Browser
            ? (ByToken, new Dictionary<string, object?> { ["TokenHash"] = lookup.Key, ["Browser"] = SessionRow.Browser })
            : (ById, new Dictionary<string, object?> { ["Id"] = lookup.Key, ["Credential"] = SessionRow.Credential });
        var rows = await DashboardDb.RowsAsync(connection, sql, parameters, ReadSessionRow, null, cancellationToken);
        return rows.Count switch
        {
            0 => null,
            1 => rows[0],
            _ => throw new InvalidOperationException("Sequence contains more than one element."),
        };
    }

    private Task<StorageWrite> TryReplaceAsync(SessionRow row, SessionRecord record, string key, CancellationToken cancellationToken)
    {
        var (nonce, ciphertext, tag) = Seal(record, key);
        var next = Copy(row) with { OriginalVersion = row.Version, Version = AccountService.NewId(), Nonce = nonce, Ciphertext = ciphertext, Tag = tag };
        return storage.TryWriteAsync(
            async (connection, transaction) => await DashboardDb.RunAsync(connection, DashboardSql.Sessions.Update, SessionUpdateParameters(next), transaction, cancellationToken) == 1,
            cancellationToken);
    }

    private async Task<SessionRow?> ReplaceAsync(SessionRow row, SessionRecord record, string key, CancellationToken cancellationToken)
    {
        var (nonce, ciphertext, tag) = Seal(record, key);
        var next = Copy(row) with { OriginalVersion = row.Version, Version = AccountService.NewId(), Nonce = nonce, Ciphertext = ciphertext, Tag = tag };
        await using var connection = await storage.OpenAsync(cancellationToken);
        return await DashboardDb.RunAsync(connection, DashboardSql.Sessions.Update, SessionUpdateParameters(next), null, cancellationToken) == 1 ? next : null;
    }

    private async Task RemoveAsync(SessionRow row, string reason, CancellationToken cancellationToken)
    {
        logger.DroppedStoredGrant(reason == "expired" ? LogLevel.Information : LogLevel.Warning, row.Kind, reason);
        await using var connection = await storage.OpenAsync(cancellationToken);
        await DashboardDb.RunAsync(
            connection,
            DashboardSql.Sessions.Delete,
            new Dictionary<string, object?> { ["Id"] = row.Id, ["OriginalVersion"] = row.Version },
            null,
            cancellationToken);
    }

    private static SessionRow ReadSessionRow(DbDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Ciphertext = reader.GetFieldValue<byte[]>(1),
        Device = reader.GetString(2),
        ExpiresAt = reader.GetDateTime(3),
        Kind = reader.GetString(4),
        Nonce = reader.GetFieldValue<byte[]>(5),
        Tag = reader.GetFieldValue<byte[]>(6),
        TokenHash = reader.IsDBNull(7) ? null : reader.GetString(7),
        UserId = reader.GetString(8),
        Version = reader.GetString(9),
    };

    private static Dictionary<string, object?> SessionUpdateParameters(SessionRow row) => new()
    {
        ["Id"] = row.Id,
        ["Kind"] = row.Kind,
        ["TokenHash"] = row.TokenHash,
        ["UserId"] = row.UserId,
        ["Device"] = row.Device,
        ["ExpiresAt"] = row.ExpiresAt,
        ["Version"] = row.Version,
        ["OriginalVersion"] = row.OriginalVersion,
        ["NonceHex"] = row.NonceHex,
        ["CiphertextHex"] = row.CiphertextHex,
        ["TagHex"] = row.TagHex,
    };

    private static SessionRow NewRow(string kind, string? tokenHash, SessionRecord record, string device, DateTimeOffset expiresAt, string key)
    {
        var (nonce, ciphertext, tag) = Seal(record, key);
        return new()
        {
            Id = AccountService.NewId(),
            Kind = kind,
            TokenHash = tokenHash,
            UserId = record.UserId,
            Device = device,
            ExpiresAt = expiresAt.UtcDateTime,
            Version = AccountService.NewId(),
            Nonce = nonce,
            Ciphertext = ciphertext,
            Tag = tag,
        };
    }

    private static SessionRow Copy(SessionRow row)
    {
        return new()
        {
            Id = row.Id,
            Kind = row.Kind,
            TokenHash = row.TokenHash,
            UserId = row.UserId,
            Device = row.Device,
            ExpiresAt = row.ExpiresAt,
            Version = row.Version,
            Nonce = row.Nonce,
            Ciphertext = row.Ciphertext,
            Tag = row.Tag,
        };
    }

    private static string Key(string token)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    private static (byte[] Nonce, byte[] Ciphertext, byte[] Tag) Seal(SessionRecord record, string key) =>
        SessionProtector.Seal(record, key);

    private static SessionRecord Open(SessionRow row, string key) =>
        SessionProtector.TryOpen(row, key) ?? throw new IOException("Dashboard session is invalid.");

    private static SessionRecord? TryOpen(SessionRow row, string key) =>
        SessionProtector.TryOpen(row, key);

    private readonly record struct RefreshStep(SessionRecord? Record, RefreshError? Error)
    {
        public static RefreshStep Succeeded(SessionRecord record) => new(record, null);

        public static RefreshStep Rejected() => new(null, null);

        public static RefreshStep Retryable(string errorType, HttpStatusCode? statusCode, string errorCode, string message, LogLevel level) =>
            new(null, new RefreshError(errorType, statusCode, errorCode, message, level, true));

        public static RefreshStep Fatal(string errorType, HttpStatusCode? statusCode, string errorCode, string message) =>
            new(null, new RefreshError(errorType, statusCode, errorCode, message, LogLevel.Warning, false));
    }

    private sealed record RefreshError(string ErrorType, HttpStatusCode? StatusCode, string ErrorCode, string Message, LogLevel Level, bool ShouldRetry);

    private readonly record struct ActivityStep(bool IsActive, SessionUnavailable? UnavailableOutcome)
    {
        public static ActivityStep Active() => new(true, null);

        public static ActivityStep Ended() => new(false, null);

        public static ActivityStep Unavailable(string errorType, string message) => new(false, new SessionUnavailable(errorType, message));
    }

    private sealed record Lookup(string Kind, string Key)
    {
        public static Lookup Browser(string tokenHash) => new(SessionRow.Browser, tokenHash);

        public static Lookup Credential(string id) => new(SessionRow.Credential, id);
    }
}

public sealed record SessionRecord(string UserId, string Name, string Email, string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt,
    DateTimeOffset? SessionExpiresAt = null, string RefreshLease = "", DateTimeOffset? RefreshLeaseUntil = null, string? IdToken = null,
    string? Device = null, bool OrganizationPicker = false, string? Actor = null)
{
    [JsonIgnore]
    public McpAgent? Agent { get; init; }

    public override string ToString()
    {
        return "Dashboard session [REDACTED]";
    }
}

public sealed record SessionAbsent;

public sealed record SessionUnavailable(string ErrorType, string Message);

public sealed record SessionRead<T>(SessionRecord Session, T Value);

public readonly union SessionResolutionOutcome(
    SessionRecord,
    SessionAbsent,
    SessionUnavailable);

public readonly union SessionReadOutcome<T>(
    SessionRead<T>,
    SessionAbsent,
    SessionUnavailable);

public sealed record DashboardSessionInfo(string Id, bool Current, string Device, DateTimeOffset ExpiresAt);

public sealed record BrowserSessionsDrained;

public sealed record BrowserSessionsBudgetExhausted(string AfterId, bool RetryableFailures);

public sealed record BrowserSessionsRateLimited(string AfterId, bool RetryableFailures, TimeSpan? RetryAfter);

public sealed record BrowserSessionsClientRejected(string AfterId, bool RetryableFailures);

public sealed record BrowserSessionsRetryableFailures;

public readonly union ExpireBrowserSessionsOutcome(
    BrowserSessionsDrained,
    BrowserSessionsBudgetExhausted,
    BrowserSessionsRateLimited,
    BrowserSessionsClientRejected,
    BrowserSessionsRetryableFailures);
