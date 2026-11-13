using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Platform.Identity;

public sealed class DashboardSessions(IDbContextFactory<DashboardDbContext> storage, IDashboardIdentity identity, IAuthGateway auth,
    IAuthRefreshGateway refresh, TimeProvider time, ILogger<DashboardSessions> logger)
{
    internal const string Cookie = "ts-dashboard-session";
    private const int MaxBrowserSessions = 1000;
    private static readonly TimeSpan RenewBeforeExpiry = TimeSpan.FromSeconds(30);

    public async Task StartAsync(HttpContext context, SessionRecord record)
    {
        if (context.Request.Cookies.ContainsKey(Cookie))
        {
            await EndAsync(context);
        }

        record = record with { SessionExpiresAt = time.GetUtcNow().AddHours(12), RefreshLease = "", RefreshLeaseUntil = null, Device = DeviceLabel.Parse(context.Request.Headers.UserAgent) };
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var credentials = await identity.ReadAsync(context.RequestAborted);
        var now = time.GetUtcNow().UtcDateTime;
        await using (var db = await storage.CreateDbContextAsync(context.RequestAborted))
        {
            if (await db.Sessions.CountAsync(row => row.UserId == record.UserId && row.Kind == SessionRow.Browser && row.ExpiresAt > now, context.RequestAborted) >= MaxBrowserSessions)
            {
                throw new IOException("Too many active dashboard sessions.");
            }

            db.Sessions.Add(NewRow(SessionRow.Browser, Key(token), record, credentials.SessionEncryptionKey));
            await db.SaveChangesAsync(context.RequestAborted);
        }

        await AccountEnrollment.ThrowIfErasedAsync(storage, record.UserId, context.RequestAborted);
        Csrf.Bind(context, token);
        context.Response.Cookies.Append(Cookie, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = record.SessionExpiresAt
        });
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

    public async Task<SessionRecord?> ReadForPrivilegeAsync(HttpContext context)
    {
        if (BrowserToken(context) is not { } token)
        {
            return null;
        }

        var lookup = Lookup.Browser(Key(token));
        var current = await ResolveAsync(lookup, context.RequestAborted);
        if (current is null) { Clear(context); return null; }
        var record = await ResolveAsync(lookup, context.RequestAborted, forceRefresh: true, initialAccessToken: current.AccessToken);
        if (record is not null && (record.UserId != current.UserId || record.Email != current.Email))
        {
            throw new IOException("Privileged session identity changed.");
        }

        if (record is null)
        {
            Clear(context);
        }

        return record;
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
        await using (var db = await storage.CreateDbContextAsync(cancellationToken))
        {
            db.Sessions.Add(row);
            await db.SaveChangesAsync(cancellationToken);
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

        await using var db = await storage.CreateDbContextAsync(cancellationToken);
        await db.Sessions.Where(row => row.Id == id && row.Kind == SessionRow.Credential).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task DeleteCredentialAsync(string id, string subject, CancellationToken cancellationToken)
    {
        if (!TerraScaleIds.IsValid(id))
        {
            throw new IOException("Credential identity is invalid.");
        }

        await using var db = await storage.CreateDbContextAsync(cancellationToken);
        var owner = await db.Sessions.Where(row => row.Id == id && row.Kind == SessionRow.Credential).Select(row => row.UserId).SingleOrDefaultAsync(cancellationToken);
        if (owner is null)
        {
            return;
        }

        if (owner != subject)
        {
            throw new IOException("Credential ownership is invalid.");
        }

        await db.Sessions.Where(row => row.Id == id && row.UserId == subject).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task ForgetUserAsync(string userId, CancellationToken cancellationToken)
    {
        await using var db = await storage.CreateDbContextAsync(cancellationToken);
        await db.Sessions.Where(row => row.UserId == userId).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DashboardSessionInfo>> ListAsync(HttpContext context, SessionRecord session)
    {
        var current = BrowserToken(context) is { } token ? Key(token) : "";
        var now = time.GetUtcNow().UtcDateTime;
        await using var db = await storage.CreateDbContextAsync(context.RequestAborted);
        var rows = await db.Sessions.AsNoTracking()
            .Where(row => row.UserId == session.UserId && row.Kind == SessionRow.Browser && row.ExpiresAt > now)
            .Select(row => new { row.Id, row.TokenHash, row.Device, row.ExpiresAt })
            .ToListAsync(context.RequestAborted);
        return [.. rows.Select(row => new DashboardSessionInfo(row.Id, row.TokenHash == current, row.Device is { Length: > 0 } device ? device : "Unknown device",
                new DateTimeOffset(row.ExpiresAt)))
            .OrderByDescending(item => item.ExpiresAt).ThenBy(item => item.Id, StringComparer.Ordinal)];
    }

    private async Task<SessionRecord?> ResolveAsync(Lookup lookup, CancellationToken cancellationToken, bool forceRefresh = false, string? initialAccessToken = null,
        Func<SessionRecord, Task>? alongside = null)
    {
        var credentials = await identity.ReadAsync(cancellationToken);
        var waitDeadline = Stopwatch.StartNew();
        var accessOnEntry = initialAccessToken;
        var renewBefore = RenewBeforeExpiry;
        while (true)
        {
            var row = await FindAsync(lookup, cancellationToken);
            if (row is null)
            {
                return null;
            }

            var record = Open(row, credentials.SessionEncryptionKey);
            accessOnEntry ??= record.AccessToken;
            var now = time.GetUtcNow();
            if ((record.SessionExpiresAt ?? record.ExpiresAt) <= now)
            {
                if (row.Kind == SessionRow.Credential)
                {
                    await RemoveAsync(row, "expired", cancellationToken);
                }

                return null;
            }

            if (record.ExpiresAt <= now + renewBefore || forceRefresh && record.AccessToken == accessOnEntry)
            {
                if (string.IsNullOrWhiteSpace(record.RefreshToken))
                {
                    if (forceRefresh && record.ExpiresAt > now)
                    {
                        throw new IOException("Fresh platform authorization is unavailable.");
                    }

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

                    if (forceRefresh || record.ExpiresAt <= time.GetUtcNow())
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

                if (refreshed.UserId != record.UserId || refreshed.Email != record.Email || refreshed.ExpiresAt <= now ||
                    forceRefresh && refreshed.AccessToken == accessOnEntry)
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
            await using (var db = await storage.CreateDbContextAsync(cancellationToken))
            {
                var query = db.Sessions.AsNoTracking().Where(row => row.Kind == SessionRow.Browser && row.ExpiresAt <= now);
                if (after is not null)
                {
                    query = query.Where(row => row.Id.CompareTo(after) > 0);
                }

                expired = await query.OrderBy(row => row.Id).Take(batchSize).ToListAsync(cancellationToken);
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
            await using var db = await storage.CreateDbContextAsync(context.RequestAborted);
            await db.Sessions.Where(row => row.TokenHash == hash && row.ExpiresAt > now).ExecuteDeleteAsync(context.RequestAborted);
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
        await using var db = await storage.CreateDbContextAsync(context.RequestAborted);
        await db.Sessions.Where(row => row.Id == id && row.UserId == session.UserId && row.Kind == SessionRow.Browser && row.TokenHash != current)
            .ExecuteDeleteAsync(context.RequestAborted);
    }

    public async Task RevokeUserAsync(string subject, CancellationToken cancellationToken)
    {
        await using var db = await storage.CreateDbContextAsync(cancellationToken);
        await db.Sessions.Where(row => row.UserId == subject && row.Kind == SessionRow.Browser).ExecuteDeleteAsync(cancellationToken);
    }

    private static string? BrowserToken(HttpContext context)
    {
        return context.Request.Cookies[Cookie] is { Length: 64 } token && token.All(char.IsAsciiHexDigitLower) ? token : null;
    }

    private static void Clear(HttpContext context)
    {
        context.Response.Cookies.Delete(Cookie);
        Csrf.Bind(context, null);
    }

    private async Task<SessionRow?> FindAsync(Lookup lookup, CancellationToken cancellationToken)
    {
        await using var db = await storage.CreateDbContextAsync(cancellationToken);
        var rows = db.Sessions.AsNoTracking();
        return await (lookup.Kind == SessionRow.Browser
                ? rows.Where(row => row.TokenHash == lookup.Key && row.Kind == SessionRow.Browser)
                : rows.Where(row => row.Id == lookup.Key && row.Kind == SessionRow.Credential))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<SessionRow?> ReplaceAsync(SessionRow row, SessionRecord record, string key, CancellationToken cancellationToken)
    {
        await using var db = await storage.CreateDbContextAsync(cancellationToken);
        var entry = db.Sessions.Attach(Copy(row));
        var (nonce, ciphertext, tag) = Seal(record, key);
        entry.Entity.Version = AccountService.NewId();
        entry.Entity.Nonce = nonce;
        entry.Entity.Ciphertext = ciphertext;
        entry.Entity.Tag = tag;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return entry.Entity;
        }
        catch (DbUpdateConcurrencyException)
        {
            return null;
        }
    }

    private async Task RemoveAsync(SessionRow row, string reason, CancellationToken cancellationToken)
    {
        logger.DroppedStoredGrant(reason == "expired" ? LogLevel.Information : LogLevel.Warning, row.Kind, reason);
        await using var db = await storage.CreateDbContextAsync(cancellationToken);
        db.Sessions.Remove(Copy(row));
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { }
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
    string? Device = null, bool OrganizationPicker = false)
{
    public override string ToString()
    {
        return "Dashboard session [REDACTED]";
    }
}

public sealed record DashboardSessionInfo(string Id, bool Current, string Device, DateTimeOffset ExpiresAt);
