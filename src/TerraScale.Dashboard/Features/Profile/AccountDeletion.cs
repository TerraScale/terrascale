using Dapper;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StripeException = Stripe.StripeException;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Features.Profile
{
    public static partial class AccountDeletion
    {
        private const string DeletionByKey = DashboardSql.AccountDeletions.Select + " WHERE key_hash = @KeyHash";
        private const string DeletionsOf = DashboardSql.AccountDeletions.Select + " WHERE user_id = @UserId";
        private const string RemoveCompleted = "DELETE FROM account_deletions WHERE id IN (SELECT id FROM account_deletions WHERE completed_until <= @Now LIMIT 500)";
        private const string RemoveExpired = """
            DELETE FROM account_deletions WHERE id IN (SELECT id FROM account_deletions
             WHERE completed_until <= @Now OR (completed_until IS NULL AND created_at <= @Expired) LIMIT 500)
            """;
        private const int WipeBatch = 500;
        private static readonly TimeSpan GrantLifetime = TimeSpan.FromDays(15);
        private const string RemoveKey = "DELETE FROM account_deletions WHERE key_hash = @KeyHash";
        private const string Complete = """
            UPDATE account_deletions SET user_id = NULL, nonce = NULL, ciphertext = NULL, tag = NULL, completed_until = @CompletedUntil, version = @Version
            WHERE id = @Id AND version = @OriginalVersion
            """;

        private const string Operations = "account_deletions";
        internal const string Cookie = "__Host-ts-dashboard-deletion";
        private sealed record Operation(SessionRecord Session, string[] Organizations, string Receipt, string Authorization, DateTimeOffset ExpiresAt)
        {
            public override string ToString()
            {
                return "Authorized deletion [REDACTED]";
            }
        }
        private static readonly TimeSpan CompletionKept = TimeSpan.FromDays(15);

        public static async Task<IResult> DeleteAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, ITsAuthPlatform platform, DashboardDb storage, AccessTokens tokens, StripeBilling billing, IDashboardIdentity identity)
        {
            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!StepUpFlow.IsResuming(http) && !Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, "/profile?tab=danger", TypedResults.StatusCode(403));
            }

            if (await ReadOperationAsync(http, storage, identity) is { } pending)
            {
                return await AdvanceAsync(http, sessions, platform, storage, tokens, billing, pending);
            }

            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return TypedResults.Unauthorized();
            }

            var cancellationToken = http.RequestAborted;
            List<string> sole = [];
            var persisted = false;
            var confirmed = false;
            try
            {
                var typed = form["email"].ToString().Trim();
                if (typed.Length == 0 || !typed.Equals(context.Account.Email.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return await RefusedViewAsync(http, sessions, context, tokens, "Type your account email to confirm deletion.", 422, form);
                }

                foreach (var org in context.Organizations)
                {
                    var members = await platform.ListMembersAsync(context.Session, org.Id, cancellationToken);
                    if (members.Any(member => member.UserId != context.UserId))
                    {
                        if (members.Any(member => member.UserId == context.UserId && member.Role == "owner") &&
                            !members.Any(member => member.UserId != context.UserId && member.Role == "owner"))
                        {
                            return await RefusedViewAsync(http, sessions, context, tokens, "Transfer ownership of your shared organizations before deleting your account.", 409, form);
                        }

                        continue;
                    }
                    sole.Add(org.Id);
                }
                var intent = StepUpFlow.Intent(http, context, "delete_account");
                if (intent is null)
                {
                    return await StepUpFlow.StartAsync(http, context, "delete_account", "ts-dashboard",
                        Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)));
                }

                if (intent.Receipt is null || intent.Audience != "ts-dashboard")
                {
                    throw StepUpFlow.Required();
                }
                var authorization = await StepUpFlow.ConsumeAsync(http, context, intent);
                if (string.IsNullOrEmpty(authorization))
                {
                    throw StepUpFlow.Required();
                }

                var operation = new Operation(context.Session, [.. sole], intent.Receipt, authorization,
                    http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow() + GrantLifetime);
                await WriteOperationAsync(http, storage, identity, operation);
                persisted = true;
                operation = await PrepareCheckoutDeletionAsync(http, platform, storage, identity, billing, operation);
                sole = [.. operation.Organizations];
                try
                {
                    if (!await platform.DeleteAccountAsync(context.Session, operation.Receipt, operation.Authorization, cancellationToken))
                    {
                        return HttpRedirects.SeeOther("/account-deletion");
                    }
                }
                catch (AccountAccessException error) when (error.Code == "last_owner")
                { throw new AccountAccessException("Transfer ownership of your shared organizations before deleting your account.", 409); }
                confirmed = true;
                return await CompleteAsync(http, sessions, platform, storage, tokens, billing, operation);
            }
            catch (AccountAccessException) when (confirmed) { return Retry(http); }
            catch (Exception error) when (persisted && Transient(error, http)) { return Retry(http); }
            catch (AccountAccessException error)
            {
                return await RefusedAsync(http, sessions, context, tokens, platform, storage, identity, error, form, cancellationToken);
            }
        }

        private static async Task<IResult> RefusedAsync(HttpContext http, DashboardSessions sessions, AccountContext context, AccessTokens tokens,
            ITsAuthPlatform platform, DashboardDb storage, IDashboardIdentity identity, AccountAccessException error, IFormCollection form, CancellationToken cancellationToken)
        {
            if (error.StatusCode == 409 && await ReadOperationAsync(http, storage, identity) is { } refused)
            {
                try { _ = await platform.AccountDeletionCompletedAsync(refused.Receipt, cancellationToken); }
                catch (AccountAccessException status) when (status.Code == "deletion_refused")
                { await DropAsync(storage, Key(http), cancellationToken); DashboardCookies.Delete(http, Cookie, SameSiteMode.Strict); }
            }
            return await RefusedViewAsync(http, sessions, context, tokens, error.Message, error.StatusCode, form);
        }

        private static async Task<IResult> RefusedViewAsync(HttpContext http, DashboardSessions sessions, AccountContext context, AccessTokens tokens, string message, int statusCode, IFormCollection form)
        {
            var view = ProfileView.Create(new(ShellModel.For(http, context, "Profile"), "danger", Error: message,
                Tokens: await tokens.ListAsync(context, http.RequestAborted), Sessions: await sessions.ListAsync(http, context.Session))
            { DialogId = "delete-account", DraftEmail = form["email"].ToString() });
            view.StatusCode = statusCode;
            return view;
        }

        public static async Task<IResult> ContinueAsync(HttpContext http, DashboardSessions sessions, ITsAuthPlatform platform, DashboardDb storage, AccessTokens tokens, StripeBilling billing, IDashboardIdentity identity)
        {
            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, "/account-deletion", TypedResults.StatusCode(403));
            }

            if (await CompletedAsync(http, storage))
            {
                DashboardCookies.Delete(http, Cookie, SameSiteMode.Strict);
                return HttpRedirects.SeeOther("/account-deleted");
            }

            return await ReadOperationAsync(http, storage, identity) is { } operation
                ? await AdvanceAsync(http, sessions, platform, storage, tokens, billing, operation) : TypedResults.Unauthorized();
        }

        private static async Task<IResult> AdvanceAsync(HttpContext http, DashboardSessions sessions, ITsAuthPlatform platform, DashboardDb storage, AccessTokens tokens, StripeBilling billing, Operation operation)
        {
            try
            {
                bool completed;
                try { completed = await platform.AccountDeletionCompletedAsync(operation.Receipt, http.RequestAborted); }
                catch (AccountAccessException error) when (error.StatusCode == 404)
                {
                    await DropAsync(storage, Key(http), http.RequestAborted);
                    DashboardCookies.Delete(http, Cookie, SameSiteMode.Strict);
                    return TypedResults.Problem(StepUpFlow.Required().Message, statusCode: 403,
                        extensions: new Dictionary<string, object?> { ["code"] = "step_up_required" });
                }
                if (!completed)
                {
                    operation = await PrepareCheckoutDeletionAsync(http, platform, storage,
                        http.RequestServices.GetRequiredService<IDashboardIdentity>(), billing, operation);
                    completed = await platform.DeleteAccountAsync(operation.Session, operation.Receipt, operation.Authorization, http.RequestAborted);
                }
                return completed ? await CompleteAsync(http, sessions, platform, storage, tokens, billing, operation) : HttpRedirects.SeeOther("/account-deletion");
            }
            catch (AccountAccessException error)
            {
                if (error.Code != "deletion_refused")
                {
                    return Retry(http);
                }

                await DropAsync(storage, Key(http), http.RequestAborted);
                DashboardCookies.Delete(http, Cookie, SameSiteMode.Strict);
                return TypedResults.Text(error.Message + " Dashboard records have been retained. Return to your profile to resolve the issue.", statusCode: error.StatusCode);
            }
            catch (Exception error) when (Transient(error, http)) { return Retry(http); }
        }

        private static async Task<Operation> PrepareCheckoutDeletionAsync(HttpContext http, ITsAuthPlatform platform,
            DashboardDb storage, IDashboardIdentity identity, StripeBilling billing, Operation operation)
        {
            await billing.PrepareOrganizationAccountDeletionAsync(operation.Session, http, http.RequestAborted);
            var context = await http.RequestServices.GetRequiredService<AccountService>().GetContextAsync(operation.Session, http.RequestAborted);
            List<string> organizations = [];
            foreach (var organization in context.Organizations)
            {
                var members = await platform.ListMembersAsync(operation.Session, organization.Id, http.RequestAborted);
                if (members.All(member => member.UserId == operation.Session.UserId))
                {
                    organizations.Add(organization.Id);
                }
            }
            operation = operation with { Organizations = [.. organizations] };
            await WriteOperationAsync(http, storage, identity, operation);
            return operation;
        }

        private static IResult Retry(HttpContext http)
        {
            _ = int.TryParse(http.Request.Form["_retry"], out var attempt);
            return HttpRedirects.SeeOther("/account-deletion?retry=" + Math.Min(5, Math.Clamp(attempt, 0, 5) + 1));
        }

        private static async Task<IResult> CompleteAsync(HttpContext http, DashboardSessions sessions, ITsAuthPlatform platform, DashboardDb storage, AccessTokens tokens, StripeBilling? billing, Operation operation)
        {
            var cancellationToken = http.RequestAborted;
            foreach (var orgId in operation.Organizations)
            {
                try
                {
                    if (!await platform.AccountDeletionOrganizationCompletedAsync(operation.Receipt, orgId, cancellationToken))
                    {
                        continue;
                    }
                }
                catch (AccountAccessException error) when (error.StatusCode == 404) { continue; }
                if (billing is not null)
                {
                    try { await billing.EndSubscriptionAsync(orgId, cancellationToken, completedDeletion: true); }
                    catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        var (type, code) = DeletionReconciler.Failure(error);
                        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(AccountDeletion)).AccountDeletionDidNotEndBilling(orgId, type, code);
                    }
                }

                await http.RequestServices.GetRequiredService<OrganizationStore>().FenceAsync(orgId, AccountService.NewId(), cancellationToken);
            }
            await http.RequestServices.GetRequiredService<AccountStore>().FenceAsync(operation.Session.UserId, AccountService.NewId(), cancellationToken);
            await tokens.ForgetAllAsync(operation.Session.UserId, cancellationToken);
            await sessions.RevokeUserAsync(operation.Session.UserId, cancellationToken);
            await sessions.EndAsync(http);
            await DropAsync(storage, Key(http), cancellationToken);
            DashboardCookies.Delete(http, Cookie, SameSiteMode.Strict);
            http.Request.QueryString = QueryString.Empty;
            return HttpRedirects.SeeOther("/account-deleted");
        }

        private static string Key(HttpContext http)
        {
            var cookie = DashboardCookies.Read(http, Cookie) ?? DashboardCookies.Read(http, DashboardSessions.Cookie);
            if (cookie is not { Length: 64 } || !cookie.All(char.IsAsciiHexDigitLower))
            {
                throw new AccountAccessException("Sign in to request deletion.", 401);
            }

            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(cookie)));
        }

        private static async Task WriteOperationAsync(HttpContext http, DashboardDb storage, IDashboardIdentity identity, Operation operation)
        {
            var key = Key(http);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(operation);
            try
            {
                var ciphertext = new byte[bytes.Length];
                var tag = new byte[16];
                using var aes = new AesGcm(Convert.FromBase64String((await identity.ReadAsync(http.RequestAborted)).SessionEncryptionKey), 16);
                aes.Encrypt(nonce, bytes, ciphertext, tag, Encoding.UTF8.GetBytes(Operations + key));
                await using var connection = await storage.OpenAsync(http.RequestAborted);
                var row = await connection.QuerySingleOrDefaultAsync<AccountDeletionRow>(DeletionByKey, new { KeyHash = key });
                if (row is null)
                {
                    await connection.ExecuteAsync(DashboardSql.AccountDeletions.Insert, new AccountDeletionRow
                    {
                        Id = AccountService.NewId(),
                        KeyHash = key,
                        UserId = operation.Session.UserId,
                        Nonce = nonce,
                        Ciphertext = ciphertext,
                        Tag = tag,
                        CreatedAt = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime,
                        Version = AccountService.NewId(),
                    });
                }
                else
                {
                    row.Nonce = nonce;
                    row.Ciphertext = ciphertext;
                    row.Tag = tag;
                    row.OriginalVersion = row.Version;
                    row.Version = AccountService.NewId();
                    DashboardDb.Expect(await connection.ExecuteAsync(DashboardSql.AccountDeletions.Update, row));
                }
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            if ((DashboardCookies.Read(http, Cookie) ?? DashboardCookies.Read(http, DashboardSessions.Cookie)) is { } bound)
            {
                http.Response.Cookies.Append(Cookie, bound, DashboardCookies.Private(http, SameSiteMode.Strict));
            }
        }

        private static async Task<Operation?> ReadOperationAsync(HttpContext http, DashboardDb storage, IDashboardIdentity identity)
        {
            if (await FindAsync(http, storage) is not { CompletedUntil: null } row)
            {
                return null;
            }

            var operation = await OpenAsync(row, identity, http.RequestAborted);
            if (operation.ExpiresAt > http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow())
            {
                return operation;
            }

            await DropAsync(storage, row.KeyHash, http.RequestAborted);
            DashboardCookies.Delete(http, Cookie, SameSiteMode.Strict);
            return null;
        }

        public static async Task WipeExpiredAsync(DashboardDb storage, TimeProvider clock, CancellationToken cancellationToken)
        {
            var now = clock.GetUtcNow();
            for (var batch = 0; batch < 20; batch++)
            {
                await using var connection = await storage.OpenAsync(cancellationToken);
                if (await connection.ExecuteAsync(RemoveExpired, new { Now = now.UtcDateTime, Expired = (now - GrantLifetime).UtcDateTime }) < WipeBatch)
                {
                    return;
                }
            }
        }

        private static async Task<bool> CompletedAsync(HttpContext http, DashboardDb storage)
        {
            return await FindAsync(http, storage) is { CompletedUntil: { } until } && RowSync.At(until) > http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        }

        private static async Task<AccountDeletionRow?> FindAsync(HttpContext http, DashboardDb storage)
        {
            var cookie = DashboardCookies.Read(http, Cookie) ?? DashboardCookies.Read(http, DashboardSessions.Cookie);
            if (cookie is not { Length: 64 } || !cookie.All(char.IsAsciiHexDigitLower))
            {
                return null;
            }

            var key = Key(http);
            await using var connection = await storage.OpenAsync(http.RequestAborted);
            return await connection.QuerySingleOrDefaultAsync<AccountDeletionRow>(DeletionByKey, new { KeyHash = key });
        }

        public static async Task CleanOperationsAsync(DashboardDb storage, IDashboardIdentity identity, string subject, TimeProvider clock,
            CancellationToken cancellationToken)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            await using var connection = await storage.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(RemoveCompleted, new { Now = now });
            foreach (var row in await connection.QueryAsync<AccountDeletionRow>(DeletionsOf, new { UserId = subject }))
            {
                var operation = await OpenAsync(row, identity, cancellationToken);
                if (operation.Session.UserId != subject)
                {
                    throw new IOException("Deletion operation ownership changed.");
                }

                await connection.ExecuteAsync(Complete, new { row.Id, OriginalVersion = row.Version, CompletedUntil = now + CompletionKept, Version = AccountService.NewId() });
            }
        }

        private static async Task<Operation> OpenAsync(AccountDeletionRow row, IDashboardIdentity identity, CancellationToken cancellationToken)
        {
            if (row is not { UserId: { } userId, Nonce: { } nonce, Ciphertext: { } ciphertext, Tag: { } tag })
            {
                throw new IOException("Deletion operation scope is invalid.");
            }

            var plaintext = new byte[ciphertext.Length];
            try
            {
                using var aes = new AesGcm(Convert.FromBase64String((await identity.ReadAsync(cancellationToken)).SessionEncryptionKey), 16);
                aes.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(Operations + row.KeyHash));
                var operation = JsonSerializer.Deserialize<Operation>(plaintext) ?? throw new IOException("Deletion operation is invalid.");
                if (operation.Session is null || operation.Session.UserId != userId || string.IsNullOrEmpty(operation.Authorization))
                {
                    throw new IOException("Deletion operation ownership is invalid.");
                }

                return operation;
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }

        private static async Task DropAsync(DashboardDb storage, string key, CancellationToken cancellationToken)
        {
            await using var connection = await storage.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(RemoveKey, new { KeyHash = key });
        }

        private static bool Transient(Exception error, HttpContext http)
        {
            return error is HttpRequestException or IOException or TimeoutException ||
                error is StripeException stripe && !StripeBilling.MissingStripeObject(stripe) ||
            error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested;
        }
    }
}
