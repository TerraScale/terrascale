using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TerraScale.Dashboard;
using TerraScale.Dashboard.Common.Accounts;
using TerraScale.Dashboard.Errors;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;

var cancellationToken = CancellationToken.None;
var state = Environment.GetEnvironmentVariable("E2E_STATE") ?? throw new InvalidOperationException("E2E_STATE is required.");
var secrets = await File.ReadAllLinesAsync(Path.Combine(state, "secrets"), cancellationToken);
Environment.SetEnvironmentVariable("TERRABASE_PEER_SECRET", secrets[0]);
Environment.SetEnvironmentVariable("TS_DASHBOARD_SESSION_ENCRYPTION_KEY", secrets[4]);
Environment.SetEnvironmentVariable("TS_AUTH_DASHBOARD_CLIENT_SECRET", secrets[5]);
Environment.SetEnvironmentVariable("STRIPE_SECRET_KEY", secrets[6]);
Environment.SetEnvironmentVariable("STRIPE_WEBHOOK_SECRET", secrets[7]);
Environment.SetEnvironmentVariable("TS_DASHBOARD_TERRABASE_URL", "http://127.0.0.1:8081");
if (args is ["background-case", var kind, var journeyPath])
{
    await BackgroundJourney.CaseAsync(kind, journeyPath, cancellationToken);
    return;
}
if (args is ["background-worker", var work, var at, var ownerPath, var job])
{
    await BackgroundJourney.WorkAsync(work, DateTimeOffset.Parse(at, CultureInfo.InvariantCulture), ownerPath, job, cancellationToken);
    return;
}
using var control = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:8027") };
var clock = new BillingClock();
await using var application = DashboardApplication.Build(["--environment", "Development", "--contentRoot", Environment.GetEnvironmentVariable("E2E_BUILD") + "/dashboard"],
    builder => builder.Services.AddSingleton<TimeProvider>(clock));
await using var contender = DashboardApplication.Build(["--environment", "Development", "--contentRoot", Environment.GetEnvironmentVariable("E2E_BUILD") + "/dashboard"],
    builder => builder.Services.AddSingleton<TimeProvider>(clock));
var otherReporter = contender.Services.GetRequiredService<UsageOverageReporter>();
var storage = application.Services.GetRequiredService<DashboardDb>();
var organizations = application.Services.GetRequiredService<OrganizationStore>();
var reporter = application.Services.GetRequiredService<UsageOverageReporter>();
var billing = application.Services.GetRequiredService<StripeBilling>();
var refresh = application.Services.GetRequiredService<StripeBindingRefresh>();
var reconciler = application.Services.GetServices<IHostedService>().OfType<PlanReconciler>().Single();
var created = new List<string>();

async Task AdvanceAsync(string value)
{
    using var response = await control.PostAsJsonAsync("/billing-clock", new ClockInput(value), BillingJson.Default.ClockInput, cancellationToken);
    response.EnsureSuccessStatusCode();
    var time = await response.Content.ReadFromJsonAsync(BillingJson.Default.ClockInput, cancellationToken) ?? throw new IOException("Billing clock is unavailable.");
    clock.Now = DateTimeOffset.Parse(time.Now, CultureInfo.InvariantCulture);
}

async Task<SubscriptionIds> SubscribeAsync(string organizationId, string price, string? customer)
{
    using var response = await control.PostAsJsonAsync("/billing-subscription", new SubscriptionInput(organizationId, price, clock.Now.ToString("O", CultureInfo.InvariantCulture), customer), BillingJson.Default.SubscriptionInput, cancellationToken);
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadFromJsonAsync(BillingJson.Default.SubscriptionIds, cancellationToken) ?? throw new IOException("Billing subscription is unavailable.");
}

async Task<BillingBinding> BindingAsync(string id)
{
    await using var connection = await storage.OpenAsync(cancellationToken);
    return await connection.QuerySingleAsync<BillingBinding>(JourneySql.Billing + " WHERE organization_id = @Id", new { Id = id });
}

async Task RefreshAsync(BillingBinding binding)
{
    await EventAsync(await BindingAsync(binding.OrganizationId), "evt_refresh_" + AccountService.NewId());
}

async Task<BillingBinding> CreateAsync(string price = "price_starter_month")
{
    var id = AccountService.NewId();
    var key = price == "price_pro_month" ? "pro" : "starter";
    await organizations.CreateAsync(new() { Id = id, PlanId = PlanCatalog.For(key).Id, PaymentMethodValidated = true }, cancellationToken);
    created.Add(id);
    var vendor = await SubscribeAsync(id, price, null);
    var binding = new BillingBinding
    {
        OrganizationId = id,
        Version = AccountService.NewId(),
        RequestId = AccountService.NewId(),
        State = "active",
        CustomerId = vendor.Customer,
        SubscriptionId = vendor.Subscription,
        StartedAt = clock.Now,
        EffectivePlan = key,
        EffectiveInterval = "monthly",
        Status = "active",
        Plan = key,
        Interval = "monthly",
        OverageStartsAt = new(clock.Now.Year, clock.Now.Month, 1, 0, 0, 0, TimeSpan.Zero)
    };
    await storage.WriteAsync(async (connection, transaction) =>
    {
        await connection.ExecuteAsync(JourneySql.InsertBilling, binding, transaction);
        await connection.ExecuteAsync(JourneySql.InsertStripeCustomer, new StripeCustomerRow { Id = AccountService.NewId(), OrganizationId = id, CustomerId = vendor.Customer, CreatedAt = clock.Now.UtcDateTime }, transaction);
    }, cancellationToken);
    await RefreshAsync(binding);
    return await BindingAsync(id);
}

async Task<BillingBinding> SucceedAsync(BillingBinding binding, string price)
{
    var vendor = await SubscribeAsync(binding.OrganizationId, price, binding.CustomerId);
    await storage.ReadAsync(connection => connection.ExecuteAsync("UPDATE billing SET subscription_id = @Subscription, status = 'active', version = @Version WHERE organization_id = @OrganizationId", new { vendor.Subscription, Version = AccountService.NewId(), binding.OrganizationId }), cancellationToken);
    await RefreshAsync(binding);
    return await BindingAsync(binding.OrganizationId);
}

async Task CancelAsync(BillingBinding binding)
{
    await ControlAsync("/billing-cancel", new CancelInput(binding.SubscriptionId, clock.Now.ToString("O", CultureInfo.InvariantCulture)));
    await RefreshAsync(binding);
}

async Task<string> ItemAsync(BillingBinding binding, long amount, long quantity, string? unitAmountDecimal, string? charge, string month)
{
    Dictionary<string, string> metadata = new() { ["terrascale_kind"] = OperationUnitOverage.Kind, ["terrascale_month"] = month, ["terrascale_org"] = binding.OrganizationId };
    if (charge is not null) { metadata["terrascale_charge"] = charge; }
    using var response = await control.PostAsJsonAsync("/billing-item", new ItemInput(binding.CustomerId, binding.SubscriptionId, amount, quantity, unitAmountDecimal, metadata), BillingJson.Default.ItemInput, cancellationToken);
    response.EnsureSuccessStatusCode();
    return (await response.Content.ReadFromJsonAsync(BillingJson.Default.ItemId, cancellationToken) ?? throw new IOException("Provider item is unavailable.")).Id;
}

async Task CheckAsync(BillingBinding binding, string month, int count, long amount)
{
    using var response = await control.GetAsync("/billing-items?customer=" + binding.CustomerId + "&month=" + month, cancellationToken);
    response.EnsureSuccessStatusCode();
    var items = await response.Content.ReadFromJsonAsync(BillingJson.Default.ItemTotals, cancellationToken) ?? throw new IOException("Billing items are unavailable.");
    if (items.Amount != amount || items.Count != count)
    {
        throw new InvalidOperationException($"Provider items {month}: expected {count} totaling {amount} cents, got {items.Count} totaling {items.Amount} cents.");
    }
}

async Task ControlAsync(string path, object input)
{
    var json = JsonSerializer.SerializeToElement(input, input.GetType(), BillingJson.Default);
    using var response = await control.PostAsJsonAsync(path, json, BillingJson.Default.JsonElement, cancellationToken);
    response.EnsureSuccessStatusCode();
}

async Task<OverageCharge?> ChargeAsync(BillingBinding binding, DateTimeOffset month, string meter = OperationUnitOverage.Kind)
{
    await using var connection = await storage.OpenAsync(cancellationToken);
    return await connection.QuerySingleOrDefaultAsync<OverageCharge>(JourneySql.Charges + " WHERE organization_id = @OrganizationId AND month = @Month AND meter = @Meter",
        new { binding.OrganizationId, Month = month, Meter = meter });
}

async Task<OverageCharge> FrozenAsync(BillingBinding binding, DateTimeOffset month) => await ChargeAsync(binding, month) ?? throw new InvalidOperationException("The month has no operations charge.");

async Task<string> CheckedAtAsync(BillingBinding binding)
{
    await using var connection = await storage.OpenAsync(cancellationToken);
    return await connection.QuerySingleAsync<string>("SELECT checked_at FROM billing_subscriptions WHERE organization_id = @OrganizationId", new { binding.OrganizationId });
}

async Task<string?> SkippedAsync(BillingBinding binding, DateTimeOffset month, string meter)
{
    await using var connection = await storage.OpenAsync(cancellationToken);
    return await connection.QuerySingleOrDefaultAsync<string>("SELECT reason FROM billing_skipped_meters WHERE organization_id = @OrganizationId AND month = @Month AND meter = @Meter",
        new { binding.OrganizationId, Month = month, Meter = meter });
}

async Task FailNextItemAsync()
{
    await ControlAsync("/failure", new FailureInput("POST", "/v1/invoiceitems", 400, 1, false));
}

async Task SetGraceAsync(BillingBinding binding)
{
    var row = await organizations.FindAsync(binding.OrganizationId, cancellationToken) ?? throw new IOException("Organization missing.");
    row.Organization.PaymentGraceUntil = clock.Now.AddDays(-1);
    row.Organization.BillingStatus = "active";
    await organizations.ReplaceAsync(row, cancellationToken);
}

async Task AssertGraceAsync(BillingBinding binding, bool retained, string status)
{
    var row = await organizations.FindAsync(binding.OrganizationId, cancellationToken) ?? throw new IOException("Organization missing.");
    if (row.Organization.PlanId != OrganizationRecord.StarterPlanId || row.Organization.BillingStatus != status || (row.Organization.PaymentGraceUntil is not null) != retained)
    {
        throw new InvalidOperationException($"Payment grace entitlement mismatch: plan {row.Organization.PlanId}, status {row.Organization.BillingStatus}, grace retained {row.Organization.PaymentGraceUntil is not null}; expected plan {OrganizationRecord.StarterPlanId}, status {status}, grace retained {retained}.");
    }
}

async Task EventAsync(BillingBinding binding, string eventId)
{
    var body = JsonSerializer.Serialize(new EventInput(eventId, "2026-09-30.endive", new(new("subscription", binding.SubscriptionId, binding.CustomerId)), false, "customer.subscription.updated", clock.Now.ToUnixTimeSeconds()), BillingJson.Default.EventInput);
    var timestamp = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
    var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secrets[7]), Encoding.UTF8.GetBytes(timestamp + "." + body)));
    var receipt = await billing.HandleWebhookAsync(body, "t=" + timestamp + ",v1=" + signature, cancellationToken) switch
    {
        Done => "applied",
        Rejected => throw new InvalidOperationException("The dashboard rejected the journey's signed provider event " + eventId + "."),
        Deferred deferred => throw new InvalidOperationException("The dashboard deferred the provider event " + deferred.EventId + ".", deferred.Cause),
        null => throw new InvalidOperationException("The dashboard returned no receipt for the provider event " + eventId + "."),
    };
    Require(receipt == "applied", "The provider event " + eventId + " was not acknowledged.");
}

void Require(bool condition, string message)
{
    if (!condition) { throw new InvalidOperationException(message); }
}

try
{
    var january = new DateTimeOffset(2028, 1, 1, 0, 0, 0, TimeSpan.Zero);
    const string Month = "2028-01";
    await AdvanceAsync("2028-01-03T00:00:00Z");
    var previous = await CreateAsync();
    var late = await CreateAsync();
    var replaced = await CreateAsync("price_pro_month");
    var noStorage = await CreateAsync();
    var crashed = await CreateAsync();
    var raced = await CreateAsync();
    var mismatched = await CreateAsync();
    var gated = await CreateAsync();
    var midpoint = await CreateAsync();
    var ended = await CreateAsync();
    var unmapped = await CreateAsync("unmapped");
    await AdvanceAsync("2028-01-15T12:00:00Z");
    var binding = await CreateAsync();
    await AdvanceAsync("2028-01-20T00:00:00Z");
    var previousItem = await ItemAsync(previous, 400, 800_000, "0.0005", null, Month);
    await CancelAsync(replaced);
    await AdvanceAsync("2028-01-22T00:00:00Z");
    foreach (var other in new[] { "2027-10", "2027-11", "2027-12" }) { await ItemAsync(previous, 100, 1, null, null, other); }
    var successor = await SucceedAsync(replaced, "price_starter_month");
    foreach (var organization in new[] { previous, late, noStorage, crashed, raced, mismatched, gated, midpoint, ended, unmapped }) { await RefreshAsync(organization); }
    await ControlAsync("/billing-plan-change", new PlanChangeInput(late.SubscriptionId, "price_pro_month", "2028-01-30T00:00:00Z"));
    await AdvanceAsync("2028-02-01T06:00:00Z");
    foreach (var organization in new[] { binding, previous, successor, noStorage, crashed, raced, mismatched, gated, midpoint, unmapped }) { await RefreshAsync(organization); }
    await CancelAsync(ended);

    await Task.WhenAll(reporter.ReportAsync(binding.OrganizationId, 1_250_000, 0, january, cancellationToken), otherReporter.ReportAsync(binding.OrganizationId, 2_250_000, 0, january, cancellationToken));
    await using (var connection = await storage.OpenAsync(cancellationToken))
    {
        if (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM billing_overage_charges WHERE organization_id = @OrganizationId AND month = @Month", new { binding.OrganizationId, Month = january }) != 2)
        {
            throw new InvalidOperationException("The racing freeze must leave exactly one row per meter.");
        }
    }
    var frozen = await FrozenAsync(binding, january);
    await CheckAsync(binding, Month, 1, frozen.AmountCents);
    if (frozen.State != OverageCharge.Charged || frozen.ProviderId is null || frozen.Allowance != 250_000 || frozen.AmountCents != (long)Math.Round(frozen.Quantity * frozen.UnitRate, MidpointRounding.AwayFromZero)) { throw new InvalidOperationException("Frozen pricing mismatch."); }
    Console.WriteLine("billing ledger journey: racing reporters -> one provider item, one row per meter, winner quantity and amount");

    await reporter.ReportAsync(previous.OrganizationId, 1_250_000, 0, january, cancellationToken);
    var adopted = await FrozenAsync(previous, january);
    if (adopted.State != OverageCharge.Adopted || adopted.ProviderId != previousItem || adopted.ProviderAmountCents != 400 || adopted.AmountCents != 500) { throw new InvalidOperationException($"Previous reporter item must be adopted: {adopted.State} {adopted.ProviderAmountCents}."); }
    await CheckAsync(previous, Month, 1, 400);
    Console.WriteLine("billing ledger journey: previous reporter item for 2028-01 on the second provider page -> adopted with its 400 cents beside the ledger's 500, no second item");

    if (await reporter.ReportAsync(late.OrganizationId, 1_250_000, 0, january, cancellationToken) != MonthSettlement.WaitingForTerms || await ChargeAsync(late, january) is not null) { throw new InvalidOperationException("January must wait for a refresh after the month ends."); }
    await AdvanceAsync("2028-02-02T09:00:00Z");
    await RefreshAsync(late);
    await reporter.ReportAsync(late.OrganizationId, 1_250_000, 0, january, cancellationToken);
    var upgraded = await FrozenAsync(late, january);
    if (upgraded.State != OverageCharge.NothingDue || upgraded.PlanName != "Pro" || upgraded.AmountCents != 0) { throw new InvalidOperationException("An upgrade effective on the 30th must give January its lower charge."); }
    await CheckAsync(late, Month, 0, 0);
    Console.WriteLine("billing terms journey: upgrade effective 2028-01-30, learned 2028-02-02 -> January waits for the post-month refresh, then Pro terms, no overage");

    await reporter.ReportAsync(successor.OrganizationId, 1_500_000, 0, january, cancellationToken);
    var spanning = await FrozenAsync(successor, january);
    if (spanning.State != OverageCharge.NothingDue || spanning.PlanName != "Pro" || spanning.SubscriptionId != successor.SubscriptionId) { throw new InvalidOperationException("A canceled subscription's terms must count for its month."); }
    await CheckAsync(successor, Month, 0, 0);
    Console.WriteLine("billing terms journey: Pro subscription canceled 2028-01-20, Starter started 2028-01-22, 1,500,000 units -> Pro allowance, no overage");

    await reporter.ReportAsync(noStorage.OrganizationId, 1_250_000, null, january, cancellationToken);
    var operationsOnly = await FrozenAsync(noStorage, january);
    if (operationsOnly.State != OverageCharge.Charged || operationsOnly.AmountCents != 500 || await SkippedAsync(noStorage, january, StorageOverage.Kind) != "missing_storage_contract") { throw new InvalidOperationException("A missing storage contract must not drop the operations charge."); }
    await CheckAsync(noStorage, Month, 1, 500);
    Console.WriteLine("billing ledger journey: no storage contract -> storage skipped with its reason, operations 500 cents charged in its own row");

    await reporter.ReportAsync(midpoint.OrganizationId, 251_000, 0, january, cancellationToken);
    var rounded = await FrozenAsync(midpoint, january);
    await CheckAsync(midpoint, Month, 1, 1);
    if (rounded.AmountCents != 1 || rounded.Description != "Operation units overage, Starter, 2028-01: 1,000") { throw new InvalidOperationException("Midpoint amount or readable invoice description differs."); }
    Console.WriteLine("billing ledger journey: half-cent midpoint -> exact one-cent provider item; readable operation name, plan, month and quantity");

    await reporter.ReportAsync(unmapped.OrganizationId, 1_250_000, 0, january, cancellationToken);
    if (await ChargeAsync(unmapped, january) is not null || await SkippedAsync(unmapped, january, OperationUnitOverage.Kind) != "missing_plan") { throw new InvalidOperationException("Unmapped price must skip the month with its reason."); }
    Console.WriteLine("billing terms journey: unmapped provider price -> no calculable terms, month skipped as missing_plan");

    await reporter.ReportAsync(ended.OrganizationId, 1_250_000, 0, january, cancellationToken);
    var retired = await FrozenAsync(ended, january);
    if (retired.State != OverageCharge.Skipped || retired.SkipReason != "subscription_ended" || retired.AmountCents != 500 || retired.ProviderId is not null) { throw new InvalidOperationException("Ended subscription without a provider item must keep a visible skipped amount."); }
    await CheckAsync(ended, Month, 0, 0);
    Console.WriteLine("billing ledger journey: subscription ended before dispatch, no provider item -> 500 cents kept as subscription_ended");

    await reporter.ReportAsync(crashed.OrganizationId, 1_250_000, 0, january, cancellationToken);
    var accepted = await FrozenAsync(crashed, january);
    await storage.ReadAsync(connection => connection.ExecuteAsync("""
        UPDATE billing_overage_charges SET state = @State, provider_id = NULL, provider_amount_cents = NULL, accepted_at = NULL, claim_owner = @ClaimOwner, claim_until = @ClaimUntil
        WHERE id = @Id
        """, new { State = OverageCharge.Pending, ClaimOwner = "crashed-instance", ClaimUntil = clock.Now.AddMinutes(-1), accepted.Id }), cancellationToken);
    await ControlAsync("/billing-forget-idempotency", new EmptyInput());
    await CancelAsync(crashed);
    await reporter.RunCurrentAsync(cancellationToken);
    var recovered = await FrozenAsync(crashed, january);
    if (recovered.State != OverageCharge.Charged || recovered.ProviderId != accepted.ProviderId) { throw new InvalidOperationException("An accepted item must be found before the row is retired."); }
    await CheckAsync(crashed, Month, 1, 500);
    Console.WriteLine("billing ledger journey: provider accepted, instance died before saving, subscription then canceled -> row charged with the provider item id");

    await FailNextItemAsync();
    await reporter.ReportAsync(raced.OrganizationId, 1_250_000, 0, january, cancellationToken);
    var hold = new HoldInput("POST", "/v1/invoiceitems", raced.CustomerId);
    await ControlAsync("/billing-hold", hold);
    var dispatching = reporter.RunCurrentAsync(cancellationToken);
    await ControlAsync("/billing-held", hold);
    await CancelAsync(raced);
    await otherReporter.RunCurrentAsync(cancellationToken);
    var during = await FrozenAsync(raced, january);
    if (during.State != OverageCharge.Pending || during.ClaimOwner is null) { throw new InvalidOperationException("Retirement must wait for the dispatch claim."); }
    await ControlAsync("/billing-release", hold);
    await dispatching;
    await otherReporter.RunCurrentAsync(cancellationToken);
    var raceResult = await FrozenAsync(raced, january);
    if (raceResult.State != OverageCharge.Charged || raceResult.ProviderId is null || raceResult.SkipReason is not null) { throw new InvalidOperationException("Dispatch and retirement must leave one outcome."); }
    await CheckAsync(raced, Month, 1, 500);
    Console.WriteLine("billing ledger journey: one instance dispatching while another retires the same row -> claim serializes them, one charged outcome matching the provider");

    await FailNextItemAsync();
    await reporter.ReportAsync(mismatched.OrganizationId, 1_250_000, 0, january, cancellationToken);
    var unsent = await FrozenAsync(mismatched, january);
    var foreign = await ItemAsync(mismatched, 499, 1, null, unsent.Id, Month);
    await reporter.RunCurrentAsync(cancellationToken);
    await reporter.RunCurrentAsync(cancellationToken);
    var held = await FrozenAsync(mismatched, january);
    if (held.State != OverageCharge.Mismatch || held.ProviderId != foreign || held.ProviderAmountCents != 499 || held.AmountCents != 500) { throw new InvalidOperationException("A provider item with another amount must be held as a mismatch."); }
    await CheckAsync(mismatched, Month, 1, 499);
    Console.WriteLine("billing ledger journey: provider item for the row with 499 cents instead of 500 -> mismatch state, no second item");

    await FailNextItemAsync();
    await reporter.ReportAsync(gated.OrganizationId, 1_250_000, 0, january, cancellationToken);
    const string UnknownScript = "TerraScale.Dashboard.Schema.999_unknown_to_this_build.sql";
    var checkedAt = await CheckedAtAsync(gated);
    await JourneySql.InsertSchemaVersionAsync(storage, UnknownScript, clock.Now.UtcDateTime, cancellationToken);
    try
    {
        await reporter.RunOnceAsync(cancellationToken);
        await reporter.RunCurrentAsync(cancellationToken);
        await refresh.RunOnceAsync(cancellationToken);
        await reconciler.RunOnceAsync(cancellationToken);
        if ((await FrozenAsync(gated, january)).State != OverageCharge.Pending || await CheckedAtAsync(gated) != checkedAt)
        {
            throw new InvalidOperationException("A build that lacks an applied migration must run no billing job.");
        }
    }
    finally
    {
        await JourneySql.DeleteSchemaVersionAsync(storage, UnknownScript, CancellationToken.None);
    }
    await reporter.RunCurrentAsync(cancellationToken);
    if ((await FrozenAsync(gated, january)).State != OverageCharge.Charged) { throw new InvalidOperationException("Billing jobs must resume once the build knows every migration."); }
    Console.WriteLine("billing migration journey: database holds a migration this build lacks -> reporter, refresh and reconciler idle; resume after it is gone");

    var expiredReceipt = "evt_cleanup_" + AccountService.NewId();
    var liveReceipt = "evt_cleanup_" + AccountService.NewId();
    await storage.WriteAsync(async (connection, transaction) =>
    {
        foreach (var (id, age) in new[] { (expiredReceipt, 31), (liveReceipt, 1) })
        {
            await connection.ExecuteAsync(JourneySql.InsertReceipt, new StripeEventReceipt
            {
                Id = id,
                Version = AccountService.NewId(),
                OrganizationId = binding.OrganizationId,
                SubscriptionId = binding.SubscriptionId,
                InvoiceId = "",
                EventType = "customer.subscription.updated",
                ReceivedAt = clock.Now.AddDays(-age),
                EventCreated = clock.Now.AddDays(-age),
                CompletedAt = clock.Now.AddDays(-age),
                ProcessingUntil = clock.Now.AddDays(-age)
            }, transaction);
        }
    }, cancellationToken);
    await Task.WhenAll(billing.CleanEventReceiptsAsync(cancellationToken), contender.Services.GetRequiredService<StripeBilling>().CleanEventReceiptsAsync(cancellationToken));
    const string ReceiptExists = "SELECT count(*) FROM stripe_event_receipts WHERE id = @Id";
    if ((await storage.ReadAsync(connection => connection.ExecuteScalarAsync<long>(ReceiptExists, new { Id = expiredReceipt }), cancellationToken) > 0) || !(await storage.ReadAsync(connection => connection.ExecuteScalarAsync<long>(ReceiptExists, new { Id = liveReceipt }), cancellationToken) > 0))
    {
        throw new InvalidOperationException("Concurrent receipt cleanup changed retention.");
    }
    Console.WriteLine("billing webhook journey: concurrent bulk cleanup removes expired receipt and retains recent receipt");

    await SetGraceAsync(binding);
    var paid = await billing.ResolveExpiredGraceAsync(binding.OrganizationId, cancellationToken);
    if (paid.PaymentGraceUntil is not null || paid.PlanId != OrganizationRecord.StarterPlanId || paid.BillingStatus != "active") { throw new InvalidOperationException("Canonical paid subscription must clear expired grace."); }
    Console.WriteLine("billing grace journey: expired grace with provider paid -> clear grace, retain plan");
    await ControlAsync("/payment", new PaymentInput(binding.SubscriptionId, false, false));
    await SetGraceAsync(binding);
    await ControlAsync("/failure", new FailureInput("GET", "/v1/subscriptions/" + binding.SubscriptionId, 400, null, true));
    await billing.ResolveExpiredGraceAsync(binding.OrganizationId, cancellationToken);
    await AssertGraceAsync(binding, true, "active");
    Console.WriteLine("billing grace journey: provider unreachable -> retain plan and grace");
    await ControlAsync("/failure", new FailureInput("GET", "/v1/subscriptions/" + binding.SubscriptionId, 400, 0, false));
    await billing.ResolveExpiredGraceAsync(binding.OrganizationId, cancellationToken);
    await AssertGraceAsync(binding, true, "billing_required");
    var expiredVersion = (await organizations.FindAsync(binding.OrganizationId, cancellationToken))?.Version;
    await billing.ResolveExpiredGraceAsync(binding.OrganizationId, cancellationToken);
    if ((await organizations.FindAsync(binding.OrganizationId, cancellationToken))?.Version != expiredVersion) { throw new InvalidOperationException("Expiry must commit once."); }
    Console.WriteLine("billing grace journey: provider unpaid -> expire once");

    await ControlAsync("/payment", new PaymentInput(binding.SubscriptionId, true, false));
    await ControlAsync("/failure", new FailureInput("POST", "/v1/subscriptions/" + binding.SubscriptionId, 400, null, true));
    await ControlAsync("/subscription-tax", new TaxInput(binding.SubscriptionId, "supported", "new"));
    await AssertGraceAsync(binding, false, "active");
    using (var response = await control.GetAsync("/subscription-tax-state?subscription=" + binding.SubscriptionId, cancellationToken))
    {
        response.EnsureSuccessStatusCode();
        var tax = await response.Content.ReadFromJsonAsync(BillingJson.Default.TaxState, cancellationToken) ?? throw new IOException("Tax state missing.");
        if (tax.Enabled) { throw new InvalidOperationException("The transport failure must leave tax repair pending after entitlement commits."); }
    }
    Console.WriteLine("billing tax journey: tax repair transport failure -> entitlement restored and grace cleared");
    await ControlAsync("/failure", new FailureInput("POST", "/v1/subscriptions/" + binding.SubscriptionId, 400, 0, false));
    var eventId = "evt_receipt_" + AccountService.NewId();
    await EventAsync(binding, eventId);
    var version = (await BindingAsync(binding.OrganizationId)).Version;
    await EventAsync(binding, eventId);
    if ((await BindingAsync(binding.OrganizationId)).Version != version) { throw new InvalidOperationException("A replayed event changed billing state."); }
    Console.WriteLine("billing webhook journey: replayed provider event -> acknowledged, no billing write");

    using var recoveryHost = BackgroundHost.Build();
    var recovery = recoveryHost.Services.GetRequiredService<StripeBilling>();
    var realNow = TimeProvider.System.GetUtcNow();

    var deadOwner = await CreateAsync();
    var deadEvent = "evt_dead_" + AccountService.NewId();
    await InsertReceiptAsync(deadOwner, deadEvent, StripeBilling.MaxReceiptAttempts - 1);
    await ControlAsync("/failure", new FailureInput("GET", "/v1/subscriptions/" + deadOwner.SubscriptionId, 404, 1, false));
    await recovery.RecoverEventReceiptsAsync(cancellationToken);
    await EventuallyAsync(async () => await ReceiptAsync(deadEvent) is { DeadLetteredAt: not null, CompletedAt: not null, Attempts: StripeBilling.MaxReceiptAttempts },
        "A receipt failing its last attempt must be dead-lettered and never retried.");
    Console.WriteLine("billing webhook journey: receipt failing its twelfth attempt -> dead-lettered, completed, no further retry");

    var retryOwner = await CreateAsync();
    var retryEvent = "evt_retry_" + AccountService.NewId();
    await InsertReceiptAsync(retryOwner, retryEvent, 0);
    await ControlAsync("/failure", new FailureInput("GET", "/v1/subscriptions/" + retryOwner.SubscriptionId, 404, 1, false));
    await recovery.RecoverEventReceiptsAsync(cancellationToken);
    await EventuallyAsync(async () => await ReceiptAsync(retryEvent) is { Attempts: 1, CompletedAt: null, DeadLetteredAt: null } retrying && retrying.ProcessingUntil > TimeProvider.System.GetUtcNow(),
        "A failed receipt must back off with one recorded attempt.");
    Require(await SignedAsync(recovery, SubscriptionEvent(retryEvent, retryOwner)) == "deferred", "A redelivery while the receipt backs off must answer 503.");
    await storage.ReadAsync(connection => connection.ExecuteAsync("UPDATE stripe_event_receipts SET processing_until = @Due WHERE id = @Id",
        new { Due = TimeProvider.System.GetUtcNow().AddMinutes(-1), Id = retryEvent }), cancellationToken);
    Require(await SignedAsync(recovery, SubscriptionEvent(retryEvent, retryOwner)) == "applied", "A redelivery of a due failed receipt must reprocess it.");
    Require(await ReceiptAsync(retryEvent) is { CompletedAt: not null, DeadLetteredAt: null }, "The reprocessed receipt must complete.");
    Console.WriteLine("billing webhook journey: failed receipt -> one attempt with backoff, redelivery answers 503 while backing off, due redelivery reprocesses and completes");

    var paidDraft = await DraftAsync(true);
    Require(await SignedAsync(recovery, DraftEvent(paidDraft.Session)) == "applied", "A paid draft checkout event must be acknowledged.");
    Require(await DraftRowAsync(paidDraft.DraftId) is { PaidAt: not null }, "A paid checkout event for an unknown customer must record payment on its draft by draft_id.");
    var unpaidDraft = await DraftAsync(false);
    await storage.ReadAsync(connection => connection.ExecuteAsync("UPDATE pending_organization_checkouts SET created_at = @At WHERE draft_id IN (@Paid, @Unpaid)",
        new { At = realNow.AddHours(-25).UtcDateTime, Paid = paidDraft.DraftId, Unpaid = unpaidDraft.DraftId }), cancellationToken);
    await recovery.ReconcileAbandonedDraftsAsync(cancellationToken);
    await EventuallyAsync(async () => await DraftRowAsync(paidDraft.DraftId) is null && await DraftRowAsync(unpaidDraft.DraftId) is null,
        "Drafts left for 24 hours must be canceled.");
    var draftLog = await StripeLogAsync();
    Require(draftLog.Cancels.Any(entry => entry.Subscription == paidDraft.Subscription) && draftLog.Refunds.Any(entry => entry.Subscription == paidDraft.Subscription),
        "An abandoned paid draft must have its subscription canceled and its payment refunded.");
    Console.WriteLine("billing draft journey: paid event for an unknown customer -> recorded on its draft; drafts left 24 hours -> canceled, paid one canceled and refunded");

    var stampDraft = await DraftAsync(true);
    var stampSubscription = stampDraft.Subscription is { Length: > 0 } stampSubscriptionId ? stampSubscriptionId : throw new IOException("The paid draft has no subscription.");
    var stampOrganization = AccountService.NewId();
    created.Add(stampOrganization);
    await storage.ReadAsync(connection => connection.ExecuteAsync("DELETE FROM pending_organization_checkouts WHERE draft_id = @DraftId", new { stampDraft.DraftId }), cancellationToken);
    await storage.WriteAsync(async (connection, transaction) => await connection.ExecuteAsync(JourneySql.InsertBilling, new BillingBinding
    {
        OrganizationId = stampOrganization,
        Version = AccountService.NewId(),
        RequestId = stampDraft.DraftId,
        State = "active",
        CustomerId = stampDraft.Customer,
        CheckoutId = stampDraft.Checkout,
        SubscriptionId = stampSubscription,
        StartedAt = realNow,
        EffectivePlan = "starter",
        EffectiveInterval = "monthly",
        Status = "active",
        Plan = "starter",
        Interval = "monthly",
        OverageStartsAt = OrganizationUsage.MonthOf(realNow)
    }, transaction), cancellationToken);
    await ControlAsync("/failure", new FailureInput("POST", "/v1/subscriptions/" + stampSubscription, 404, 1, false));
    await recovery.StampDraftOwnersAsync(cancellationToken);
    await EventuallyAsync(async () =>
    {
        await recovery.StampDraftOwnersAsync(cancellationToken);
        return await storage.ReadAsync(connection => connection.ExecuteScalarAsync<long>(JourneySql.OwnerStamped,
            new { OrganizationId = stampOrganization, SubscriptionId = stampSubscription }), cancellationToken) == 1;
    }, "The background owner stamp must retry after a contained failure.");
    var stampLog = await StripeLogAsync();
    Require(stampLog.Calls.Any(call => call.Path == "/v1/subscriptions/" + stampSubscription && call.Status == 404), "The injected owner stamp failure must have been met.");
    var stamped = await ObjectAsync(stampSubscription);
    var stampedMetadata = stamped.GetProperty("metadata");
    Require(stampedMetadata.GetProperty("organization_id").GetString() == stampOrganization && stampedMetadata.GetProperty("terrascale_tax").GetString() == "automatic"
        && stampedMetadata.GetProperty("draft_id").GetString() == stampDraft.DraftId, "The owner stamp must add organization_id and keep the other metadata.");
    Console.WriteLine("billing draft journey: owner stamp failure contained in the background; retry stamps organization_id and keeps existing metadata");

    var realized = await CreateAsync();
    var orphaned = await CreateAsync();
    var recent = await CreateAsync();
    await ControlAsync("/billing-plan-change", new PlanChangeInput(realized.SubscriptionId, "price_pro_month", clock.Now.ToString("O", CultureInfo.InvariantCulture)));
    var schedule = await ScheduleAsync(orphaned.SubscriptionId);
    await InsertChangeAsync(realized, realNow.AddMinutes(-20));
    await InsertChangeAsync(orphaned, realNow.AddMinutes(-20));
    await InsertChangeAsync(recent, realNow);
    await recovery.ReconcileStaleChangesAsync(cancellationToken);
    await EventuallyAsync(async () => await ChangeStatusAsync(realized) == "applied" && await ChangeStatusAsync(orphaned) == "abandoned",
        "Stale applying changes must complete when Stripe applied them and be abandoned otherwise.");
    Require(await ChangeStatusAsync(recent) == "applying", "A change applying for less than 15 minutes must be left alone.");
    Require((await StripeLogAsync()).Releases.Any(entry => entry.Schedule == schedule), "Abandoning a stale change must release its orphan schedule.");
    Console.WriteLine("billing plan journey: stale applying changes -> applied when Stripe has them, abandoned with orphan schedule released otherwise; recent change untouched");

    var previousMonth = OrganizationUsage.MonthOf(realNow).AddMonths(-1);
    var unavailableOrganization = new string('u', 26);
    var partialOrganization = AccountService.NewId();
    created.Add(unavailableOrganization);
    created.Add(partialOrganization);
    foreach (var organizationId in new[] { unavailableOrganization, partialOrganization })
    {
        await storage.WriteAsync(async (connection, transaction) => await connection.ExecuteAsync(JourneySql.InsertBilling, new BillingBinding
        {
            OrganizationId = organizationId,
            Version = AccountService.NewId(),
            RequestId = AccountService.NewId(),
            State = "active",
            StartedAt = previousMonth,
            EffectivePlan = "starter",
            EffectiveInterval = "monthly",
            Status = "active",
            Plan = "starter",
            Interval = "monthly",
            OverageStartsAt = previousMonth
        }, transaction), cancellationToken);
    }
    await JourneySql.DeleteJobAsync(storage, "usage-settlement", cancellationToken);
    Require(await ActivatorUtilities.CreateInstance<UsageOverageReporter>(recoveryHost.Services).RunOnceAsync(cancellationToken), "The settlement pass must run.");
    foreach (var organizationId in new[] { unavailableOrganization, partialOrganization })
    {
        var open = await BindingAsync(organizationId);
        var charges = await storage.ReadAsync(connection => connection.ExecuteScalarAsync<long>(
            "SELECT (SELECT count(*) FROM billing_overage_charges WHERE organization_id = @Id) + (SELECT count(*) FROM billing_skipped_meters WHERE organization_id = @Id)",
            new { Id = organizationId }), cancellationToken);
        Require(open.OverageStartsAt == previousMonth && charges == 0, "A month whose usage is unavailable or incomplete must stay open with nothing frozen.");
    }
    Console.WriteLine("billing usage journey: usage refused or reported incomplete -> month stays open, nothing frozen, charged or skipped");

    async Task InsertReceiptAsync(BillingBinding owner, string id, int attempts) =>
        await storage.WriteAsync(async (connection, transaction) => await connection.ExecuteAsync(JourneySql.InsertRetryingReceipt, new StripeEventReceipt
        {
            Id = id,
            Version = AccountService.NewId(),
            OrganizationId = owner.OrganizationId,
            SubscriptionId = owner.SubscriptionId,
            InvoiceId = "",
            EventType = "customer.subscription.updated",
            ReceivedAt = realNow,
            EventCreated = realNow,
            ProcessingUntil = realNow.AddMinutes(-1),
            Attempts = attempts
        }, transaction), cancellationToken);

    async Task<StripeEventReceipt?> ReceiptAsync(string id) =>
        await storage.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<StripeEventReceipt>(JourneySql.Receipt, new { Id = id }), cancellationToken);

    async Task<PendingOrganizationCheckout?> DraftRowAsync(string draftId) =>
        await storage.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<PendingOrganizationCheckout>(JourneySql.Draft, new { DraftId = draftId }), cancellationToken);

    async Task<string> ChangeStatusAsync(BillingBinding owner) =>
        await storage.ReadAsync(connection => connection.QuerySingleAsync<string>(JourneySql.ChangeStatus, new { owner.OrganizationId }), cancellationToken);

    async Task InsertChangeAsync(BillingBinding owner, DateTimeOffset changedAt) =>
        await storage.WriteAsync(async (connection, transaction) => await connection.ExecuteAsync(JourneySql.InsertChange, new BillingChangeReceipt
        {
            Id = AccountService.NewId(),
            Version = AccountService.NewId(),
            OrganizationId = owner.OrganizationId,
            SubscriptionId = owner.SubscriptionId,
            RequestId = Guid.CreateVersion7().ToString(),
            Action = "change",
            Plan = "pro",
            Interval = "monthly",
            SourcePlanKey = "starter",
            SourceInterval = "monthly",
            SourcePriceId = "price_starter_month",
            TargetPriceId = "price_pro_month",
            PeriodEnd = clock.Now.AddMonths(1),
            Revision = 1,
            Status = "applying",
            StatusChangedAt = changedAt
        }, transaction), cancellationToken);

    async Task<DraftCheckout> DraftAsync(bool paid)
    {
        var draftId = AccountService.NewId();
        var owner = AccountService.NewId();
        using var response = await control.PostAsJsonAsync("/billing-draft", new DraftInput(draftId, owner, "Recovery " + draftId, "price_starter_month", paid), BillingJson.Default.DraftInput, cancellationToken);
        response.EnsureSuccessStatusCode();
        var answer = await response.Content.ReadFromJsonAsync(BillingJson.Default.DraftAnswer, cancellationToken) ?? throw new IOException("Draft checkout is unavailable.");
        await storage.WriteAsync(async (connection, transaction) => await connection.ExecuteAsync(JourneySql.InsertDraft, new PendingOrganizationCheckout
        {
            UserId = owner,
            DraftId = draftId,
            Name = "Recovery " + draftId,
            Plan = "starter",
            Interval = "monthly",
            CheckoutId = answer.Checkout,
            CreatedAt = realNow.UtcDateTime,
            Version = AccountService.NewId()
        }, transaction), cancellationToken);
        if (paid != answer.Subscription is { Length: > 0 })
        {
            throw new IOException("A paid draft checkout must have a subscription and an unpaid one none.");
        }

        return new(draftId, answer.Checkout, answer.Customer, answer.Subscription, answer.Session);
    }

    async Task<string> ScheduleAsync(string subscription)
    {
        using var response = await control.PostAsJsonAsync("/billing-orphan-schedule", new OrphanScheduleInput(subscription), BillingJson.Default.OrphanScheduleInput, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(BillingJson.Default.ScheduleAnswer, cancellationToken) ?? throw new IOException("Schedule is unavailable.")).Schedule;
    }

    async Task<StripeLog> StripeLogAsync() =>
        await control.GetFromJsonAsync("/log", BillingJson.Default.StripeLog, cancellationToken) ?? throw new IOException("Stripe log is unavailable.");

    async Task<JsonElement> ObjectAsync(string id) =>
        await control.GetFromJsonAsync("/billing-object?id=" + Uri.EscapeDataString(id), BillingJson.Default.JsonElement, cancellationToken);

    string SubscriptionEvent(string id, BillingBinding owner) =>
        JsonSerializer.Serialize(new EventInput(id, "2026-09-30.endive", new(new("subscription", owner.SubscriptionId, owner.CustomerId)), false, "customer.subscription.updated",
            clock.Now.ToUnixTimeSeconds()), BillingJson.Default.EventInput);

    string DraftEvent(JsonElement session) =>
        JsonSerializer.Serialize(new DraftEventInput("evt_draft_" + AccountService.NewId(), "2026-09-30.endive", new(session), false, "checkout.session.completed",
            TimeProvider.System.GetUtcNow().ToUnixTimeSeconds()), BillingJson.Default.DraftEventInput);

    async Task<string> SignedAsync(StripeBilling target, string body)
    {
        var timestamp = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secrets[7]), Encoding.UTF8.GetBytes(timestamp + "." + body)));
        var outcome = await target.HandleWebhookAsync(body, "t=" + timestamp + ",v1=" + signature, cancellationToken);
        return outcome switch
        {
            Done => "applied",
            Rejected => "rejected",
            Deferred => "deferred",
            null => "missing",
        };
    }

    async Task EventuallyAsync(Func<Task<bool>> condition, string message)
    {
        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(45);
        while (!await condition())
        {
            if (TimeProvider.System.GetUtcNow() > deadline) { throw new InvalidOperationException(message); }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }
}

finally
{
    foreach (var id in created)
    {
        await storage.ReadAsync(connection => connection.ExecuteAsync("DELETE FROM stripe_event_receipts WHERE organization_id = @Id; DELETE FROM billing WHERE organization_id = @Id; DELETE FROM organizations WHERE id = @Id", new { Id = id }), CancellationToken.None);
    }
}

internal sealed class BillingClock : TimeProvider
{
    public DateTimeOffset Now { get; set; }
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed record ClockInput(string Now);
internal sealed record SubscriptionInput(string Organization, string Price, string Start, string? Customer);
internal sealed record SubscriptionIds(string Customer, string Subscription);
internal sealed record ItemTotals(int Count, long Amount);
internal sealed record ItemInput(string Customer, string Subscription, long Amount, long Quantity, string? UnitAmountDecimal, Dictionary<string, string> Metadata);
internal sealed record ItemId(string Id);
internal sealed record CancelInput(string Subscription, string At);
internal sealed record PlanChangeInput(string Subscription, string Price, string At);
internal sealed record HoldInput(string Method, string Path, string Customer);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(EmptyInput))]
[JsonSerializable(typeof(FailureInput))]
[JsonSerializable(typeof(PaymentInput))]
[JsonSerializable(typeof(TaxInput))]
[JsonSerializable(typeof(TaxState))]
[JsonSerializable(typeof(EventInput))]
[JsonSerializable(typeof(ClockInput))]
[JsonSerializable(typeof(SubscriptionInput))]
[JsonSerializable(typeof(SubscriptionIds))]
[JsonSerializable(typeof(ItemTotals))]
[JsonSerializable(typeof(ItemInput))]
[JsonSerializable(typeof(ItemId))]
[JsonSerializable(typeof(CancelInput))]
[JsonSerializable(typeof(PlanChangeInput))]
[JsonSerializable(typeof(HoldInput))]
[JsonSerializable(typeof(DraftInput))]
[JsonSerializable(typeof(DraftAnswer))]
[JsonSerializable(typeof(OrphanScheduleInput))]
[JsonSerializable(typeof(ScheduleAnswer))]
[JsonSerializable(typeof(StripeLog))]
[JsonSerializable(typeof(DraftEventInput))]
internal sealed partial class BillingJson : JsonSerializerContext;

internal sealed record DraftInput(string Draft, string Owner, string Name, string Price, bool Paid);
internal sealed record DraftAnswer(string Checkout, string Customer, string? Subscription, JsonElement Session);
internal sealed record DraftCheckout(string DraftId, string Checkout, string Customer, string? Subscription, JsonElement Session);
internal sealed record OrphanScheduleInput(string Subscription);
internal sealed record ScheduleAnswer(string Schedule);
internal sealed record StripeLog(IReadOnlyList<StripeCall> Calls, IReadOnlyList<StripeCancel> Cancels, IReadOnlyList<StripeRefund> Refunds, IReadOnlyList<StripeRelease> Releases);
internal sealed record StripeCall(string Method, string Path, int Status);
internal sealed record StripeCancel(string Subscription);
internal sealed record StripeRefund(string Subscription);
internal sealed record StripeRelease(string Schedule);
internal sealed record DraftEventInput(string Id, [property: JsonPropertyName("api_version")] string ApiVersion, DraftEventData Data, bool Livemode, string Type, long Created);
internal sealed record DraftEventData(JsonElement Object);

internal sealed record EmptyInput;
internal sealed record FailureInput(string Method, string Path, int Status, int? Remaining, bool Transport);
internal sealed record PaymentInput(string Subscription, bool Paid, bool Deliver, string? Status = null);
internal sealed record TaxState(int Updates, bool Enabled);
internal sealed record TaxInput(string Subscription, string Location, string Policy);
internal sealed record EventInput(string Id, [property: JsonPropertyName("api_version")] string ApiVersion, EventData Data, bool Livemode, string Type, long Created);
internal sealed record EventData(EventObject Object);
internal sealed record EventObject(string Object, string Id, string Customer);
