using System.Net;
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

        var cancellationToken = deadline.Token;
        var name = "Organization";
        return await OptionalPanel.LoadAsync(http, "organization checkout", async () =>
        {
            var draft = await PendingOrganizationAsync(session.UserId, cancellationToken);
            if (draft is null || http.Request.Path == "/billing/checkout/return" && http.Request.Query["session_id"] != draft.CheckoutId)
            {
                return new OrganizationCheckoutCompletion();
            }

            name = draft.Name;
            try
            {
                return new OrganizationCheckoutCompletion(await CompletePendingOrganizationAsync(session, http, cancellationToken));
            }
            catch (Exception error) when (error is OrganizationCheckoutRefusedException or AccountAccessException { Code: "organization_deleted" })
            {
                var refunded = await CancelDraftPaymentAsync(draft, cancellationToken);
                return await RefuseOrganizationAsync(http, draft, error.Message + (refunded ? " Your payment was refunded." : ""), cancellationToken);
            }
            catch (StripeException error) when (PermanentStripeFailure(error))
            {
                var refunded = await CancelDraftPaymentAsync(draft, cancellationToken);
                return await RefuseOrganizationAsync(http, draft, "Organization checkout is unavailable. Start again." + (refunded ? " Your payment was refunded." : ""), cancellationToken);
            }
        }, () => new OrganizationCheckoutCompletion(Notice: name + " isn't ready yet.", Retry: true));
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

    private async Task RemovePendingOrganizationAsync(PendingOrganizationCheckout draft, CancellationToken cancellationToken)
    {
        if (!await storage.RemoveAsync(draft, cancellationToken)
            && await PendingOrganizationAsync(draft.UserId, cancellationToken) is { } current && current.DraftId == draft.DraftId
            && !await storage.RemoveAsync(current, cancellationToken))
        {
            throw new AccountAccessException("Checkout changed concurrently. Retry organization setup.", 409);
        }
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

    private async Task<OrganizationCheckoutCompletion> RefuseOrganizationAsync(HttpContext http, PendingOrganizationCheckout draft, string message, CancellationToken cancellationToken)
    {
        await OrganizationCheckoutNavigation.RefusedAsync(http, draft.UserId, message, cancellationToken);
        await RemovePendingOrganizationAsync(draft, cancellationToken);
        return new OrganizationCheckoutCompletion(Notice: message);
    }

    internal static bool MissingStripeObject(StripeException error) =>
        error.HttpStatusCode == HttpStatusCode.NotFound || error.StripeError?.Code == "resource_missing";

    private static bool PermanentStripeFailure(StripeException error) =>
        (int)error.HttpStatusCode is >= 400 and < 500 and not (408 or 409 or 429)
        && (MissingStripeObject(error) || error.StripeError?.Type == "invalid_request_error");

    internal static bool TransientStripeFailure(StripeException error) =>
        (int)error.HttpStatusCode is 0 or 408 or 409 or 429 or >= 500;

    internal async Task<bool> CancelPendingOrganizationAsync(string userId, string? draftId, CancellationToken cancellationToken)
    {
        if (await PendingOrganizationAsync(userId, cancellationToken) is not { } draft || draftId is not null && draft.DraftId != draftId)
        {
            return false;
        }

        var refunded = await CancelDraftPaymentAsync(draft, cancellationToken);
        await RemovePendingOrganizationAsync(draft, cancellationToken);
        return refunded;
    }
}
