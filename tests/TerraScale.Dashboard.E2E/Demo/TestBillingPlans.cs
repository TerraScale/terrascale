using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using TerraScale.Dashboard.Features.Billing;

namespace TerraScale.Dashboard.E2E.Demo
{
    // Only the E2E suite install this protocol wrapper. All app calls continue
    // through the official Stripe SDK and validate actual provider-shaped facts.
    sealed class TestBillingPlanTransport(HttpMessageHandler underlying) : HttpMessageHandler
    {
        private int cancellationCalls;
        public int CancellationCalls => Volatile.Read(ref cancellationCalls);
        private readonly HttpMessageInvoker vendor = new(underlying, false);
        private readonly ConcurrentDictionary<string, JsonObject> subscriptions = new();
        private readonly ConcurrentDictionary<string, JsonObject> schedules = new();
        private readonly ConcurrentDictionary<string, JsonObject> expiredCheckouts = new();
        private readonly ConcurrentDictionary<string, string> idempotency = new();
        private readonly ConcurrentDictionary<string, string> scenarios = new();
        private readonly ConcurrentDictionary<string, byte> portalDefaults = new();
        private readonly ConcurrentDictionary<string, byte> outages = new();
        private readonly ConcurrentDictionary<string, BillingPlanBarrier> barriers = new();
        private readonly ConcurrentDictionary<string, string> republishedPrices = new();
        private readonly ConcurrentDictionary<string, byte> observedPublishedPrices = new();
        private readonly ConcurrentDictionary<string, (string Customer, string Price)> uncertainUpdates = new();
        private readonly ConcurrentQueue<(string Customer, string Price, string RequestKey)> updateAttempts = new();
        private readonly ConcurrentQueue<(string Customer, string Path)> mutations = new();
        private readonly SemaphoreSlim serial = new(1);
        public void MapRoutes(WebApplication app)
        {
            app.MapPost("/__test/billing/plans/scenario", async (HttpContext http, DashboardSessions sessions, AccountService accounts, StripeBilling billing) =>
            {
                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return Results.Unauthorized();
                }

                var form = await http.Request.ReadFormAsync(http.RequestAborted);
                if (!Csrf.Validate(http, form))
                {
                    return Results.BadRequest();
                }

                if (context.Role != "owner")
                {
                    return Results.StatusCode(403);
                }

                var binding = await billing.ReadBindingAsync(context, http.RequestAborted);
                if (binding?.CustomerId is not { Length: > 0 })
                {
                    return Results.BadRequest();
                }

                var scenario = form["scenario"].ToString();
                if (scenario is not ("confirmed" or "pending" or "offline" or "foreign-customer" or "confirm-payment" or "source-race" or "release-source-race" or "uncertain-upgrade" or "republish-price" or "portal-default" or "reconcile-outage"))
                {
                    return Results.BadRequest();
                }

                await serial.WaitAsync(http.RequestAborted);
                try
                {
                    // The billing portal's "use as default" sets the customer's invoice default, not the subscription's.
                    if (scenario == "portal-default") { portalDefaults[binding.CustomerId] = 0; return Results.NoContent(); }
                    if (scenario == "republish-price")
                    {
                        var admitted = uncertainUpdates.Values.FirstOrDefault(update => update.Customer == binding.CustomerId);
                        if (admitted.Price is null)
                        {
                            return Results.BadRequest();
                        }

                        republishedPrices[admitted.Price] = admitted.Price + "_replacement";
                        scenarios[binding.CustomerId] = "confirmed";
                    }
                    else if (scenario == "confirmed")
                    {
                        foreach (var admitted in uncertainUpdates.Values.Where(update => update.Customer == binding.CustomerId))
                        {
                            republishedPrices.TryRemove(admitted.Price, out _);
                        }

                        scenarios[binding.CustomerId] = "confirmed";
                    }
                    else if (scenario == "source-race")
                    {
                        if (binding.SubscriptionId.Length == 0)
                        {
                            return Results.BadRequest();
                        }

                        barriers[binding.CustomerId] = new();
                    }
                    else if (scenario == "release-source-race")
                    {
                        if (!barriers.TryGetValue(binding.CustomerId, out var barrier) || !barrier.Waiting.Task.IsCompleted)
                        {
                            return Results.BadRequest();
                        }

                        var subscription = await SubscriptionAsync(binding.SubscriptionId, http.RequestAborted);
                        var currentId = subscription["items"]!["data"]![0]!["price"]!["id"]!.GetValue<string>();
                        var nextId = currentId + "_replacement";
                        subscription["items"]!["data"]![0]!["price"] = Price(nextId);
                        barrier.Resume.TrySetResult();
                    }
                    else if (scenario == "confirm-payment")
                    {
                        var subscription = await SubscriptionAsync(binding.SubscriptionId, http.RequestAborted);
                        if (subscription["pending_update"]?["subscription_items"]?[0] is not { } item)
                        {
                            return Results.BadRequest();
                        }

                        subscription["items"]!["data"]![0]!["price"] = item["price"]!.DeepClone();
                        subscription["pending_update"] = null;
                        if (subscription["latest_invoice"] is JsonObject invoice)
                        {
                            invoice["status"] = "paid";
                        }
                    }
                    if (scenario is not ("source-race" or "release-source-race" or "republish-price"))
                    {
                        scenarios[binding.CustomerId] = scenario == "confirm-payment" ? "confirmed" : scenario;
                    }
                }
                finally { serial.Release(); }
                return Results.NoContent();
            });
            // Organization deletion removes the binding's organization, so this reads Stripe's side by its metadata.
            app.MapGet("/__test/billing/plans/subscriptions/{organizationId}", async (HttpContext http, DashboardSessions sessions, AccountService accounts, string organizationId) =>
                await AccountRequest.RequireAsync(http, sessions, accounts) is null ? Results.Unauthorized() : Results.Json(subscriptions.Values
                    .Where(item => item["metadata"]?["organization_id"]?.GetValue<string>() == organizationId).Select(item => item["status"]!.GetValue<string>()).ToArray()));
            app.MapGet("/__test/billing/plans/probe", async (HttpContext http, DashboardSessions sessions, AccountService accounts, StripeBilling billing) =>
            {
                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return Results.Unauthorized();
                }

                var binding = await billing.ReadBindingAsync(context, http.RequestAborted);
                return binding is null ? Results.NotFound() : Results.Json(new
                {
                    mutations = mutations.Where(row => row.Customer == binding.CustomerId).Select(row => row.Path).ToArray(),
                    updateAttempts = updateAttempts.Where(row => row.Customer == binding.CustomerId).Select(row => new { priceId = row.Price, requestKey = row.RequestKey }).ToArray(),
                    publishedReplacementObserved = uncertainUpdates.Values.Where(row => row.Customer == binding.CustomerId)
                        .Any(row => republishedPrices.TryGetValue(row.Price, out var replacement) && observedPublishedPrices.ContainsKey(replacement)),
                    sourceRaceWaiting = barriers.TryGetValue(binding.CustomerId, out var barrier) && barrier.Waiting.Task.IsCompleted && !barrier.Resume.Task.IsCompleted
                });
            });
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host != "api.stripe.com")
            {
                throw new HttpRequestException("Unexpected billing plan fixture origin.");
            }

            var path = request.RequestUri.AbsolutePath;
            if (request.Method == HttpMethod.Delete && path.StartsWith("/v1/subscriptions/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref cancellationCalls);
            }
            // Stripe rejects expand strings with more than four properties.
            if (Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query).Any(item => item.Key.StartsWith("expand[", StringComparison.Ordinal) && item.Value.Any(value => value!.Split('.').Length > 4)))
            {
                return Json(new JsonObject { ["error"] = new JsonObject { ["type"] = "invalid_request_error", ["message"] = "You cannot expand more than 4 levels of a property." } }, HttpStatusCode.BadRequest);
            }

            if (request.Method == HttpMethod.Get && path.StartsWith("/v1/prices/", StringComparison.Ordinal))
            {
                if (request.Headers.Authorization?.Parameter != "sk_test_demo")
                {
                    return Json(new JsonObject { ["error"] = new JsonObject { ["message"] = "Unknown vendor credential." } }, HttpStatusCode.Unauthorized);
                }

                return Json(Price(path["/v1/prices/".Length..]));
            }
            if (request.Method == HttpMethod.Get && path == "/v1/prices" && !republishedPrices.IsEmpty)
            {
                using var response = await vendor.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                var listing = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!;
                foreach (var price in listing["data"]!.AsArray())
                {
                    if (republishedPrices.TryGetValue(price!["id"]!.GetValue<string>(), out var replacement)) { price["id"] = replacement; observedPublishedPrices.TryAdd(replacement, 0); }
                }

                return Json(listing);
            }
            if (request.Method == HttpMethod.Get && path is "/v1/checkout/sessions" or "/v1/subscriptions")
            {
                using var response = await vendor.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                var listing = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!;
                var replacements = path == "/v1/checkout/sessions" ? expiredCheckouts : subscriptions;
                var rows = listing["data"]!.AsArray();
                for (var index = 0; index < rows.Count; index++)
                {
                    if (replacements.TryGetValue(rows[index]!["id"]!.GetValue<string>(), out var current))
                    {
                        rows[index] = current.DeepClone();
                    }
                }

                return Json(listing);
            }
            var isSubscription = path.StartsWith("/v1/subscriptions/", StringComparison.Ordinal);
            var isSchedule = path.StartsWith("/v1/subscription_schedules", StringComparison.Ordinal);
            var isExpire = request.Method == HttpMethod.Post && path.StartsWith("/v1/checkout/sessions/", StringComparison.Ordinal) && path.EndsWith("/expire", StringComparison.Ordinal);
            var checkoutId = path.StartsWith("/v1/checkout/sessions/", StringComparison.Ordinal) ? path.Split('/')[4] : "";
            if (request.Method == HttpMethod.Get && expiredCheckouts.TryGetValue(checkoutId, out var ended))
            {
                return Json(ended.DeepClone());
            }
            // The overage reporter's monthly pending-item interval is not a plan change; the underlying vendor records it.
            var pendingInterval = isSubscription && request.Method == HttpMethod.Post && request.Content is not null &&
                QueryHelpers.ParseQuery(await request.Content.ReadAsStringAsync(cancellationToken)).ContainsKey("pending_invoice_item_interval[interval]");
            if (pendingInterval || !isSubscription && !isSchedule && !isExpire && path != "/v1/invoices/create_preview")
            {
                return await vendor.SendAsync(request, cancellationToken);
            }

            if (request.Headers.Authorization?.Parameter != "sk_test_demo")
            {
                return Json(new JsonObject { ["error"] = new JsonObject { ["message"] = "Unknown vendor credential." } }, HttpStatusCode.Unauthorized);
            }

            BillingPlanBarrier? blocked = null;
            if (isSubscription && request.Method == HttpMethod.Get)
            {
                await serial.WaitAsync(cancellationToken);
                try
                {
                    var subscription = await SubscriptionAsync(path.Split('/')[3], cancellationToken);
                    if (barriers.TryGetValue(subscription["customer"]!.GetValue<string>(), out var barrier) && ++barrier.Reads == 2)
                    { blocked = barrier; barrier.Waiting.TrySetResult(); }
                }
                finally { serial.Release(); }
            }
            if (blocked is not null)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                await blocked.Resume.Task.WaitAsync(deadline.Token);
            }
            await serial.WaitAsync(cancellationToken);
            try
            {
                var form = QueryHelpers.ParseQuery(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
                var key = request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : "";
                if ((request.Method == HttpMethod.Post && !isPreview() || request.Method == HttpMethod.Delete) && key.Length == 0)
                {
                    throw new HttpRequestException("Stripe mutations must retain an idempotency key.");
                }

                if (key.Length > 0)
                {
                    var fingerprint = path + "?" + string.Join('&', form.OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + pair.Value));
                    if (idempotency.TryGetValue(key, out var previous) && previous != fingerprint)
                    {
                        throw new HttpRequestException("An idempotency key was reused for different inputs.");
                    }

                    idempotency[key] = fingerprint;
                }
                if (isExpire)
                {
                    using var get = new HttpRequestMessage(HttpMethod.Get, "https://api.stripe.com/v1/checkout/sessions/" + checkoutId);
                    using var original = await vendor.SendAsync(get, cancellationToken);
                    var checkout = JsonNode.Parse(await original.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
                    if (checkout["status"]!.GetValue<string>() != "open")
                    {
                        return Json(new JsonObject { ["error"] = new JsonObject { ["message"] = "Checkout is no longer open." } }, HttpStatusCode.BadRequest);
                    }

                    checkout["status"] = "expired"; checkout["url"] = null;
                    expiredCheckouts[checkoutId] = checkout;
                    return Json(checkout.DeepClone());
                }
                if (isSubscription)
                {
                    var id = path.Split('/')[3]; var subscription = await SubscriptionAsync(id, cancellationToken);
                    var customer = subscription["customer"]!.GetValue<string>(); var scenario = scenarios.GetValueOrDefault(customer, "confirmed");
                    // reconcile-outage: Stripe accepts the mutation, then reads fail until the scenario changes.
                    if (scenario == "offline" || scenario == "reconcile-outage" && request.Method == HttpMethod.Get && outages.ContainsKey(customer))
                    {
                        return Json(new JsonObject { ["error"] = new JsonObject { ["message"] = "Test-only Stripe outage." } }, HttpStatusCode.ServiceUnavailable);
                    }

                    if (scenario == "foreign-customer") { var foreign = subscription.DeepClone(); foreign["customer"] = "cus_foreign"; return Json(foreign); }
                    if (request.Method == HttpMethod.Post)
                    {
                        mutations.Enqueue((customer, path));
                        if (scenario == "reconcile-outage")
                        {
                            outages[customer] = 0;
                        }
                        else
                        {
                            outages.TryRemove(customer, out _);
                        }

                        if (form.TryGetValue("cancel_at_period_end", out var cancel))
                        {
                            subscription["cancel_at_period_end"] = cancel == "true";
                        }
                        else
                        {
                            if (form.GetValueOrDefault("payment_behavior") != "pending_if_incomplete" || form.GetValueOrDefault("proration_behavior") != "always_invoice" ||
                                form.GetValueOrDefault("items[0][id]") != subscription["items"]!["data"]![0]!["id"]!.GetValue<string>() || form.GetValueOrDefault("items[0][quantity]") != "1")
                            {
                                throw new HttpRequestException("Unexpected live subscription update serialization.");
                            }

                            var price = Price(form.GetValueOrDefault("items[0][price]").ToString());
                            updateAttempts.Enqueue((customer, price["id"]!.GetValue<string>(), key));
                            if (scenario == "uncertain-upgrade")
                            {
                                uncertainUpdates.TryAdd(key, (customer, price["id"]!.GetValue<string>()));
                                throw new HttpRequestException("The test-only vendor admitted the request, but its response was lost.");
                            }
                            if (scenario == "pending")
                            {
                                var pending = subscription["items"]!["data"]![0]!.DeepClone(); pending["price"] = price;
                                subscription["pending_update"] = new JsonObject { ["expires_at"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(), ["subscription_items"] = new JsonArray(pending) };
                                subscription["latest_invoice"] = new JsonObject { ["id"] = "in_pending_" + id, ["object"] = "invoice", ["customer"] = customer, ["status"] = "open", ["hosted_invoice_url"] = "https://invoice.stripe.com/i/pending_" + id };
                            }
                            else { subscription["items"]!["data"]![0]!["price"] = price; subscription["pending_update"] = null; }
                        }
                    }
                    else if (request.Method == HttpMethod.Delete)
                    {
                        if (subscription["status"]!.GetValue<string>() is "canceled" or "incomplete_expired")
                        {
                            throw new HttpRequestException("An ended subscription must not be canceled again.");
                        }

                        mutations.Enqueue((customer, "DELETE " + path)); subscription["status"] = "canceled";
                    }
                    var reply = subscription.DeepClone().AsObject();
                    if (portalDefaults.ContainsKey(customer))
                    {
                        reply["default_payment_method"] = null;
                        if (QueryHelpers.ParseQuery(request.RequestUri.Query).Any(item => item.Key.StartsWith("expand[", StringComparison.Ordinal) && item.Value == "customer"))
                        {
                            reply["customer"] = new JsonObject { ["id"] = customer, ["object"] = "customer", ["invoice_settings"] = new JsonObject { ["default_payment_method"] = "pm_portal_default" } };
                        }
                    }
                    return Json(reply);
                }
                if (isPreview())
                {
                    var subscription = await SubscriptionAsync(form.GetValueOrDefault("subscription").ToString(), cancellationToken);
                    mutations.Enqueue((subscription["customer"]!.GetValue<string>(), path));
                    if (form.GetValueOrDefault("subscription_details[proration_behavior]") != "always_invoice" ||
                        form.GetValueOrDefault("subscription_details[items][0][id]") != subscription["items"]!["data"]![0]!["id"]!.GetValue<string>())
                    {
                        throw new HttpRequestException("Unexpected invoice preview serialization.");
                    }

                    _ = Price(form.GetValueOrDefault("subscription_details[items][0][price]").ToString());
                    return Json(new JsonObject
                    {
                        ["id"] = "upcoming_demo",
                        ["object"] = "invoice",
                        ["customer"] = subscription["customer"]!.DeepClone(),
                        ["amount_due"] = 666,
                        ["currency"] = "usd",
                        ["lines"] = new JsonObject
                        {
                            ["object"] = "list",
                            ["has_more"] = false,
                            ["data"] = new JsonArray(new JsonObject
                            {
                                ["id"] = "il_proration",
                                ["object"] = "line_item",
                                ["amount"] = 166,
                                ["parent"] = new JsonObject { ["type"] = "subscription_item_details", ["subscription_item_details"] = new JsonObject { ["proration"] = true } }
                            })
                        }
                    });
                }
                if (request.Method == HttpMethod.Post && path == "/v1/subscription_schedules")
                {
                    // Stripe rejects any other parameter alongside from_subscription.
                    if (form.Keys.Any(name => name != "from_subscription"))
                    {
                        return Json(new JsonObject { ["error"] = new JsonObject { ["type"] = "invalid_request_error", ["message"] = "You cannot set other parameters when from_subscription is set." } }, HttpStatusCode.BadRequest);
                    }

                    var subscription = await SubscriptionAsync(form.GetValueOrDefault("from_subscription").ToString(), cancellationToken);
                    mutations.Enqueue((subscription["customer"]!.GetValue<string>(), path));
                    var id = "sub_sched_" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..24];
                    if (!schedules.ContainsKey(id))
                    {
                        if (subscription["schedule"] is not null)
                        {
                            throw new HttpRequestException("Subscription already has a schedule.");
                        }

                        var start = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); var end = subscription["items"]!["data"]![0]!["current_period_end"]!.GetValue<long>();
                        schedules[id] = new JsonObject
                        {
                            ["id"] = id,
                            ["object"] = "subscription_schedule",
                            ["customer"] = subscription["customer"]!.DeepClone(),
                            ["subscription"] = subscription["id"]!.DeepClone(),
                            ["status"] = "active",
                            ["metadata"] = new JsonObject(),
                            ["current_phase"] = new JsonObject { ["start_date"] = start, ["end_date"] = end },
                            ["phases"] = new JsonArray(Phase(start, end, subscription["items"]!["data"]![0]!["price"]!.DeepClone()))
                        };
                        subscription["schedule"] = id;
                    }
                    return Json(schedules[id].DeepClone());
                }
                var scheduleId = path.Split('/')[3]; var schedule = schedules[scheduleId];
                if (request.Method == HttpMethod.Post)
                {
                    mutations.Enqueue((schedule["customer"]!.GetValue<string>(), path));
                }

                if (request.Method == HttpMethod.Post && path.EndsWith("/release", StringComparison.Ordinal))
                {
                    var subscription = await SubscriptionAsync(schedule["subscription"]!.GetValue<string>(), cancellationToken);
                    subscription["schedule"] = null; schedule["status"] = "released"; schedule["released_subscription"] = schedule["subscription"]!.DeepClone(); schedule["subscription"] = null;
                }
                else if (request.Method == HttpMethod.Post)
                {
                    if (form.GetValueOrDefault("end_behavior") != "release" || form.GetValueOrDefault("proration_behavior") != "none" || form.GetValueOrDefault("phases[0][items][0][quantity]") != "1" || form.GetValueOrDefault("phases[1][items][0][quantity]") != "1")
                    {
                        throw new HttpRequestException("Unexpected subscription schedule serialization.");
                    }

                    var start = long.Parse(form["phases[0][start_date]"].ToString()); var renewal = long.Parse(form["phases[0][end_date]"].ToString());
                    if (form.ContainsKey("phases[1][start_date]") && long.Parse(form["phases[1][start_date]"].ToString()) != renewal)
                    {
                        throw new HttpRequestException("Plan phases do not meet at renewal.");
                    }

                    var nextPrice = Price(form["phases[1][items][0][price]"].ToString()); var duration = form["phases[1][duration][interval]"].ToString();
                    var end = (duration == "year" ? DateTimeOffset.FromUnixTimeSeconds(renewal).AddYears(1) : DateTimeOffset.FromUnixTimeSeconds(renewal).AddMonths(1)).ToUnixTimeSeconds();
                    var next = Phase(renewal, end, nextPrice); next["metadata"] = new JsonObject { ["organization_id"] = form.GetValueOrDefault("phases[1][metadata][organization_id]").ToString(), ["plan"] = form.GetValueOrDefault("phases[1][metadata][plan]").ToString(), ["interval"] = form.GetValueOrDefault("phases[1][metadata][interval]").ToString() };
                    schedule["phases"] = new JsonArray(Phase(start, renewal, Price(form["phases[0][items][0][price]"].ToString())), next);
                    if (form.TryGetValue("metadata[dashboard_change_key]", out var changeKey))
                    {
                        schedule["metadata"] = new JsonObject { ["dashboard_change_key"] = changeKey.ToString() };
                    }
                }
                return Json(schedule.DeepClone());
                bool isPreview() => path == "/v1/invoices/create_preview";
            }
            finally { serial.Release(); }
        }
        internal string? SubscriptionStatus(string id)
        {
            return subscriptions.TryGetValue(id, out var subscription) ? subscription["status"]!.GetValue<string>() : null;
        }

        internal string[] Mutations(string customer)
        {
            return mutations.Where(row => row.Customer == customer).Select(row => row.Path).ToArray();
        }

        internal void SetScenario(string customer, string scenario)
        {
            scenarios[customer] = scenario;
        }

        private async Task<JsonObject> SubscriptionAsync(string id, CancellationToken cancellationToken)
        {
            if (subscriptions.TryGetValue(id, out var known))
            {
                return known;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.stripe.com/v1/subscriptions/" + id);
            using var response = await vendor.SendAsync(request, cancellationToken); response.EnsureSuccessStatusCode();
            return subscriptions[id] = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
        }
        private static JsonObject Phase(long start, long end, JsonNode price)
        {
            return new() { ["start_date"] = start, ["end_date"] = end, ["items"] = new JsonArray(new JsonObject { ["price"] = price, ["quantity"] = 1 }) };
        }

        private JsonObject Price(string id)
        {
            var canonicalId = id.EndsWith("_replacement", StringComparison.Ordinal) ? id[..^12] : id;
            var plan = PlanCatalog.Plans.FirstOrDefault(plan => plan.PriceCents > 0 && canonicalId.StartsWith("price_" + plan.Key + "_", StringComparison.Ordinal)) ?? throw new HttpRequestException("Unknown published price.");
            var interval = canonicalId[("price_" + plan.Key + "_").Length..]; if (!plan.SupportsInterval(interval))
            {
                throw new HttpRequestException("Unknown published interval.");
            }

            return new()
            {
                ["id"] = id,
                ["object"] = "price",
                ["active"] = !republishedPrices.ContainsKey(id),
                ["currency"] = "usd",
                ["unit_amount"] = plan.IntervalPriceCents(interval),
                ["recurring"] = new JsonObject { ["interval"] = interval == "annual" ? "year" : "month", ["interval_count"] = 1 },
                ["product"] = new JsonObject { ["id"] = "prod_" + plan.Key, ["object"] = "product", ["active"] = true, ["name"] = "TerraScale " + plan.Name, ["metadata"] = new JsonObject { ["plan"] = plan.Key } }
            };
        }
        private static HttpResponseMessage Json(JsonNode? value, HttpStatusCode status = HttpStatusCode.OK)
        {
            return new(status) { Content = new StringContent(value!.ToJsonString(), System.Text.Encoding.UTF8, "application/json") };
        }

        private sealed class BillingPlanBarrier
        {
            public int Reads { get; set; }
            public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
