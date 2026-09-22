using System.Net.Http.Headers;
using System.Text.Json;
using TerraScale.Dashboard.Features.Payments.Models;

namespace TerraScale.Dashboard.Features.Payments.Services;

/// <summary>The Stripe calls the dashboard depends on. Keeping the surface
/// narrow lets fast tests inject a deterministic provider double.</summary>
public interface IStripeBillingClient
{
    Task<StripeCustomer> CreateCustomerAsync(string email, string organizationId, string idempotencyKey, CancellationToken ct);

    Task<StripeCheckoutSession> CreateCheckoutSessionAsync(StripeCheckoutRequest request, CancellationToken ct);

    Task<StripeCheckoutSession?> GetCheckoutSessionAsync(string sessionId, CancellationToken ct);

    Task<StripePortalSession> CreatePortalSessionAsync(string customerId, Uri returnUrl, string idempotencyKey, CancellationToken ct);

    Task<StripeSubscription?> GetSubscriptionAsync(string subscriptionId, CancellationToken ct);
}

/// <summary>Typed Stripe REST client for the hosted Checkout, Billing Portal,
/// Customer, and Subscription resources. Form bodies use Stripe's bracket
/// notation; every mutation carries a caller-owned idempotency key.</summary>
public sealed class StripeBillingClient(HttpClient http, StripePaymentOptions options) : IStripeBillingClient
{
    public Task<StripeCustomer> CreateCustomerAsync(string email, string organizationId, string idempotencyKey, CancellationToken ct) =>
        SendAsync(
            "v1/customers",
            new Dictionary<string, string>
            {
                ["email"] = email,
                ["metadata[organization_id]"] = organizationId,
            },
            idempotencyKey,
            document => new StripeCustomer(Required(document, "id")),
            ct);

    public Task<StripeCheckoutSession> CreateCheckoutSessionAsync(StripeCheckoutRequest request, CancellationToken ct) =>
        SendAsync(
            "v1/checkout/sessions",
            new Dictionary<string, string>
            {
                ["mode"] = "subscription",
                ["customer"] = request.CustomerId,
                ["client_reference_id"] = request.OrganizationId,
                ["line_items[0][price]"] = request.PriceId,
                ["line_items[0][quantity]"] = "1",
                ["success_url"] = request.SuccessUrl.ToString(),
                ["cancel_url"] = request.CancelUrl.ToString(),
                ["metadata[organization_id]"] = request.OrganizationId,
                ["metadata[organization_name]"] = request.OrganizationName,
                ["metadata[plan_key]"] = request.PlanKey,
                ["metadata[interval]"] = request.Interval,
                ["subscription_data[metadata][organization_id]"] = request.OrganizationId,
                ["subscription_data[metadata][plan_key]"] = request.PlanKey,
            },
            request.IdempotencyKey,
            ReadCheckoutSession,
            ct);

    public async Task<StripeCheckoutSession?> GetCheckoutSessionAsync(string sessionId, CancellationToken ct)
    {
        using var response = await SendRawAsync(HttpMethod.Get, $"v1/checkout/sessions/{Uri.EscapeDataString(sessionId)}", null, null, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        using var document = await ReadDocumentAsync(response, ct);
        return ReadCheckoutSession(document.RootElement);
    }

    public Task<StripePortalSession> CreatePortalSessionAsync(string customerId, Uri returnUrl, string idempotencyKey, CancellationToken ct) =>
        SendAsync(
            "v1/billing_portal/sessions",
            new Dictionary<string, string>
            {
                ["customer"] = customerId,
                ["return_url"] = returnUrl.ToString(),
            },
            idempotencyKey,
            document => new StripePortalSession(Required(document, "id"), Required(document, "url")),
            ct);

    public async Task<StripeSubscription?> GetSubscriptionAsync(string subscriptionId, CancellationToken ct)
    {
        using var response = await SendRawAsync(HttpMethod.Get, $"v1/subscriptions/{Uri.EscapeDataString(subscriptionId)}", null, null, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        using var document = await ReadDocumentAsync(response, ct);
        return ReadSubscription(document.RootElement);
    }

    private async Task<T> SendAsync<T>(
        string path,
        IReadOnlyDictionary<string, string> form,
        string? idempotencyKey,
        Func<JsonElement, T> read,
        CancellationToken ct)
    {
        using var response = await SendRawAsync(HttpMethod.Post, path, new FormUrlEncodedContent(form), idempotencyKey, ct);
        using var document = await ReadDocumentAsync(response, ct);
        return read(document.RootElement);
    }

    private async Task<HttpResponseMessage> SendRawAsync(
        HttpMethod method,
        string path,
        HttpContent? content,
        string? idempotencyKey,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.SecretKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (options.ApiVersion is { Length: > 0 } apiVersion)
        {
            request.Headers.TryAddWithoutValidation("Stripe-Version", apiVersion);
        }

        if (idempotencyKey is { Length: > 0 })
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static async Task<JsonDocument> ReadDocumentAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new StripeApiException(ErrorMessage(body, response.StatusCode), (int)response.StatusCode);
        }

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new StripeApiException("Stripe returned a response that was not valid JSON.", (int)response.StatusCode);
        }
    }

    private static string ErrorMessage(string body, System.Net.HttpStatusCode status)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString()!;
            }
        }
        catch (JsonException)
        {
            // Fall through to the generic message.
        }

        return $"Stripe rejected the request with HTTP {(int)status}.";
    }

    private static StripeCheckoutSession ReadCheckoutSession(JsonElement element) => new(
        Required(element, "id"),
        Optional(element, "url"),
        Optional(element, "customer"),
        Optional(element, "subscription"),
        Optional(element, "status") ?? "open",
        Optional(element, "payment_status"),
        Optional(element, "client_reference_id"),
        Optional(element, "client_secret"));

    private static StripeSubscription ReadSubscription(JsonElement element)
    {
        string? priceId = null;
        if (element.TryGetProperty("items", out var items)
            && items.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array
            && data.GetArrayLength() > 0)
        {
            var first = data[0];
            if (first.TryGetProperty("price", out var price))
            {
                priceId = Optional(price, "id");
            }
        }

        DateTimeOffset? periodEnd = null;
        if (element.TryGetProperty("current_period_end", out var period)
            && period.ValueKind == JsonValueKind.Number
            && period.TryGetInt64(out var seconds))
        {
            periodEnd = DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element.TryGetProperty("metadata", out var metadataElement) && metadataElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadataElement.EnumerateObject())
            {
                metadata[property.Name] = property.Value.GetString() ?? "";
            }
        }

        return new StripeSubscription(
            Required(element, "id"),
            Optional(element, "status") ?? "unknown",
            Optional(element, "customer"),
            priceId,
            periodEnd,
            metadata);
    }

    private static string Required(JsonElement element, string name) =>
        Optional(element, name) ?? throw new StripeApiException($"Stripe response omitted \"{name}\".", 502);

    private static string? Optional(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
