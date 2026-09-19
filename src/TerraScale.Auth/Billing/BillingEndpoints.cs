namespace TerraScale.Auth.Billing;

using System.Globalization;
using System.Text.Json.Serialization;
using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.DotNet;

/// <summary>Billing feature endpoints: plan status, Stripe checkout/portal, the
/// signature-verified webhook receiver, and the hosted billing pages.</summary>
public static class BillingEndpoints
{
    /// <summary>Maps Billing endpoints.</summary>
    public static void Map(WebApplication app)
    {
        // TODO(users owner): gate user creation in UserEndpoints (POST /v1/{slug}/users and the
        // sign-up UI) on Quota.AllowsSignUpAsync(store, instanceId) → 403 "quota_exceeded" when
        // false. This module owns the check but not that file; until wired, the enforce-check
        // endpoint below is the integration seam.
        MapManagement(app);
        MapWebhook(app);
        MapUi(app);
    }

    private static void MapManagement(WebApplication app)
    {
        app.MapGet("/v1/{slug}/billing", async (
            string slug, HttpRequest request, InstanceResolver resolver, AuthStore store, CancellationToken ct) =>
        {
            if (await resolver.ResolveAsync(request, ct) is not { } context || context.Instance.Slug != slug)
                return ApiResults.Unauthorized();

            var state = await BillingState.CurrentAsync(store, context.Instance.Id, ct);
            // Display-only count: nothing past the quota matters, so the walk stops at quota+1.
            var userCount = await BillingState.CountUsersAsync(store, context.Instance.Id, state.Quota + 1, ct);
            return Results.Ok(new
            {
                plan = state.Plan,
                status = state.Status,
                stripe_customer_id = context.Instance.StripeCustomerId ?? state.Subscription?.StripeCustomerId,
                stripe_subscription_id = state.Subscription?.StripeSubscriptionId,
                current_period_end = state.Subscription?.CurrentPeriodEnd,
                user_count = userCount,
                quota = state.Quota,
            });
        });

        app.MapPost("/v1/{slug}/billing/checkout", async (
            string slug, HttpRequest request, InstanceResolver resolver, AuthOptions options, CancellationToken ct) =>
        {
            if (await resolver.ResolveAsync(request, ct) is not { } context || context.Instance.Slug != slug)
                return ApiResults.Unauthorized();

            var (body, failure) = await ReadJsonAsync<CheckoutBody>(request, ct);
            if (failure is not null)
                return failure;
            if (body?.Plan is not (Plans.Pro or Plans.Enterprise))
                return ApiResults.BadRequest("invalid_plan", "plan must be \"pro\" or \"enterprise\"");

            var priceId = body.Plan == Plans.Pro ? options.PricePro : options.PriceEnterprise;
            if (options.StripeSecretKey is null || priceId is null)
                return ApiResults.Error(503, "billing_not_configured", "Stripe billing is not configured for this deployment");

            var origin = options.PublicOrigin.ToString().TrimEnd('/');
            JsonElement session;
            try
            {
                session = await StripeClient.PostAsync(options, "/v1/checkout/sessions", new Dictionary<string, string>
                {
                    ["mode"] = "subscription",
                    ["line_items[0][price]"] = priceId,
                    ["line_items[0][quantity]"] = "1",
                    ["success_url"] = $"{origin}/ui/{slug}/billing/return?status=success",
                    ["cancel_url"] = $"{origin}/ui/{slug}/billing/return?status=cancel",
                    ["client_reference_id"] = context.Instance.Id,
                    ["metadata[instance_id]"] = context.Instance.Id,
                }, ct);
            }
            catch (StripeApiException exception)
            {
                return ApiResults.Error(502, "stripe_error", $"Stripe rejected the checkout session request (HTTP {exception.StatusCode}).");
            }

            // Stripe returns url today; the session id is the fallback for flows without one.
            var url = Str(session, "url") ?? Str(session, "id");
            return Results.Ok(new { checkout_url = url });
        });

        app.MapPost("/v1/{slug}/billing/portal", async (
            string slug, HttpRequest request, InstanceResolver resolver, AuthOptions options, AuthStore store, CancellationToken ct) =>
        {
            if (await resolver.ResolveAsync(request, ct) is not { } context || context.Instance.Slug != slug)
                return ApiResults.Unauthorized();

            if (options.StripeSecretKey is null)
                return ApiResults.Error(503, "billing_not_configured", "Stripe billing is not configured for this deployment");
            if (context.Instance.StripeCustomerId is not { } customer)
                return ApiResults.BadRequest("no_customer", "this instance has no Stripe customer yet; complete a checkout first");

            var origin = options.PublicOrigin.ToString().TrimEnd('/');
            JsonElement portal;
            try
            {
                portal = await StripeClient.PostAsync(options, "/v1/billing_portal/sessions", new Dictionary<string, string>
                {
                    ["customer"] = customer,
                    ["return_url"] = $"{origin}/ui/{slug}/account",
                }, ct);
            }
            catch (StripeApiException exception)
            {
                return ApiResults.Error(502, "stripe_error", $"Stripe rejected the portal session request (HTTP {exception.StatusCode}).");
            }
            return Results.Ok(new { portal_url = Str(portal, "url") });
        });

        // The quota gate as a management endpoint: the integration seam for tests and for
        // other modules until UserEndpoints wires Quota.AllowsSignUpAsync into user creation.
        app.MapPost("/v1/{slug}/billing/enforce-check", async (
            string slug, HttpRequest request, InstanceResolver resolver, AuthStore store, CancellationToken ct) =>
        {
            if (await resolver.ResolveAsync(request, ct) is not { } context || context.Instance.Slug != slug)
                return ApiResults.Unauthorized();

            var (body, failure) = await ReadJsonAsync<EnforceCheckBody>(request, ct);
            if (failure is not null)
                return failure;
            if (body?.CurrentUsers is not { } currentUsers || currentUsers < 0)
                return ApiResults.BadRequest("invalid_request", "current_users must be a non-negative integer");

            var state = await BillingState.CurrentAsync(store, context.Instance.Id, ct);
            return Results.Ok(new { allowed = currentUsers < state.Quota, plan = state.Plan, quota = state.Quota });
        });
    }

    private static void MapWebhook(WebApplication app)
    {
        // No auth: signature-verified, and instance-less (client_reference_id carries the instance).
        app.MapPost("/v1/billing/webhook", (HttpRequest request, AuthOptions options, AuthStore store, TerraScaleClient sdk, CancellationToken ct) =>
            StripeWebhooks.HandleAsync(request, options, store, sdk, ct));
    }

    private static void MapUi(WebApplication app)
    {
        app.MapGet("/ui/{slug}/billing/return", (string slug, string? status) =>
            Ui.Page("Billing", status == "success"
                ? "<h1>Subscription confirmed</h1><p class=\"success\">Your subscription is now active.</p>"
                : "<h1>Checkout canceled</h1><p class=\"error\">The checkout was canceled; nothing was charged.</p>"));

        // Tradeoff: the hosted UI has no operator session yet, so the dashboard is keyed by the
        // management sk_ secret via ?key= — convenient for operators, but query strings leak into
        // logs and browser history; swap for a session cookie once operator sign-in exists.
        app.MapGet("/ui/{slug}/billing", async (string slug, string? key, AuthStore store, CancellationToken ct) =>
        {
            if (key is null
                || await store.FindApiKeyAsync(SecureTokens.Sha256Hex(key), ct) is not { } apiKey
                || apiKey.Revoked
                || await store.GetInstanceAsync(apiKey.InstanceId, ct) is not { } instance
                || instance.Slug != slug)
                return Ui.Page("Billing", "<h1>Not authorized</h1><p class=\"error\">A valid management key is required (?key=sk_...).</p>", status: 401);

            var state = await BillingState.CurrentAsync(store, instance.Id, ct);
            var count = await BillingState.CountUsersAsync(store, instance.Id, state.Quota + 1, ct);
            var customerId = instance.StripeCustomerId ?? state.Subscription?.StripeCustomerId;
            var renews = state.Subscription?.CurrentPeriodEnd is { } end
                ? DateTimeOffset.FromUnixTimeSeconds(end).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : "—";
            return Ui.Page("Billing", $"""
                <h1>Plan: {Ui.Esc(state.Plan)}</h1>
                <div class="list">
                  <div class="row"><span>Status</span><span>{Ui.Esc(state.Status)}</span></div>
                  <div class="row"><span>Users</span><span>{count} of {state.Quota}</span></div>
                  <div class="row"><span>Stripe customer</span><span>{Ui.Esc(customerId ?? "—")}</span></div>
                  <div class="row"><span>Renews</span><span>{renews}</span></div>
                </div>
                """);
        });
    }

    private static async Task<(TBody? Body, IResult? Failure)> ReadJsonAsync<TBody>(HttpRequest request, CancellationToken ct)
        where TBody : class
    {
        try
        {
            var body = await request.ReadFromJsonAsync<TBody>(ct);
            return body is null
                ? (null, ApiResults.BadRequest("invalid_request", "a JSON body is required"))
                : (body, null);
        }
        catch (JsonException)
        {
            return (null, ApiResults.BadRequest("invalid_json", "the request body is not valid JSON"));
        }
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed record CheckoutBody(string? Plan);

    private sealed record EnforceCheckBody([property: JsonPropertyName("current_users")] int? CurrentUsers);
}
