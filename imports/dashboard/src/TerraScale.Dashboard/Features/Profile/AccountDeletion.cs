using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Shared.UI;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Profile;

// ts-auth deletes the user, its sessions, tokens, memberships and the organizations only
// the user belongs to, including project/database erasure; the dashboard then drops
// its own records for the scope confirmed by ts-auth.
// ts-auth fences that scope atomically; paid plans end in Stripe, without a refund, only for
// organizations it proves deleted, so a membership change can never cancel a shared plan.
public static partial class AccountDeletion
{
    private const string Operations = "account_deletions";
    internal const string Cookie = "ts-dashboard-deletion";
    private sealed record Operation(SessionRecord Session, string[] Organizations, string Receipt, string Authorization, DateTimeOffset ExpiresAt)
    {
        public override string ToString() => "Authorized deletion [REDACTED]";
    }
    private sealed record Envelope(string Key, string UserId, SealedSession Payload);

    public static async Task<IResult> DeleteAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, ITsAuthPlatform platform, IDashboardState state, AccessTokens tokens, StripeBilling billing, IDashboardIdentity identity)
    {
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        if (!StepUpFlow.IsResuming(http) && !Csrf.Validate(http, form)) return Results.StatusCode(403);
        if (await ReadOperationAsync(http, state, identity) is { } pending)
            return await AdvanceAsync(http, sessions, platform, state, tokens, billing, pending);
        var context = await AccountRequest.RequireAsync(http, sessions, accounts);
        if (context is null) return Results.Unauthorized();
        var cancellationToken = http.RequestAborted;
        List<string> sole = [];
        var persisted = false;
        var confirmed = false;
        try
        {
            var typed = form["email"].ToString().Trim();
            if (typed.Length == 0 || !typed.Equals(context.Account.Email.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new AccountAccessException("Type your account email to confirm deletion.", 422);
            foreach (var org in context.Organizations)
            {
                var members = await platform.ListMembersAsync(context.Session, org.Id, cancellationToken);
                if (members.Any(member => member.UserId != context.UserId))
                {
                    // Checked before any subscription is canceled; ts-auth enforces the same rule below.
                    if (members.Any(member => member.UserId == context.UserId && member.Role == "owner") &&
                        !members.Any(member => member.UserId != context.UserId && member.Role == "owner"))
                        throw new AccountAccessException("Transfer ownership of your shared organizations before deleting your account.", 409);
                    continue;
                }
                sole.Add(org.Id);
            }
            var intent = StepUpFlow.Intent(http, context, "delete_account");
            if (intent is null)
                return await StepUpFlow.StartAsync(http, context, "delete_account", "ts-dashboard",
                    Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)));
            if (intent.Receipt is null || intent.Audience != "ts-dashboard") throw StepUpFlow.Required();
            // No vendor or data effects precede successful consumption. A lost answer
            // requires another hosted proof; the pending intent never becomes an operation.
            var authorization = await StepUpFlow.ConsumeAsync(http, context, intent);
            if (string.IsNullOrEmpty(authorization)) throw StepUpFlow.Required();
            var operation = new Operation(context.Session, [.. sole], intent.Receipt, authorization,
                http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow().AddDays(15));
            await WriteOperationAsync(http, state, identity, operation);
            persisted = true;
            try
            {
                if (!await platform.DeleteAccountAsync(context.Session, operation.Receipt, operation.Authorization, cancellationToken))
                    return HttpRedirects.SeeOther("/account-deletion");
            }
            catch (AccountAccessException error) when (error.Code == "last_owner")
            { throw new AccountAccessException("Transfer ownership of your shared organizations before deleting your account.", 409); }
            // Dashboard records go only after ts-auth confirms; a 401/404 can mean an expired token or a ts-auth without the route.
            confirmed = true;
            return await CompleteAsync(http, sessions, platform, state, tokens, billing, operation);
        }
        catch (AccountAccessException) when (confirmed) { return Retry(http); }
        catch (Exception error) when (persisted && Transient(error, http)) { return Retry(http); }
        catch (AccountAccessException error)
        {
            if (error.StatusCode == 409 && await ReadOperationAsync(http, state, identity) is { } refused)
            {
                try { _ = await platform.AccountDeletionCompletedAsync(refused.Receipt, cancellationToken); }
                catch (AccountAccessException status) when (status.Code == "deletion_refused")
                { await DropAsync(state, Operations, Key(http), cancellationToken); http.Response.Cookies.Delete(Cookie); }
            }
            var view = ProfileView.Create(new(ShellModel.For(http, context, "Profile"), "danger", Error: error.Message,
                Tokens: await tokens.ListAsync(context, cancellationToken), Sessions: await sessions.ListAsync(http, context.Session)));
            view.StatusCode = error.StatusCode;
            return view;
        }
    }

    public static async Task<IResult> ContinueAsync(HttpContext http, DashboardSessions sessions, ITsAuthPlatform platform, IDashboardState state, AccessTokens tokens, StripeBilling billing, IDashboardIdentity identity)
    {
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        if (!Csrf.Validate(http, form)) return Results.StatusCode(403);
        return await ReadOperationAsync(http, state, identity) is { } operation
            ? await AdvanceAsync(http, sessions, platform, state, tokens, billing, operation) : Results.Unauthorized();
    }

    private static async Task<IResult> AdvanceAsync(HttpContext http, DashboardSessions sessions, ITsAuthPlatform platform, IDashboardState state, AccessTokens tokens, StripeBilling billing, Operation operation)
    {
        try
        {
            bool completed;
            try { completed = await platform.AccountDeletionCompletedAsync(operation.Receipt, http.RequestAborted); }
            // An unknown receipt has no accepted durable auth job. Its saved grant
            // may have been revoked since confirmation; never spend it on Stripe.
            catch (AccountAccessException error) when (error.StatusCode == 404)
            {
                await DropAsync(state, Operations, Key(http), http.RequestAborted);
                http.Response.Cookies.Delete(Cookie);
                return Results.Problem(StepUpFlow.Required().Message, statusCode: 403,
                    extensions: new Dictionary<string, object?> { ["code"] = "step_up_required" });
            }
            return completed ? await CompleteAsync(http, sessions, platform, state, tokens, billing, operation) : HttpRedirects.SeeOther("/account-deletion");
        }
        catch (AccountAccessException error)
        {
            if (error.Code != "deletion_refused") return Retry(http);
            await DropAsync(state, Operations, Key(http), http.RequestAborted);
            http.Response.Cookies.Delete(Cookie);
            return Results.Text(error.Message + " Dashboard records have been retained. Return to your profile to resolve the issue.", statusCode: error.StatusCode);
        }
        catch (Exception error) when (Transient(error, http)) { return Retry(http); }
    }

    private static IResult Retry(HttpContext http)
    {
        _ = int.TryParse(http.Request.Form["_retry"], out var attempt);
        return HttpRedirects.SeeOther("/account-deletion?retry=" + Math.Min(5, Math.Clamp(attempt, 0, 5) + 1));
    }

    private static async Task<IResult> CompleteAsync(HttpContext http, DashboardSessions sessions, ITsAuthPlatform platform, IDashboardState state, AccessTokens tokens, StripeBilling? billing, Operation operation)
    {
        var cancellationToken = http.RequestAborted;
        foreach (var orgId in operation.Organizations)
        {
            // A sole organization can become shared before ts-auth fences it. Only its own deletion proof permits cleanup.
            try { if (!await platform.AccountDeletionOrganizationCompletedAsync(operation.Receipt, orgId, cancellationToken)) continue; }
            catch (AccountAccessException error) when (error.StatusCode == 404) { continue; }
            // The billing binding stays for DeletionReconciler, which ends the plan from the same proof
            // until it succeeds and only then fences billing; this attempt just ends it without waiting.
            if (billing is not null)
                try { await billing.EndSubscriptionAsync(orgId, cancellationToken, completedDeletion: true); }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { }
            await AccountService.FenceOrganizationDeletionAsync(state, orgId, AccountService.NewId(), cancellationToken);
        }
        await AccountService.FenceDeletionAsync(state, operation.Session.UserId, AccountService.NewId(), cancellationToken);
        await tokens.ForgetAllAsync(operation.Session.UserId, cancellationToken);
        await sessions.RevokeUserAsync(operation.Session.UserId, cancellationToken);
        await sessions.EndAsync(http);
        await DropAsync(state, Operations, Key(http), cancellationToken);
        http.Response.Cookies.Delete(Cookie);
        http.Request.QueryString = QueryString.Empty;
        return HttpRedirects.SeeOther("/account-deleted");
    }

    // Possession of the original opaque dashboard cookie continues only this previously authorized operation.
    // It never authenticates a user or grants access to any other dashboard resource.
    private static string Key(HttpContext http)
    {
        var cookie = http.Request.Cookies[Cookie] ?? http.Request.Cookies["ts-dashboard-session"];
        if (cookie is not { Length: 64 } || !cookie.All(char.IsAsciiHexDigitLower)) throw new AccountAccessException("Sign in to request deletion.", 401);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(cookie)));
    }

    private static async Task WriteOperationAsync(HttpContext http, IDashboardState state, IDashboardIdentity identity, Operation operation)
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
            await state.PutAsync(Operations, key, JsonSerializer.SerializeToElement(new Envelope(key, operation.Session.UserId,
                new SealedSession(Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag)))), TerraBasePrecondition.Absent, http.RequestAborted);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        http.Response.Cookies.Append(Cookie, http.Request.Cookies[Cookie] ?? http.Request.Cookies["ts-dashboard-session"]!,
            new CookieOptions { HttpOnly = true, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Strict, Path = "/" });
    }

    private static async Task<Operation?> ReadOperationAsync(HttpContext http, IDashboardState state, IDashboardIdentity identity)
    {
        var cookie = http.Request.Cookies[Cookie] ?? http.Request.Cookies["ts-dashboard-session"];
        if (cookie is not { Length: 64 } || !cookie.All(char.IsAsciiHexDigitLower)) return null;
        var key = Key(http);
        var document = await state.GetAsync(Operations, key, http.RequestAborted);
        if (document is null) return null;
        if (document.State != "value") throw new IOException("Deletion operation has a causal conflict.");
        var envelope = document.Document.Deserialize<Envelope>() ?? throw new IOException("Deletion operation is invalid.");
        if (envelope.Key != key) throw new IOException("Deletion operation identity is invalid.");
        return await OpenAsync(envelope, identity, http.RequestAborted);
    }

    public static async Task CleanOperationsAsync(IDashboardState state, IDashboardIdentity identity, string subject, CancellationToken cancellationToken)
    {
        foreach (var document in await state.ListAsync(Operations, cancellationToken))
        {
            var envelope = AccountService.Read<Envelope>(document);
            if (envelope.UserId != subject) continue;
            var operation = await OpenAsync(envelope, identity, cancellationToken);
            if (operation.Session.UserId != subject) throw new IOException("Deletion operation ownership changed.");
            var current = await state.GetAsync(Operations, envelope.Key, cancellationToken);
            if (current is null) continue;
            if (current.Document.GetRawText() != document.Document.GetRawText()) throw new IOException("Deletion operation changed concurrently.");
            await state.DeleteAsync(Operations, envelope.Key, current.Version, cancellationToken);
        }
    }

    private static async Task<Operation> OpenAsync(Envelope envelope, IDashboardIdentity identity, CancellationToken cancellationToken)
    {
        if (envelope.Key is not { Length: 64 } || !envelope.Key.All(char.IsAsciiHexDigitLower) || envelope.Payload is null)
            throw new IOException("Deletion operation scope is invalid.");
        var sealedValue = envelope.Payload;
        var ciphertext = Convert.FromBase64String(sealedValue.Ciphertext);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(Convert.FromBase64String((await identity.ReadAsync(cancellationToken)).SessionEncryptionKey), 16);
            aes.Decrypt(Convert.FromBase64String(sealedValue.Nonce), ciphertext, Convert.FromBase64String(sealedValue.Tag), plaintext, Encoding.UTF8.GetBytes(Operations + envelope.Key));
            var operation = JsonSerializer.Deserialize<Operation>(plaintext) ?? throw new IOException("Deletion operation is invalid.");
            if (operation.Session is null || operation.Session.UserId != envelope.UserId || string.IsNullOrEmpty(operation.Authorization)) throw new IOException("Deletion operation ownership is invalid.");
            return operation;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static async Task DropAsync(IDashboardState state, string collection, string id, CancellationToken cancellationToken)
    {
        if (await state.GetAsync(collection, id, cancellationToken) is { } document)
            await state.DeleteAsync(collection, id, document.Version, cancellationToken);
    }

    private static bool Transient(Exception error, HttpContext http) => error is HttpRequestException or IOException or TimeoutException ||
        error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested;
}
