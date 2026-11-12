using System.Net;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Stripe;
using TerraScale.Dashboard.Platform.Http;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Billing;

internal static class OrganizationCheckoutNavigation
{
    private const string NoticeCookie = "__Host-organization-checkout-notice";

    public static bool IsNavigation(HttpContext http) =>
        HttpMethods.IsGet(http.Request.Method) && !http.Request.Path.StartsWithSegments("/api")
        && !http.Request.Path.StartsWithSegments("/mcp") && http.Request.Path != "/billing/checkout/return"
        && http.Request.Headers["Datastar-Request"] != "true"
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
        await using var db = await storage.CreateDbContextAsync(cancellationToken);
        var drafts = await db.PendingOrganizationCheckouts.AsNoTracking()
            .Where(row => row.UserId == session.UserId).OrderBy(row => row.State == OrganizationCheckoutStates.RefundPending).ThenBy(row => row.CreatedAt).ToListAsync(cancellationToken);
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

        await OptionalPanel.RunAsync(http, "organization checkout metadata", async () =>
        {
            var context = await accounts.GetContextAsync(session, cancellationToken);
            var owned = context.Organizations.Where(org => org.Kind == "business" && org.OwnerUserId == session.UserId).Select(org => org.Id).ToArray();
            var bindings = await db.Billing.AsNoTracking().Where(row => owned.Contains(row.OrganizationId) && row.Kind == "stripe"
                && row.ErasureId == null && row.State == "active" && row.RequestId.Length == 26).ToListAsync(cancellationToken);
            foreach (var binding in bindings)
            {
                await OptionalPanel.RunAsync(http, "organization checkout metadata", async () =>
                {
                    try
                    {
                        var client = await ClientAsync(cancellationToken);
                        var subscription = await client.V1.Subscriptions.GetAsync(binding.SubscriptionId, new() { Expand = ["customer"] }, cancellationToken: cancellationToken);
                        var metadata = new Dictionary<string, string> { ["organization_id"] = binding.OrganizationId };
                        if (subscription.Metadata.GetValueOrDefault("organization_id") != binding.OrganizationId)
                        {
                            await client.V1.Subscriptions.UpdateAsync(binding.SubscriptionId, new() { Metadata = metadata },
                                Idempotent("organization-subscription-" + binding.RequestId), cancellationToken);
                        }
                        if (subscription.Customer?.Metadata.GetValueOrDefault("organization_id") != binding.OrganizationId)
                        {
                            await client.V1.Customers.UpdateAsync(binding.CustomerId, new() { Metadata = metadata },
                                Idempotent("organization-owner-" + binding.RequestId), cancellationToken);
                        }
                    }
                    catch (StripeException)
                    {
                        telemetry.ReportOrganizationMetadataFailure();
                        throw;
                    }
                });
            }
        });
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
        await using var db = await storage.CreateDbContextAsync(cancellationToken);
        return await db.PendingOrganizationCheckouts.AsNoTracking()
            .SingleOrDefaultAsync(row => row.UserId == userId && row.CheckoutId == checkoutId, cancellationToken);
    }

    internal async Task PrepareOrganizationAccountDeletionAsync(SessionRecord session, HttpContext http, CancellationToken cancellationToken)
    {
        await using var db = await storage.CreateDbContextAsync(cancellationToken);
        var drafts = await db.PendingOrganizationCheckouts.AsNoTracking().Where(row => row.UserId == session.UserId).ToListAsync(cancellationToken);
        foreach (var draft in drafts)
        {
            await CancelPendingOrganizationAsync(session, http, draft.DraftId, "Organization checkout canceled.", cancellationToken);
        }
        if (await db.PendingOrganizationCheckouts.AnyAsync(row => row.UserId == session.UserId, cancellationToken))
        {
            throw new IOException("Organization checkout cleanup is pending.");
        }
    }

    private async Task<PendingOrganizationCheckout?> ReadPendingOrganizationAsync(string draftId, CancellationToken cancellationToken)
    {
        await using var db = await storage.CreateDbContextAsync(cancellationToken);
        return await db.PendingOrganizationCheckouts.AsNoTracking().SingleOrDefaultAsync(row => row.DraftId == draftId, cancellationToken);
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
                await using var db = await storage.CreateDbContextAsync(cancellationToken);
                var checkout = await db.StripeCheckouts.AsNoTracking().SingleOrDefaultAsync(row => row.RequestId == draftId, cancellationToken);
                var context = await accounts.GetContextAsync(session, cancellationToken);
                if (checkout is not null && context.Organizations.Any(org => org.Id == checkout.OrganizationId && org.OwnerUserId == session.UserId))
                {
                    const string message = "The organization has been created. Checkout cannot be canceled.";
                    await OrganizationCheckoutNavigation.RefusedAsync(http, session.UserId, message, cancellationToken);
                    return new OrganizationCheckoutCompletion(checkout.OrganizationId, message);
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

            try
            {
                var refunded = await CancelDraftPaymentAsync(draft, cancellationToken);
                var message = draft.RefusalReason + (refunded ? " Your payment was refunded." : "");
                await using var db = await storage.CreateDbContextAsync(cancellationToken);
                var current = await db.PendingOrganizationCheckouts.SingleOrDefaultAsync(row => row.DraftId == draftId, cancellationToken);
                if (current is null)
                {
                    return new OrganizationCheckoutCompletion();
                }
                if (current.State == OrganizationCheckoutStates.Creating)
                {
                    continue;
                }
                db.Remove(current);
                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateConcurrencyException)
                {
                    continue;
                }

                await OrganizationCheckoutNavigation.RefusedAsync(http, session.UserId, message, cancellationToken);
                return new OrganizationCheckoutCompletion(Notice: message);
            }
            catch (OrganizationRefundUnsettledException error)
            {
                var notice = await RecordUnsettledRefundAsync(draftId, error.Status, cancellationToken);
                if (notice)
                {
                    var message = draft.RefusalReason + " Your refund is " + error.Status + ". You can start a new checkout.";
                    await OrganizationCheckoutNavigation.RefusedAsync(http, session.UserId, message, cancellationToken);
                    return new OrganizationCheckoutCompletion(Notice: message);
                }
                return new OrganizationCheckoutCompletion();
            }
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
