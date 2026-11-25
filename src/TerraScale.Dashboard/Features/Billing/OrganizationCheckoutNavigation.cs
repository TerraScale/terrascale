using Dapper;
using System.Data;
using TerraScale.Dashboard.Platform.Datastar;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Stripe;
using TerraScale.Dashboard.Platform.Http;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Errors;

namespace TerraScale.Dashboard.Features.Billing;

internal static class OrganizationCheckoutNavigation
{
    private const string NoticeCookie = "__Host-organization-checkout-notice";

    public static bool IsNavigation(HttpContext http) =>
        HttpMethods.IsGet(http.Request.Method) && !http.Request.Path.StartsWithSegments("/api")
        && !http.Request.Path.StartsWithSegments("/mcp") && http.Request.Path != "/billing/checkout/return"
        && !http.Request.IsDatastarRequest
        && http.Request.Headers.Accept.Any(value => value?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true)
        && (!http.Request.Headers.TryGetValue("Sec-Fetch-Dest", out var destination) || destination == "document");

    public static async Task RefusedAsync(HttpContext http, string userId, string message, CancellationToken cancellationToken)
    {
        var credentials = await http.RequestServices.GetRequiredService<IDashboardIdentity>().ReadAsync(cancellationToken);
        var bytes = Encoding.UTF8.GetBytes(message);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[bytes.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(Convert.FromBase64String(credentials.SessionEncryptionKey), 16);
        aes.Encrypt(nonce, bytes, ciphertext, tag, Encoding.UTF8.GetBytes(NoticeCookie + userId));
        http.Response.Cookies.Append(NoticeCookie, WebEncoders.Base64UrlEncode([.. nonce, .. tag, .. ciphertext]),
            new() { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/", MaxAge = TimeSpan.FromDays(1) });
    }

    public static async Task<string> TakeNoticeAsync(HttpContext http, string userId)
    {
        if (http.Request.Cookies[NoticeCookie] is not { Length: <= 2048 } value)
        {
            return "";
        }

        http.Response.Cookies.Delete(NoticeCookie, new() { Secure = true, Path = "/" });
        var bytes = WebEncoders.Base64UrlDecode(value);
        if (bytes.Length < 28)
        {
            return "";
        }

        var credentials = await http.RequestServices.GetRequiredService<IDashboardIdentity>().ReadAsync(http.RequestAborted);
        var plaintext = new byte[bytes.Length - 28];
        using var aes = new AesGcm(Convert.FromBase64String(credentials.SessionEncryptionKey), 16);
        aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plaintext, Encoding.UTF8.GetBytes(NoticeCookie + userId));
        return Encoding.UTF8.GetString(plaintext);
    }
}

internal sealed record OrganizationCheckoutCompletion(string? OrganizationId = null, string Notice = "", bool Retry = false);

internal sealed class OrganizationCheckoutRefusedException(string message) : IOException(message);

public sealed partial class StripeBilling
{
    private const string Drafts = DashboardSql.PendingOrganizationCheckouts.Select + " WHERE user_id = @UserId";
    private const string DraftsOrdered = Drafts + " ORDER BY state = @RefundPending, created_at";
    private const string DraftCount = "SELECT count(*) FROM pending_organization_checkouts WHERE user_id = @UserId";
    private const string DraftForReturn = Drafts + " AND checkout_id = @CheckoutId";
    private const string DraftById = DashboardSql.PendingOrganizationCheckouts.Select + " WHERE draft_id = @DraftId";
    private const string CheckoutByRequest = DashboardSql.StripeCheckouts.Select + " WHERE request_id = @RequestId";

    internal Task<OrganizationCheckoutCompletion> ResumeOrganizationAsync(SessionRecord session, HttpContext http)
    {
        if (http.Items.TryGetValue(typeof(OrganizationCheckoutCompletion), out var existing))
        {
            return (Task<OrganizationCheckoutCompletion>)existing!;
        }

        var completion = ResumeOrganizationCoreAsync(session, http);
        http.Items[typeof(OrganizationCheckoutCompletion)] = completion;
        return completion;
    }

    private static readonly TimeSpan OrganizationCompletionDeadline = TimeSpan.FromSeconds(5);

    private async Task<OrganizationCheckoutCompletion> ResumeOrganizationCoreAsync(SessionRecord session, HttpContext http)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        if (http.Request.Path != "/billing/checkout/return")
        {
            deadline.CancelAfter(OrganizationCompletionDeadline);
        }

        return await OptionalPanel.LoadAsync(http, "organization checkout",
            () => ResumeOrganizationAttemptsAsync(session, http, deadline.Token),
            () => new OrganizationCheckoutCompletion(Notice: "Organization isn't ready yet.", Retry: true));
    }

    private async Task<OrganizationCheckoutCompletion> ResumeOrganizationAttemptsAsync(SessionRecord session, HttpContext http, CancellationToken cancellationToken)
    {
        var result = new OrganizationCheckoutCompletion();
        List<PendingOrganizationCheckout> drafts;
        await using (var connection = await storage.OpenAsync(cancellationToken))
        {
            drafts = (await connection.QueryAsync<PendingOrganizationCheckout>(DraftsOrdered, new { session.UserId, OrganizationCheckoutStates.RefundPending })).AsList();
        }

        foreach (var draft in drafts)
        {
            if (http.Request.Path == "/billing/checkout/return" && http.Request.Query["session_id"] != draft.CheckoutId)
            {
                continue;
            }

            var completion = await OptionalPanel.LoadAsync(http, "organization checkout", async () =>
            {
                try
                {
                    return new OrganizationCheckoutCompletion(await CompletePendingOrganizationAsync(session, http, draft, cancellationToken));
                }
                catch (Exception error) when (error is OrganizationCheckoutRefusedException or AccountAccessException { Code: "organization_deleted" }
                    || error is StripeException stripe && MissingStripeObject(stripe))
                {
                    var reason = error is StripeException ? "Organization checkout is unavailable. Start again." : error.Message;
                    return await CancelPendingOrganizationAsync(session, http, draft.DraftId, reason, cancellationToken);
                }
            }, () => new OrganizationCheckoutCompletion(Notice: draft.Name + " isn't ready yet.", Retry: true));
            if (completion.OrganizationId is not null || completion.Notice.Length > 0)
            {
                result = completion with { OrganizationId = completion.OrganizationId ?? result.OrganizationId };
            }
        }

        return result;
    }

    internal static async Task<OrganizationCheckoutCompletion?> OrganizationCheckoutNoticeAsync(HttpContext http, string userId)
    {
        var once = await OptionalPanel.LoadAsync(http, "organization checkout notice", () => OrganizationCheckoutNavigation.TakeNoticeAsync(http, userId), () => "");
        if (http.Items.TryGetValue(typeof(OrganizationCheckoutCompletion), out var result))
        {
            var completion = await (Task<OrganizationCheckoutCompletion>)result!;
            if (completion.Notice.Length > 0)
            {
                http.Response.Cookies.Delete("__Host-organization-checkout-notice", new() { Secure = true, Path = "/" });
                return completion;
            }
        }

        return once.Length > 0 ? new(Notice: once) : null;
    }

    private async Task ExpireDraftCheckoutAsync(string checkoutId, CancellationToken cancellationToken)
    {
        var client = await ClientAsync(cancellationToken);
        var checkout = await client.V1.Checkout.Sessions.GetAsync(checkoutId, cancellationToken: cancellationToken);
        if (checkout.Status == "open")
        {
            try
            {
                await client.V1.Checkout.Sessions.ExpireAsync(checkoutId, cancellationToken: cancellationToken);
            }
            catch (StripeException error) when (error.HttpStatusCode == HttpStatusCode.BadRequest)
            {
                if ((await client.V1.Checkout.Sessions.GetAsync(checkoutId, cancellationToken: cancellationToken)).Status == "open")
                {
                    throw;
                }
            }
        }
    }

    internal static bool MissingStripeObject(StripeException error) =>
        error.HttpStatusCode == HttpStatusCode.NotFound || error.StripeError?.Code == "resource_missing";

    internal async Task<PendingOrganizationCheckout?> OrganizationDraftForCancellationAsync(string userId, string draftId, CancellationToken cancellationToken)
    {
        var draft = await ReadPendingOrganizationAsync(draftId, cancellationToken);
        return draft?.UserId == userId ? draft : null;
    }

    internal async Task<PendingOrganizationCheckout?> OrganizationDraftForReturnAsync(string userId, string checkoutId, CancellationToken cancellationToken)
    {
        await using var connection = await storage.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<PendingOrganizationCheckout>(DraftForReturn, new { UserId = userId, CheckoutId = checkoutId });
    }

    internal async Task PrepareOrganizationAccountDeletionAsync(SessionRecord session, HttpContext http, CancellationToken cancellationToken)
    {
        await using var connection = await storage.OpenAsync(cancellationToken);
        var drafts = (await connection.QueryAsync<PendingOrganizationCheckout>(Drafts, new { session.UserId })).AsList();
        foreach (var draft in drafts)
        {
            await CancelPendingOrganizationAsync(session, http, draft.DraftId, "Organization checkout canceled.", cancellationToken);
        }

        if (await connection.ExecuteScalarAsync<long>(DraftCount, new { session.UserId }) > 0)
        {
            throw new IOException("Organization checkout cleanup is pending.");
        }
    }

    private async Task<PendingOrganizationCheckout?> ReadPendingOrganizationAsync(string draftId, CancellationToken cancellationToken)
    {
        await using var connection = await storage.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<PendingOrganizationCheckout>(DraftById, new { DraftId = draftId });
    }

    private async Task<PendingOrganizationCheckout> RecordPaidDraftAsync(PendingOrganizationCheckout draft, CancellationToken cancellationToken)
    {
        while (draft.PaidAt is null)
        {
            draft.PaidAt = time.GetUtcNow().UtcDateTime;
            if (await storage.ReplaceAsync(draft, cancellationToken))
            {
                return draft;
            }
            draft = await ReadPendingOrganizationAsync(draft.DraftId, cancellationToken)
                ?? throw new AccountAccessException("Checkout changed concurrently. Reload organization setup.", 409);
        }
        return draft;
    }

    internal async Task<OrganizationCheckoutCompletion> CancelPendingOrganizationAsync(SessionRecord session, HttpContext http, string draftId,
        string reason, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var draft = await ReadPendingOrganizationAsync(draftId, cancellationToken);
            if (draft is null)
            {
                StripeCheckoutRow? checkout;
                await using (var connection = await storage.OpenAsync(cancellationToken))
                {
                    checkout = await connection.QuerySingleOrDefaultAsync<StripeCheckoutRow>(CheckoutByRequest, new { RequestId = draftId });
                }

                var context = await accounts.GetContextAsync(session, cancellationToken);
                if (checkout is not null && context.Organizations.Any(org => org.Id == checkout.OrganizationId && org.OwnerUserId == session.UserId))
                {
                    const string createdMessage = "The organization has been created. Checkout cannot be canceled.";
                    await OrganizationCheckoutNavigation.RefusedAsync(http, session.UserId, createdMessage, cancellationToken);
                    return new OrganizationCheckoutCompletion(checkout.OrganizationId, createdMessage);
                }
                return new OrganizationCheckoutCompletion();
            }
            if (draft.UserId != session.UserId)
            {
                return new OrganizationCheckoutCompletion();
            }
            if (draft.State == OrganizationCheckoutStates.Creating)
            {
                await OrganizationCheckoutNavigation.RefusedAsync(http, session.UserId, "The organization is being created. Checkout cannot be canceled.", cancellationToken);
                return new OrganizationCheckoutCompletion(await CompleteCreatingOrganizationAsync(session, draft, cancellationToken),
                    "The organization is being created. Checkout cannot be canceled.");
            }
            if (draft.State == OrganizationCheckoutStates.Pending)
            {
                draft.State = OrganizationCheckoutStates.Canceling;
                draft.RefusalReason = reason;
                if (!await storage.ReplaceAsync(draft, cancellationToken))
                {
                    continue;
                }
            }

            var refund = await CancelDraftPaymentAsync(draft, cancellationToken);
            var pendingRefund = refund switch
            {
                bool => null,
                OrganizationRefundPending pending => pending,
            };
            if (pendingRefund is not null)
            {
                var unsettledNotice = await RecordUnsettledRefundAsync(draftId, pendingRefund.Status, cancellationToken);
                if (unsettledNotice)
                {
                    var unsettledMessage = draft.RefusalReason + " Your refund is " + pendingRefund.Status + ". You can start a new checkout.";
                    await OrganizationCheckoutNavigation.RefusedAsync(http, session.UserId, unsettledMessage, cancellationToken);
                    return new OrganizationCheckoutCompletion(Notice: unsettledMessage);
                }
                return new OrganizationCheckoutCompletion();
            }

            var refunded = refund switch { bool canceled => canceled, OrganizationRefundPending => false };
            var message = draft.RefusalReason + (refunded ? " Your payment was refunded." : "");
            var current = await ReadPendingOrganizationAsync(draftId, cancellationToken);
            if (current is null)
            {
                return new OrganizationCheckoutCompletion();
            }
            if (current.State == OrganizationCheckoutStates.Creating)
            {
                continue;
            }
            if (!await storage.RemoveAsync(current, cancellationToken))
            {
                continue;
            }

            await OrganizationCheckoutNavigation.RefusedAsync(http, session.UserId, message, cancellationToken);
            return new OrganizationCheckoutCompletion(Notice: message);
        }
        throw new IOException("Organization cancellation changed concurrently. Retry organization setup.");
    }

    private async Task<bool> RecordUnsettledRefundAsync(string draftId, string status, CancellationToken cancellationToken)
    {
        while (await ReadPendingOrganizationAsync(draftId, cancellationToken) is { } current)
        {
            if (current.State == OrganizationCheckoutStates.Creating)
            {
                throw new AccountAccessException("The organization is being created.", 409);
            }
            if (current.State == OrganizationCheckoutStates.RefundPending && current.RefundStatus == status && current.RefundNoticeAt is not null)
            {
                return false;
            }
            current.State = OrganizationCheckoutStates.RefundPending;
            current.RefundStatus = status;
            current.RefundNoticeAt = time.GetUtcNow().UtcDateTime;
            if (await storage.ReplaceAsync(current, cancellationToken))
            {
                return true;
            }
        }
        return false;
    }
}

