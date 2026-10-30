using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.TerraBase.Contracts;
using TerraScale.TerraBase.Contracts.Documents;

namespace TerraScale.Dashboard.Platform.Identity
{
    public sealed class DashboardSessions(IDashboardState state, IDashboardIdentity identity, IAuthGateway auth, IAuthRefreshGateway refresh, TimeProvider time, ILogger<DashboardSessions> logger)
    {
        internal const string Cookie = "ts-dashboard-session";

        public async Task StartAsync(HttpContext context, SessionRecord record)
        {
            if (context.Request.Cookies.ContainsKey(Cookie))
            {
                await EndAsync(context);
            }

            record = record with { SessionExpiresAt = time.GetUtcNow().AddHours(12), RefreshLease = "", RefreshLeaseUntil = null };
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var credentials = await identity.ReadAsync(context.RequestAborted);
            var encrypted = Seal(record, credentials.SessionEncryptionKey);
            var id = Key(token);
            await AccountEnrollment.RememberAsync(state, record.UserId, "sessions", id, context.RequestAborted);
            await AccountEnrollment.RememberAsync(state, record.UserId, "session_indexes", Key(record.UserId), context.RequestAborted);
            await state.PutAsync("sessions", id, JsonSerializer.SerializeToElement(encrypted), TerraBasePrecondition.Absent, context.RequestAborted);
            try { await UpdateIndexAsync(record.UserId, entries => entries[id] = record.SessionExpiresAt!.Value, context.RequestAborted); }
            catch
            {
                // No cookie was sent yet. Remove a partial enrollment even when
                // the originating browser has disconnected.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await DeleteStrictAsync("sessions", id, cleanup.Token); }
                catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
                { context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("DashboardSessions").PartialSessionCleanupFailed(error.GetType().Name); }
                throw;
            }
            await AccountEnrollment.ThrowIfErasedAsync(state, record.UserId, context.RequestAborted);
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

        // Starts alongside(session) while ts-auth confirms the session is still active; its result is used
        // only once that check passes. Each ts-auth call alongside makes is authorized by the same token.
        public async Task<(SessionRecord Session, T Value)?> ReadWithAsync<T>(HttpContext context, Func<SessionRecord, Task<T>> alongside)
        {
            var token = context.Request.Cookies[Cookie];
            if (token is not { Length: 64 } || token.Any(character => !char.IsAsciiHexDigitLower(character)))
            {
                return null;
            }

            Task<T>? work = null;
            var record = await ResolveAsync("sessions", Key(token), context.RequestAborted, alongside: session => work = alongside(session));
            if (record is null)
            {
                Clear(context);
                return null;
            }
            return (record, await work!);
        }

        public async Task<SessionRecord?> ReadForPrivilegeAsync(HttpContext context)
        {
            var token = context.Request.Cookies[Cookie];
            if (token is not { Length: 64 } || token.Any(character => !char.IsAsciiHexDigitLower(character)))
            {
                return null;
            }

            var id = Key(token);
            // Refuse an unavailable or inactive issuer before spending a valid
            // refresh token merely to check an operator role.
            var current = await ResolveAsync("sessions", id, context.RequestAborted);
            if (current is null) { Clear(context); return null; }
            var record = await ResolveAsync("sessions", id, context.RequestAborted, forceRefresh: true, initialAccessToken: current.AccessToken);
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

        // A PAT authenticates its hashed bearer/index before dereferencing this ID.
        // It must obtain a fresh ts-auth grant so its rotating token family is not
        // copied from an already persisted browser session.
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

            var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var credentials = await identity.ReadAsync(cancellationToken);
            record = record with { SessionExpiresAt = expiresAt, RefreshLease = "", RefreshLeaseUntil = null };
            await AccountEnrollment.RememberAsync(state, record.UserId, "auth_credentials", id, cancellationToken);
            await state.PutAsync("auth_credentials", id, JsonSerializer.SerializeToElement(Seal(record, credentials.SessionEncryptionKey)), TerraBasePrecondition.Absent, cancellationToken);
            await AccountEnrollment.ThrowIfErasedAsync(state, record.UserId, cancellationToken);
            return id;
        }

        public Task<SessionRecord?> ReadCredentialAsync(string id, CancellationToken cancellationToken)
        {
            return id is { Length: 64 } && id.All(char.IsAsciiHexDigitLower) ? ResolveAsync("auth_credentials", id, cancellationToken) : Task.FromResult<SessionRecord?>(null);
        }

        public async Task DeleteCredentialAsync(string id, CancellationToken cancellationToken)
        {
            if (id is not { Length: 64 } || !id.All(char.IsAsciiHexDigitLower))
            {
                return;
            }

            await DeleteStrictAsync("auth_credentials", id, cancellationToken);
        }

        // Completed auth deletions need no active grant, but every stored credential
        // must still belong to the proven subject before it can be removed.
        public async Task DeleteCredentialAsync(string id, string subject, CancellationToken cancellationToken)
        {
            if (id is not { Length: 64 } || !id.All(char.IsAsciiHexDigitLower))
            {
                throw new IOException("Credential identity is invalid.");
            }

            var document = await state.GetAsync("auth_credentials", id, cancellationToken);
            if (document is null)
            {
                return;
            }

            if (document.Id != id || document.State != "value")
            {
                throw new IOException("Credential identity is invalid.");
            }

            var credentials = await identity.ReadAsync(cancellationToken);
            var record = Open(document.Document.Deserialize<SealedSession>() ?? throw new IOException("Dashboard credential is invalid."), credentials.SessionEncryptionKey);
            if (record.UserId != subject)
            {
                throw new IOException("Credential ownership is invalid.");
            }

            await state.DeleteAsync("auth_credentials", id, document.Version, cancellationToken);
        }

        // Erasure replay uses retained references, independently of active issuer grants
        // and mutable indexes. Password-change revocation continues to use RevokeUserAsync.
        public async Task CleanEnrollmentsAsync(string userId, CancellationToken cancellationToken)
        {
            foreach (var reference in await AccountEnrollment.ListAsync(state, userId, cancellationToken))
            {
                if (reference.Collection is not ("sessions" or "auth_credentials"))
                {
                    continue;
                }

                var document = await state.GetAsync(reference.Collection, reference.RecordId, cancellationToken);
                if (document is null)
                {
                    continue;
                }

                if (document.Id != reference.RecordId || document.State != "value")
                {
                    throw new IOException("Enrollment identity is invalid.");
                }

                var credentials = await identity.ReadAsync(cancellationToken);
                var record = Open(document.Document.Deserialize<SealedSession>() ?? throw new IOException("Dashboard credential is invalid."), credentials.SessionEncryptionKey);
                if (record.UserId != userId)
                {
                    throw new IOException("Enrollment ownership is invalid.");
                }

                await state.DeleteAsync(reference.Collection, reference.RecordId, document.Version, cancellationToken);
            }
            await AccountEnrollment.CleanAsync(state, userId, cancellationToken);
        }

        public async Task<IReadOnlyList<DashboardSessionInfo>> ListAsync(HttpContext context, SessionRecord session)
        {
            var index = await state.GetAsync("session_indexes", Key(session.UserId), context.RequestAborted);
            if (index is null)
            {
                return [];
            }

            if (index.State != "value")
            {
                throw new IOException("Session index has a causal conflict.");
            }

            var values = index.Document.Deserialize<SessionIndex>() ?? throw new IOException("Session index is invalid.");
            if (values.UserId != session.UserId || values.Entries is null)
            {
                throw new IOException("Session index identity is invalid.");
            }

            var current = context.Request.Cookies[Cookie] is { Length: 64 } token && token.All(char.IsAsciiHexDigitLower) ? Key(token) : "";
            var credentials = await identity.ReadAsync(context.RequestAborted);
            var result = new List<DashboardSessionInfo>();
            var stale = new List<string>();
            foreach (var (id, expires) in values.Entries)
            {
                if (id.Length != 64 || !id.All(char.IsAsciiHexDigitLower)) { stale.Add(id); continue; }
                if (expires <= time.GetUtcNow()) { await DeleteStrictAsync("sessions", id, context.RequestAborted); stale.Add(id); continue; }
                var document = await state.GetAsync("sessions", id, context.RequestAborted);
                if (document is null) { stale.Add(id); continue; }
                if (document.State != "value")
                {
                    throw new IOException("Dashboard session has a causal conflict.");
                }

                var record = Open(document.Document.Deserialize<SealedSession>() ?? throw new IOException("Dashboard session is invalid."), credentials.SessionEncryptionKey);
                if (record.UserId != session.UserId)
                {
                    throw new IOException("Session index ownership is invalid.");
                }

                var expiry = record.SessionExpiresAt ?? record.ExpiresAt;
                if (expiry <= time.GetUtcNow()) { await DeleteStrictAsync("sessions", id, context.RequestAborted); stale.Add(id); continue; }
                result.Add(new(id, id == current, expiry));
            }
            if (stale.Count != 0)
            {
                await UpdateIndexAsync(session.UserId, entries => { foreach (var id in stale) { entries.Remove(id); } }, context.RequestAborted);
            }

            return [.. result.OrderByDescending(item => item.ExpiresAt).ThenBy(item => item.Id, StringComparer.Ordinal)];
        }

        private async Task<SessionRecord?> ResolveAsync(string collection, string id, CancellationToken cancellationToken, bool forceRefresh = false, string? initialAccessToken = null,
            Func<SessionRecord, Task>? alongside = null)
        {
            var credentials = await identity.ReadAsync(cancellationToken);
            var waitDeadline = System.Diagnostics.Stopwatch.StartNew();
            // A privileged request keeps the token it already validated. Another
            // replica may persist its replacement between that validation and this
            // read; that completed rotation also satisfies this request.
            string? accessOnEntry = initialAccessToken;
            while (true)
            {
                var document = await state.GetAsync(collection, id, cancellationToken);
                if (document is null)
                {
                    return null;
                }

                if (document.State != "value")
                {
                    throw new IOException("Dashboard session has a causal conflict.");
                }

                var record = Open(document.Document.Deserialize<SealedSession>() ?? throw new IOException("Dashboard session is invalid."), credentials.SessionEncryptionKey);
                accessOnEntry ??= record.AccessToken;
                var now = time.GetUtcNow();
                if ((record.SessionExpiresAt ?? record.ExpiresAt) <= now)
                {
                    await RemoveAsync(collection, document, "expired", cancellationToken);
                    return null;
                }
                // Privileged callers need a grant minted after their first read.
                // Waiters share a rotation that began after they saw the old token;
                // a later caller must acquire the same single-use lease again.
                if (record.ExpiresAt <= now || forceRefresh && record.AccessToken == accessOnEntry)
                {
                    if (string.IsNullOrWhiteSpace(record.RefreshToken))
                    {
                        if (forceRefresh && record.ExpiresAt > now)
                        {
                            throw new IOException("Fresh platform authorization is unavailable.");
                        }

                        await RemoveAsync(collection, document, "no refresh token", cancellationToken);
                        return null;
                    }
                    if (record.RefreshLease.Length != 0)
                    {
                        // A replica disappeared after potentially rotating the token.
                        // Starting a second rotation could revoke the token family.
                        if (record.RefreshLeaseUntil is null || record.RefreshLeaseUntil <= now) { await RemoveAsync(collection, document, "abandoned refresh lease", cancellationToken); return null; }
                        if (waitDeadline.Elapsed > TimeSpan.FromSeconds(20))
                        {
                            throw new TimeoutException("Session refresh is still in progress.");
                        }

                        await Task.Delay(100, cancellationToken);
                        continue;
                    }
                    // Once the lease is written the rotation must finish even if the caller disconnects: an abandoned
                    // lease expires into a deleted credential, so a client timeout would sign a PAT out for good.
                    using var rotation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var rotating = rotation.Token;
                    var lease = record with { RefreshLease = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), RefreshLeaseUntil = now.AddMinutes(1) };
                    try
                    {
                        await state.PutAsync(collection, id, JsonSerializer.SerializeToElement(Seal(lease, credentials.SessionEncryptionKey)), TerraBasePrecondition.Matches(document.Version), rotating);
                    }
                    catch (HttpRequestException error) when (error.StatusCode is System.Net.HttpStatusCode.PreconditionFailed or System.Net.HttpStatusCode.Conflict)
                    { continue; }
                    SessionRecord refreshed;
                    try { refreshed = await refresh.RefreshAsync(lease, rotating); }
                    catch (SignInRejectedException)
                    {
                        var refused = await state.GetAsync(collection, id, rotating);
                        if (refused is not null)
                        {
                            await RemoveAsync(collection, refused, "refresh rejected by ts-auth", rotating);
                        }

                        return null;
                    }
                    catch (Exception error) when (error is not (SignInRejectedException or RefreshNotStartedException))
                    {
                        // The lease stays until it expires; the next request after that replays the refresh once.
                        logger.RefreshOfAStoredTsAuth(collection,
                            error.GetType().Name, (error as HttpRequestException)?.StatusCode, (error as TerraScale.Dashboard.Platform.TsAuth.TsAuthException)?.ErrorCode ?? "none");
                        throw;
                    }
                    catch (RefreshNotStartedException)
                    {
                        // ts-auth never read the secret, so the session keeps it and
                        // survives an auth restart instead of expiring with the lease.
                        var unchanged = await state.GetAsync(collection, id, rotating);
                        if (unchanged is { State: "value" } &&
                            Open(unchanged.Document.Deserialize<SealedSession>() ?? throw new IOException("Dashboard session is invalid."), credentials.SessionEncryptionKey).RefreshLease == lease.RefreshLease)
                        {
                            try
                            {
                                await state.PutAsync(collection, id, JsonSerializer.SerializeToElement(Seal(record, credentials.SessionEncryptionKey)), TerraBasePrecondition.Matches(unchanged.Version), rotating);
                            }
                            catch (HttpRequestException error) when (error.StatusCode is System.Net.HttpStatusCode.PreconditionFailed or System.Net.HttpStatusCode.Conflict) { }
                        }
                        throw;
                    }
                    // A failed request keeps its lease until expiry; a refresh whose
                    // response was lost is never blindly replayed by another replica.
                    if (refreshed.UserId != record.UserId || refreshed.Email != record.Email || refreshed.ExpiresAt <= now ||
                        forceRefresh && refreshed.AccessToken == accessOnEntry)
                    {
                        throw new IOException("Refreshed session identity is invalid.");
                    }

                    refreshed = refreshed with { SessionExpiresAt = record.SessionExpiresAt, RefreshLease = "", RefreshLeaseUntil = null };
                    // ts-auth has rotated the secret, so losing this write would end the grant for good (the old secret is
                    // never replayed). Retry transient state failures; a write whose answer was lost is recognized on re-read.
                    for (var attempt = 0; ; attempt++)
                    {
                        try
                        {
                            var latest = await state.GetAsync(collection, id, rotating);
                            if (latest is null)
                            {
                                return null; // A concurrent logout wins.
                            }

                            if (latest.State != "value")
                            {
                                throw new IOException("Dashboard session has a causal conflict.");
                            }

                            var current = Open(latest.Document.Deserialize<SealedSession>() ?? throw new IOException("Dashboard session is invalid."), credentials.SessionEncryptionKey);
                            if (attempt > 0 && current.AccessToken == refreshed.AccessToken && current.RefreshLease.Length == 0)
                            {
                                break;
                            }

                            if (current.RefreshLease != lease.RefreshLease)
                            {
                                throw new IOException("Session refresh ownership changed.");
                            }

                            try
                            {
                                await state.PutAsync(collection, id, JsonSerializer.SerializeToElement(Seal(refreshed, credentials.SessionEncryptionKey)), TerraBasePrecondition.Matches(latest.Version), rotating);
                            }
                            catch (HttpRequestException error) when (error.StatusCode is System.Net.HttpStatusCode.PreconditionFailed or System.Net.HttpStatusCode.Conflict)
                            {
                                if (attempt is 0 or >= 5)
                                {
                                    return null;
                                }

                                continue; // An earlier uncertain write may have landed; the re-read decides.
                            }
                            break;
                        }
                        catch (Exception error) when (attempt < 5 && (error is TimeoutException or HttpRequestException { StatusCode: null or >= System.Net.HttpStatusCode.InternalServerError or System.Net.HttpStatusCode.TooManyRequests } ||
                                                                      error is OperationCanceledException && !rotating.IsCancellationRequested))
                        {
                            logger.SavingARefreshedTsAuthGrant(collection, error.GetType().Name);
                            await Task.Delay(TimeSpan.FromMilliseconds(500 << attempt), rotating);
                        }
                    }
                    record = refreshed;
                }
                var active = auth.IsActiveAsync(record, cancellationToken);
                var work = alongside?.Invoke(record);
                if (!await active)
                {
                    // Work for a session ts-auth no longer accepts is discarded, failures included.
                    if (work is not null)
                    {
                        _ = work.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
                    }

                    var rejected = await state.GetAsync(collection, id, cancellationToken);
                    if (rejected is not null)
                    {
                        if (rejected.State != "value")
                        {
                            throw new IOException("Dashboard session has a causal conflict.");
                        }

                        var latestRecord = Open(rejected.Document.Deserialize<SealedSession>() ?? throw new IOException("Dashboard session is invalid."), credentials.SessionEncryptionKey);
                        if (latestRecord.AccessToken != record.AccessToken)
                        {
                            continue;
                        }
                        // Access can expire while the issuer checks it. Reenter the
                        // existing refresh lease; only a valid new grant can recover
                        // the request, and issuer revocation still refuses refresh.
                        if (record.ExpiresAt <= time.GetUtcNow())
                        {
                            continue;
                        }

                        await RemoveAsync(collection, rejected, "inactive at ts-auth", cancellationToken);
                    }
                    return null;
                }
                return record;
            }
        }

        public async Task EndAsync(HttpContext context)
        {
            if (context.Request.Cookies[Cookie] is { Length: 64 } token && token.All(char.IsAsciiHexDigitLower))
            {
                var id = Key(token);
                var document = await state.GetAsync("sessions", id, context.RequestAborted);
                string? subject = null;
                if (document is not null)
                {
                    if (document.State != "value")
                    {
                        throw new IOException("Dashboard session has a causal conflict.");
                    }

                    var credentials = await identity.ReadAsync(context.RequestAborted);
                    subject = Open(document.Document.Deserialize<SealedSession>() ?? throw new IOException("Dashboard session is invalid."), credentials.SessionEncryptionKey).UserId;
                }
                await DeleteStrictAsync("sessions", id, context.RequestAborted);
                if (subject is not null)
                {
                    try { await UpdateIndexAsync(subject, entries => entries.Remove(id), context.RequestAborted); }
                    catch (Exception error) when (error is HttpRequestException or IOException or JsonException)
                    { context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("DashboardSessions").RevokedSessionIndexCleanupFailed(error.GetType().Name); }
                }
            }
            Clear(context);
        }

        private static void Clear(HttpContext context)
        {
            context.Response.Cookies.Delete(Cookie);
            Csrf.Bind(context, null);
        }

        public async Task RevokeUserAsync(string subject, CancellationToken cancellationToken)
        {
            // A password change first revokes the user at ts-auth. Delete each
            // indexed browser credential with CAS so an in-flight refresh cannot
            // restore it after revocation.
            var document = await state.GetAsync("session_indexes", Key(subject), cancellationToken);
            if (document is null)
            {
                return;
            }

            if (document.State != "value")
            {
                throw new IOException("Session index has a causal conflict.");
            }

            var index = document.Document.Deserialize<SessionIndex>() ?? throw new IOException("Session index is invalid.");
            if (index.UserId != subject || index.Entries is null)
            {
                throw new IOException("Session index identity is invalid.");
            }

            var credentials = await identity.ReadAsync(cancellationToken);
            foreach (var id in index.Entries.Keys)
            {
                if (id.Length != 64 || !id.All(char.IsAsciiHexDigitLower))
                {
                    throw new IOException("Session index is invalid.");
                }

                var session = await state.GetAsync("sessions", id, cancellationToken);
                if (session is null)
                {
                    continue;
                }

                if (session.State != "value")
                {
                    throw new IOException("Dashboard session has a causal conflict.");
                }

                var record = Open(session.Document.Deserialize<SealedSession>() ?? throw new IOException("Dashboard session is invalid."), credentials.SessionEncryptionKey);
                if (record.UserId != subject)
                {
                    throw new IOException("Session index ownership is invalid.");
                }

                await DeleteStrictAsync("sessions", id, cancellationToken);
            }
            await UpdateIndexAsync(subject, entries => { foreach (var id in index.Entries.Keys) { entries.Remove(id); } }, cancellationToken);
        }

        private async Task UpdateIndexAsync(string subject, Action<Dictionary<string, DateTimeOffset>> update, CancellationToken cancellationToken)
        {
            var id = Key(subject);
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var document = await state.GetAsync("session_indexes", id, cancellationToken);
                if (document is not null && document.State != "value")
                {
                    throw new IOException("Session index has a causal conflict.");
                }

                var index = document?.Document.Deserialize<SessionIndex>() ?? new(subject, []);
                if (index.UserId != subject || index.Entries is null)
                {
                    throw new IOException("Session index identity is invalid.");
                }

                foreach (var expired in index.Entries.Where(item => item.Value <= time.GetUtcNow()).Select(item => item.Key).ToArray())
                {
                    if (expired.Length != 64 || !expired.All(char.IsAsciiHexDigitLower))
                    {
                        throw new IOException("Session index is invalid.");
                    }

                    await DeleteStrictAsync("sessions", expired, cancellationToken);
                    index.Entries.Remove(expired);
                }
                update(index.Entries);
                if (index.Entries.Count > 1000)
                {
                    throw new IOException("Too many active dashboard sessions.");
                }

                try
                {
                    if (index.Entries.Count == 0)
                    {
                        if (document is not null)
                        {
                            await state.DeleteAsync("session_indexes", id, document.Version, cancellationToken);
                        }

                        return;
                    }
                    await state.PutAsync("session_indexes", id, JsonSerializer.SerializeToElement(index),
                        document is null ? TerraBasePrecondition.Absent : TerraBasePrecondition.Matches(document.Version), cancellationToken);
                    return;
                }
                catch (HttpRequestException error) when (attempt < 3 && error.StatusCode is System.Net.HttpStatusCode.PreconditionFailed or System.Net.HttpStatusCode.Conflict) { }
            }
        }

        private async Task DeleteStrictAsync(string collection, string id, CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var document = await state.GetAsync(collection, id, cancellationToken);
                if (document is null)
                {
                    return;
                }

                try { await state.DeleteAsync(collection, document.Id, document.Version, cancellationToken); return; }
                catch (HttpRequestException error) when (attempt < 3 && error.StatusCode is System.Net.HttpStatusCode.PreconditionFailed or System.Net.HttpStatusCode.Conflict) { }
                catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.NotFound) { return; }
            }
        }

        // Every drop of a stored grant is logged so a signed-out session or PAT can be traced to its cause.
        private async Task RemoveAsync(string collection, TerraBaseDocument document, string reason, CancellationToken cancellationToken)
        {
            logger.DroppedStoredGrant(reason == "expired" ? LogLevel.Information : LogLevel.Warning, collection, reason);
            try { await state.DeleteAsync(collection, document.Id, document.Version, cancellationToken); }
            catch (HttpRequestException error) when (error.StatusCode is System.Net.HttpStatusCode.PreconditionFailed or System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.NotFound) { }
        }

        private static string Key(string token)
        {
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        }

        private sealed record SessionIndex(string UserId, Dictionary<string, DateTimeOffset> Entries);

        private static SealedSession Seal(SessionRecord record, string key)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[bytes.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(Convert.FromBase64String(key), 16);
            try
            {
                aes.Encrypt(nonce, bytes, cipher, tag, "TerraScale.Dashboard/session/v1"u8);
                return new(Convert.ToBase64String(nonce), Convert.ToBase64String(cipher), Convert.ToBase64String(tag));
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }

        private static SessionRecord Open(SealedSession value, string key)
        {
            try
            {
                var cipher = Convert.FromBase64String(value.Ciphertext);
                var plain = new byte[cipher.Length];
                using var aes = new AesGcm(Convert.FromBase64String(key), 16);
                try
                {
                    aes.Decrypt(Convert.FromBase64String(value.Nonce), cipher, Convert.FromBase64String(value.Tag), plain, "TerraScale.Dashboard/session/v1"u8);
                    var record = JsonSerializer.Deserialize<SessionRecord>(plain) ?? throw new IOException("Dashboard session is invalid.");
                    if (string.IsNullOrWhiteSpace(record.UserId) || string.IsNullOrWhiteSpace(record.Email) ||
                        string.IsNullOrWhiteSpace(record.AccessToken) || record.RefreshLease is null)
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
    }

    public sealed record SessionRecord(string UserId, string Name, string Email, string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt,
        DateTimeOffset? SessionExpiresAt = null, string RefreshLease = "", DateTimeOffset? RefreshLeaseUntil = null, string? IdToken = null)
    {
        public override string ToString()
        {
            return "Dashboard session [REDACTED]";
        }
    }

    public sealed record SealedSession(string Nonce, string Ciphertext, string Tag);
    public sealed record DashboardSessionInfo(string Id, bool Current, DateTimeOffset ExpiresAt);
}
