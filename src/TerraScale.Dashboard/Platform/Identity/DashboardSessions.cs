using Dapper;
using System.Data;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.TsAuth;

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
    private const string Expired = DashboardSql.Sessions.Select + " WHERE kind = @Browser AND expires_at <= @Now AND (@After IS NULL OR id > @After) ORDER BY id LIMIT @Limit";
    private const string RemoveToken = "DELETE FROM sessions WHERE token_hash = @TokenHash AND expires_at > @Now";
    private const string RevokeOther = "DELETE FROM sessions WHERE id = @Id AND user_id = @UserId AND kind = @Browser AND token_hash <> @Current";
    private const string RevokeBrowsers = "DELETE FROM sessions WHERE user_id = @UserId AND kind = @Browser";
    private const string ByToken = DashboardSql.Sessions.Select + " WHERE token_hash = @TokenHash AND kind = @Browser";
    private const string ById = DashboardSql.Sessions.Select + " WHERE id = @Id AND kind = @Credential";

    internal const string Cookie = "ts-dashboard-session";
    private const int MaxBrowserSessions = 1000;
    private static readonly TimeSpan RenewBeforeExpiry = TimeSpan.FromSeconds(30);

    public async Task StartAsync(HttpContext context, SessionRecord record)
    {
        if (context.Request.Cookies.ContainsKey(Cookie))
        {
            await EndAsync(context);
        }

        record = record with { SessionExpiresAt = record.Actor is null ? time.GetUtcNow().AddHours(12) : record.ExpiresAt, RefreshLease = "", RefreshLeaseUntil = null, Device = DeviceLabel.Parse(context.Request.Headers.UserAgent) };
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var credentials = await identity.ReadAsync(context.RequestAborted);
        var now = time.GetUtcNow().UtcDateTime;
        await using (var connection = await storage.OpenAsync(context.RequestAborted))
        {
            if (await connection.ExecuteScalarAsync<long>(ActiveBrowsers, new { record.UserId, SessionRow.Browser, Now = now }) >= MaxBrowserSessions)
            {
                throw new IOException("Too many active dashboard sessions.");
            }

            await connection.ExecuteAsync(DashboardSql.Sessions.Insert, NewRow(SessionRow.Browser, Key(token), record, credentials.SessionEncryptionKey));
        }

        await AccountEnrollment.ThrowIfErasedAsync(storage, record.UserId, context.RequestAborted);
        Csrf.Bind(context, token);
        context.Response.Cookies.Append(Cookie, token, DashboardCookies.Private(context, SameSiteMode.Lax, record.SessionExpiresAt));
    }

    public async Task<SessionRecord?> ReadAsync(HttpContext context)
    {
        return (await ReadWithAsync<object?>(context, _ => Task.FromResult<object?>(null)))?.Session;
    }

    public async Task<(SessionRecord Session, T Value)?> ReadWithAsync<T>(HttpContext context, Func<SessionRecord, Task<T>> alongside)
    {
        if (BrowserToken(context) is not { } token)
        {
            return null;
        }

        Task<T>? work = null;
        var record = await ResolveAsync(Lookup.Browser(Key(token)), context.RequestAborted, alongside: session => work = alongside(session));
        if (record is null)
        {
            Clear(context);
            return null;
        }
        return (record, await work!);
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
        var row = NewRow(SessionRow.Credential, null, record, credentials.SessionEncryptionKey);
        await using (var connection = await storage.OpenAsync(cancellationToken))
        {
            await connection.ExecuteAsync(DashboardSql.Sessions.Insert, row);
        }

        await AccountEnrollment.ThrowIfErasedAsync(storage, record.UserId, cancellationToken);
        return row.Id;
    }

    public Task<SessionRecord?> ReadCredentialAsync(string id, CancellationToken cancellationToken)
    {
        return TerraScaleIds.IsValid(id) ? ResolveAsync(Lookup.Credential(id), cancellationToken) : Task.FromResult<SessionRecord?>(null);
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

    private async Task<SessionRecord?> ResolveAsync(Lookup lookup, CancellationToken cancellationToken, Func<SessionRecord, Task>? alongside = null)
    {
        var credentials = await identity.ReadAsync(cancellationToken);
        var waitDeadline = Stopwatch.StartNew();
        var renewBefore = RenewBeforeExpiry;
        while (true)
        {
            var row = await FindAsync(lookup, cancellationToken);
            if (row is null)
            {
                return null;
            }

            var record = Open(row, credentials.SessionEncryptionKey);
            var now = time.GetUtcNow();
            if ((record.SessionExpiresAt ?? record.ExpiresAt) <= now)
            {
                if (row.Kind == SessionRow.Credential)
                {
                    await RemoveAsync(row, "expired", cancellationToken);
                }

                return null;
            }

            if (record.ExpiresAt <= now + renewBefore)
            {
                if (string.IsNullOrWhiteSpace(record.RefreshToken))
                {
                    await RemoveAsync(row, "no refresh token", cancellationToken);
                    return null;
                }
                var leaseEnded = record.RefreshLeaseUntil is null || record.RefreshLeaseUntil <= now;
                if (record.RefreshLease.Length != 0 && !(leaseEnded && lookup.Kind == SessionRow.Credential))
                {
                    if (leaseEnded) { await RemoveAsync(row, "abandoned refresh lease", cancellationToken); return null; }
                    if (waitDeadline.Elapsed > TimeSpan.FromSeconds(20))
                    {
                        throw new TimeoutException("Session refresh is still in progress.");
                    }

                    await Task.Delay(100, cancellationToken);
                    continue;
                }

                using var rotation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var rotating = rotation.Token;
                var lease = record with { RefreshLease = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), RefreshLeaseUntil = now.AddMinutes(1) };
                if (await ReplaceAsync(row, lease, credentials.SessionEncryptionKey, rotating) is null)
                {
                    continue;
                }

                SessionRecord refreshed;
                try { refreshed = await refresh.RefreshAsync(lease, rotating); }
                catch (SignInRejectedException)
                {
                    if (await FindAsync(lookup, rotating) is { } refused)
                    {
                        await RemoveAsync(refused, "refresh rejected by ts-auth", rotating);
                    }

                    return null;
                }
                catch (Exception error) when (error is RefreshNotStartedException || lookup.Kind == SessionRow.Credential)
                {
                    var level = error is RefreshNotStartedException ? LogLevel.Debug : LogLevel.Warning;
                    if (logger.IsEnabled(level))
                    {
                        var cause = error is RefreshNotStartedException { InnerException: { } inner } ? inner : error;
                        logger.RefreshOfAStoredTsAuth(level, lookup.Kind, cause.GetType().Name, (cause as HttpRequestException)?.StatusCode, (cause as TsAuthException)?.ErrorCode ?? "none");
                    }

                    if (await FindAsync(lookup, cancellationToken) is { } unchanged && Open(unchanged, credentials.SessionEncryptionKey).RefreshLease == lease.RefreshLease)
                    {
                        await ReplaceAsync(unchanged, record, credentials.SessionEncryptionKey, cancellationToken);
                    }

                    if (record.ExpiresAt <= time.GetUtcNow())
                    {
                        throw;
                    }

                    renewBefore = TimeSpan.Zero;
                    continue;
                }
                catch (Exception error)
                {
                    logger.RefreshOfAStoredTsAuth(LogLevel.Warning, lookup.Kind, error.GetType().Name, (error as HttpRequestException)?.StatusCode,
                        (error as TsAuthException)?.ErrorCode ?? "none");
                    throw;
                }

                if (refreshed.UserId != record.UserId || refreshed.Email != record.Email || refreshed.ExpiresAt <= now)
                {
                    throw new IOException("Refreshed session identity is invalid.");
                }

                refreshed = refreshed with { SessionExpiresAt = record.SessionExpiresAt, RefreshLease = "", RefreshLeaseUntil = null, Device = record.Device };
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        var latest = await FindAsync(lookup, rotating);
                        if (latest is null)
                        {
                            return null;
                        }

                        var current = Open(latest, credentials.SessionEncryptionKey);
                        if (attempt > 0 && current.AccessToken == refreshed.AccessToken && current.RefreshLease.Length == 0)
                        {
                            break;
                        }

                        if (current.RefreshLease != lease.RefreshLease)
                        {
                            throw new IOException("Session refresh ownership changed.");
                        }

                        if (await ReplaceAsync(latest, refreshed, credentials.SessionEncryptionKey, rotating) is null)
                        {
                            if (attempt is 0 or >= 5)
                            {
                                return null;
                            }

                            continue;
                        }
                        break;
                    }
                    catch (Exception error) when (attempt < 5 && (error is TimeoutException or HttpRequestException { StatusCode: null or >= HttpStatusCode.InternalServerError or HttpStatusCode.TooManyRequests } ||
                                                                  error is OperationCanceledException && !rotating.IsCancellationRequested))
                    {
                        logger.SavingARefreshedTsAuthGrant(lookup.Kind, error.GetType().Name);
                        await Task.Delay(TimeSpan.FromMilliseconds(500 << attempt), rotating);
                    }
                }
                record = refreshed;
            }
            var active = auth.IsActiveAsync(record, cancellationToken);
            var work = alongside?.Invoke(record);
            if (!await active)
            {
                if (work is not null)
                {
                    _ = work.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
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
                return null;
            }
            return record;
        }
    }

    public async Task ExpireBrowserSessionsAsync(int batchSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        var key = (await identity.ReadAsync(cancellationToken)).SessionEncryptionKey;
        var now = time.GetUtcNow().UtcDateTime;
        string? after = null;
        while (true)
        {
            List<SessionRow> expired;
            await using (var connection = await storage.OpenAsync(cancellationToken))
            {
                expired = (await connection.QueryAsync<SessionRow>(Expired, new { SessionRow.Browser, Now = now, After = after, Limit = batchSize })).AsList();
            }

            foreach (var row in expired)
            {
                SessionRecord record;
                try { record = Open(row, key); }
                catch (IOException)
                {
                    await RemoveAsync(row, "unreadable", cancellationToken);
                    continue;
                }

                try { await auth.RevokeAsync(record, cancellationToken); }
                catch (Exception error) when (error is HttpRequestException or TimeoutException)
                {
                    logger.RevokingAnExpiredSessionFailed(row.Id, error.GetType().Name, (error as HttpRequestException)?.StatusCode, (error as TsAuthException)?.ErrorCode ?? "none");
                    if (ConcernsTheClient(error))
                    {
                        throw;
                    }

                    continue;
                }

                await RemoveAsync(row, "expired", cancellationToken);
            }

            if (expired.Count < batchSize)
            {
                return;
            }

            after = expired[^1].Id;
        }
    }

    private static bool ConcernsTheClient(Exception error) => error switch
    {
        TsAuthException { ErrorCode: "invalid_client" } => true,
        TsAuthException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout } => true,
        TsAuthException => false,
        _ => true,
    };

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
        return context.Request.Cookies[Cookie] is { Length: 64 } token && token.All(char.IsAsciiHexDigitLower) ? token : null;
    }

    private static void Clear(HttpContext context)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.Cookies.Delete(Cookie, DashboardCookies.Private(context, SameSiteMode.Lax));
        }

        Csrf.Bind(context, null);
    }

    private async Task<SessionRow?> FindAsync(Lookup lookup, CancellationToken cancellationToken)
    {
        await using var connection = await storage.OpenAsync(cancellationToken);
        return lookup.Kind == SessionRow.Browser
            ? await connection.QuerySingleOrDefaultAsync<SessionRow>(ByToken, new { TokenHash = lookup.Key, SessionRow.Browser })
            : await connection.QuerySingleOrDefaultAsync<SessionRow>(ById, new { Id = lookup.Key, SessionRow.Credential });
    }

    private async Task<SessionRow?> ReplaceAsync(SessionRow row, SessionRecord record, string key, CancellationToken cancellationToken)
    {
        var (nonce, ciphertext, tag) = Seal(record, key);
        var next = Copy(row) with { OriginalVersion = row.Version, Version = AccountService.NewId(), Nonce = nonce, Ciphertext = ciphertext, Tag = tag };
        await using var connection = await storage.OpenAsync(cancellationToken);
        return await connection.ExecuteAsync(DashboardSql.Sessions.Update, next) == 1 ? next : null;
    }

    private async Task RemoveAsync(SessionRow row, string reason, CancellationToken cancellationToken)
    {
        logger.DroppedStoredGrant(reason == "expired" ? LogLevel.Information : LogLevel.Warning, row.Kind, reason);
        await using var connection = await storage.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(DashboardSql.Sessions.Delete, Copy(row) with { OriginalVersion = row.Version });
    }

    private static SessionRow NewRow(string kind, string? tokenHash, SessionRecord record, string key)
    {
        var (nonce, ciphertext, tag) = Seal(record, key);
        return new()
        {
            Id = AccountService.NewId(),
            Kind = kind,
            TokenHash = tokenHash,
            UserId = record.UserId,
            Device = record.Device ?? "",
            ExpiresAt = record.SessionExpiresAt!.Value.UtcDateTime,
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

    private static (byte[] Nonce, byte[] Ciphertext, byte[] Tag) Seal(SessionRecord record, string key)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[bytes.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(Convert.FromBase64String(key), 16);
        try
        {
            aes.Encrypt(nonce, bytes, cipher, tag, "TerraScale.Dashboard/session/v1"u8);
            return (nonce, cipher, tag);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static SessionRecord Open(SessionRow row, string key)
    {
        try
        {
            var plain = new byte[row.Ciphertext.Length];
            using var aes = new AesGcm(Convert.FromBase64String(key), 16);
            try
            {
                aes.Decrypt(row.Nonce, row.Ciphertext, row.Tag, plain, "TerraScale.Dashboard/session/v1"u8);
                var record = JsonSerializer.Deserialize<SessionRecord>(plain) ?? throw new IOException("Dashboard session is invalid.");
                if (string.IsNullOrWhiteSpace(record.UserId) || string.IsNullOrWhiteSpace(record.Email) ||
                    string.IsNullOrWhiteSpace(record.AccessToken) || record.RefreshLease is null || record.UserId != row.UserId)
                {
                    throw new IOException("Dashboard session is invalid.");
                }

                return record;
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception error) when (error is FormatException or CryptographicException or ArgumentException or JsonException)
        { throw new IOException("Dashboard session is invalid.", error); }
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
    public override string ToString()
    {
        return "Dashboard session [REDACTED]";
    }
}

public sealed record DashboardSessionInfo(string Id, bool Current, string Device, DateTimeOffset ExpiresAt);
