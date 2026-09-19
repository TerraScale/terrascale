namespace TerraScale.Auth.Billing;

using System.Collections.Concurrent;
using System.Text;
using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.DotNet;

/// Inbound Stripe webhook processing: signature verification, event-id idempotency,
/// and the three events that drive billing state.
internal static class StripeWebhooks
{
    private const int MaxProcessedEventIds = 10_000;

    // ponytail: in-memory event-id dedupe, bounded and pruned oldest-first under a lock —
    // covers Stripe's retry window; a restart or a >10k backlog re-processes, which the
    // upsert handlers tolerate (a duplicate audit row is the only effect). Persist ids
    // for exact-once across restarts.
    private static readonly ConcurrentDictionary<string, long> ProcessedEventIds = new();
    private static readonly object PruneGate = new();

    public static async Task<IResult> HandleAsync(
        HttpRequest request, AuthOptions options, AuthStore store, TerraScaleClient sdk, CancellationToken ct)
    {
        if (options.StripeWebhookSecret is null)
            return ApiResults.Error(503, "billing_not_configured", "Stripe webhooks are not configured for this deployment");

        string payload;
        using (var reader = new StreamReader(request.Body, Encoding.UTF8))
            payload = await reader.ReadToEndAsync(ct);

        // Both rejection bodies are non-descriptive on purpose: never say which check failed.
        var verification = StripeWebhookSignatures.Verify(
            options.StripeWebhookSecret, request.Headers["Stripe-Signature"], payload,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if (verification == StripeWebhookVerification.StaleTimestamp)
            return ApiResults.BadRequest("invalid_request", "the webhook could not be accepted");
        if (verification != StripeWebhookVerification.Valid)
            return ApiResults.Unauthorized();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return ApiResults.BadRequest("invalid_json", "the request body is not valid JSON");
        }

        using (document)
        {
            return await DispatchAsync(document.RootElement, store, options, sdk, ct);
        }
    }

    private static async Task<IResult> DispatchAsync(
        JsonElement root, AuthStore store, AuthOptions options, TerraScaleClient sdk, CancellationToken ct)
    {
        var source = Obj(Obj(root, "data"), "object");
        if (Str(root, "id") is not { } eventId)
        {
            await ProcessAsync(root, source, store, options, sdk, ct);
            return Results.Ok();
        }

        if (!ProcessedEventIds.TryAdd(eventId, DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
            return Results.Ok(); // Stripe retry of an already-processed event
        try
        {
            await ProcessAsync(root, source, store, options, sdk, ct);
        }
        catch
        {
            // Processing failed and the 500 will make Stripe retry: release the id so the
            // retry can apply the event instead of being deduped into a silent loss.
            ProcessedEventIds.TryRemove(eventId, out _);
            throw;
        }
        Prune();
        return Results.Ok();
    }

    private static async Task ProcessAsync(
        JsonElement root, JsonElement source, AuthStore store, AuthOptions options, TerraScaleClient sdk, CancellationToken ct)
    {
        switch (source.ValueKind == JsonValueKind.Object ? Str(root, "type") : null)
        {
            case "checkout.session.completed":
                await CheckoutCompletedAsync(store, options, sdk, source, ct);
                break;
            case "customer.subscription.updated":
                await SubscriptionUpdatedAsync(store, options, source, ct);
                break;
            case "customer.subscription.deleted":
                await SubscriptionDeletedAsync(store, source, ct);
                break;
            default:
                break; // unknown event types are acknowledged so Stripe stops retrying
        }
    }

    private static void Prune()
    {
        lock (PruneGate)
        {
            if (ProcessedEventIds.Count <= MaxProcessedEventIds)
                return;
            foreach (var id in ProcessedEventIds.OrderBy(kvp => kvp.Value)
                .Take(ProcessedEventIds.Count - MaxProcessedEventIds)
                .Select(kvp => kvp.Key)
                .ToList())
                ProcessedEventIds.TryRemove(id, out _);
        }
    }

    private static async Task CheckoutCompletedAsync(
        AuthStore store, AuthOptions options, TerraScaleClient sdk, JsonElement session, CancellationToken ct)
    {
        var instanceId = Str(session, "client_reference_id") ?? Str(Obj(session, "metadata"), "instance_id");
        if (instanceId is null || await store.GetInstanceAsync(instanceId, ct) is not { } instance)
            return; // unknown instance: acknowledge so Stripe stops retrying
        if (Str(session, "customer") is not { } customer)
            return;

        var existing = await store.FindSubscriptionByCustomerAsync(customer, ct);
        if (existing is not null && existing.InstanceId != instance.Id)
            return; // the customer belongs to another instance: a foreign pairing (only an
                    // externally-created session can produce it) must not link this instance to
                    // that customer (which would open their billing portal here) or touch their
                    // subscription — acknowledge and leave both sides alone.

        if (instance.StripeCustomerId != customer)
        {
            // AuthStore exposes no instance upsert (shared read-only surface), so mirror its
            // UpsertAsync directly through the SDK client: same database, same collection, plain PUT.
            await sdk.Documents.PutAsync(
                store.Database, "instances", instance.Id, instance with { StripeCustomerId = customer },
                new TerraScaleDocumentMutationOptions(), ct);
        }

        // Resolve the plan from the subscription's price; when the fetch fails, default to pro
        // and let the follow-up customer.subscription.updated correct it.
        var subscriptionId = Str(session, "subscription");
        var plan = Plans.Pro;
        long? periodEnd = null;
        if (subscriptionId is not null)
        {
            try
            {
                var subscription = await StripeClient.GetAsync(options, "/v1/subscriptions/" + subscriptionId, ct);
                plan = Plans.FromPriceId(FirstPriceId(subscription), options) ?? plan;
                periodEnd = Num(subscription, "current_period_end");
            }
            catch (Exception exception) when (exception is StripeApiException or HttpRequestException or TaskCanceledException)
            {
            }
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var updated = existing is { } current
            ? current with
            {
                StripeCustomerId = customer,
                StripeSubscriptionId = subscriptionId ?? existing.StripeSubscriptionId,
                Plan = plan,
                Status = "active",
                CurrentPeriodEnd = periodEnd ?? existing.CurrentPeriodEnd,
                UpdatedAt = now,
            }
            : new Subscription
            {
                Id = "sub_" + SecureTokens.RandomToken(20),
                InstanceId = instance.Id,
                StripeCustomerId = customer,
                StripeSubscriptionId = subscriptionId,
                Plan = plan,
                Status = "active",
                CurrentPeriodEnd = periodEnd,
                CreatedAt = now,
                UpdatedAt = now,
            };
        await store.UpsertSubscriptionAsync(updated, ct);
        await AuditAsync(store, instance.Id, "billing.plan_changed", new()
        {
            ["plan"] = plan,
            ["status"] = "active",
            ["stripe_customer_id"] = customer,
            ["stripe_subscription_id"] = subscriptionId ?? "",
        }, ct);
    }

    private static async Task SubscriptionUpdatedAsync(
        AuthStore store, AuthOptions options, JsonElement subscription, CancellationToken ct)
    {
        if (Str(subscription, "customer") is not { } customer
            || Str(subscription, "status") is not { } rawStatus
            || await store.FindSubscriptionByCustomerAsync(customer, ct) is not { } existing)
            return; // no subscription yet: checkout.session.completed creates it

        var status = rawStatus == "incomplete_expired" ? "canceled" : rawStatus;
        var plan = Plans.FromPriceId(FirstPriceId(subscription), options) ?? existing.Plan;
        var periodEnd = Num(subscription, "current_period_end");
        if (existing.Status == status && existing.Plan == plan && existing.CurrentPeriodEnd == periodEnd)
            return; // nothing transitioned

        await store.UpsertSubscriptionAsync(existing with
        {
            Status = status,
            Plan = plan,
            CurrentPeriodEnd = periodEnd,
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, ct);
        await AuditAsync(store, existing.InstanceId, existing.Plan != plan ? "billing.plan_changed" : "billing.subscription_updated", new()
        {
            ["from_status"] = existing.Status,
            ["to_status"] = status,
            ["plan"] = plan,
        }, ct);
    }

    private static async Task SubscriptionDeletedAsync(AuthStore store, JsonElement subscription, CancellationToken ct)
    {
        if (Str(subscription, "customer") is not { } customer
            || await store.FindSubscriptionByCustomerAsync(customer, ct) is not { } existing
            || existing.Status == "canceled")
            return;

        // The plan stays on the document — "canceled" flags it; period-end semantics are the
        // consumer's call (see BillingState.CurrentAsync).
        await store.UpsertSubscriptionAsync(existing with
        {
            Status = "canceled",
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, ct);
        await AuditAsync(store, existing.InstanceId, "billing.subscription_canceled", new()
        {
            ["plan"] = existing.Plan,
        }, ct);
    }

    private static Task AuditAsync(AuthStore store, string instanceId, string type, Dictionary<string, object> data, CancellationToken ct) =>
        store.CreateAuditEventAsync(new AuditEvent
        {
            Id = "evt_" + SecureTokens.RandomToken(20),
            InstanceId = instanceId,
            Type = type,
            Data = data,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, ct);

    // ---- Small JSON accessors (tolerate missing/mistyped fields) --------------------------

    private static JsonElement Obj(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Object ? value : default;

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Num(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number) ? number : null;

    private static string? FirstPriceId(JsonElement subscription) =>
        subscription.ValueKind == JsonValueKind.Object
        && subscription.TryGetProperty("items", out var items)
        && items.ValueKind == JsonValueKind.Array
        && items.GetArrayLength() > 0
        && items[0].TryGetProperty("price", out var price)
        && price.TryGetProperty("id", out var id)
        && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
}
