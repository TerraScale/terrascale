using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Dashboard;
using TerraScale.Dashboard.Common.Accounts;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.Persistence.Migrations;

var cancellationToken = CancellationToken.None;
var state = Environment.GetEnvironmentVariable("E2E_STATE") ?? throw new InvalidOperationException("E2E_STATE is required.");
var secrets = await File.ReadAllLinesAsync(Path.Combine(state, "secrets"), cancellationToken);
Environment.SetEnvironmentVariable("TERRABASE_PEER_SECRET", secrets[0]);
Environment.SetEnvironmentVariable("TS_DASHBOARD_SESSION_ENCRYPTION_KEY", secrets[4]);
Environment.SetEnvironmentVariable("TS_AUTH_DASHBOARD_CLIENT_SECRET", secrets[5]);
Environment.SetEnvironmentVariable("STRIPE_SECRET_KEY", secrets[6]);
Environment.SetEnvironmentVariable("STRIPE_WEBHOOK_SECRET", secrets[7]);
Environment.SetEnvironmentVariable("TS_DASHBOARD_TERRABASE_URL", "http://127.0.0.1:8081");
using var control = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:8027") };
var clock = new BillingClock();
await using var application = DashboardApplication.Build(["--environment", "Development", "--contentRoot", Environment.GetEnvironmentVariable("E2E_BUILD") + "/dashboard"],
    builder => builder.Services.AddSingleton<TimeProvider>(clock));
await using var contender = DashboardApplication.Build(["--environment", "Development", "--contentRoot", Environment.GetEnvironmentVariable("E2E_BUILD") + "/dashboard"],
    builder => builder.Services.AddSingleton<TimeProvider>(clock));
var otherReporter = contender.Services.GetRequiredService<UsageOverageReporter>();
var storage = application.Services.GetRequiredService<IDbContextFactory<DashboardDbContext>>();
var organizations = application.Services.GetRequiredService<OrganizationStore>();
var reporter = application.Services.GetRequiredService<UsageOverageReporter>();
var billing = application.Services.GetRequiredService<StripeBilling>();
var created = new List<string>();

async Task AdvanceAsync(string value)
{
    using var response = await control.PostAsJsonAsync("/billing-clock", new ClockInput(value), BillingJson.Default.ClockInput, cancellationToken);
    response.EnsureSuccessStatusCode();
    var time = await response.Content.ReadFromJsonAsync(BillingJson.Default.ClockInput, cancellationToken) ?? throw new IOException("Billing clock is unavailable.");
    clock.Now = DateTimeOffset.Parse(time.Now, CultureInfo.InvariantCulture);
}

async Task<BillingBinding> CreateAsync(DateTimeOffset start, DateTimeOffset? since)
{
    var id = AccountService.NewId();
    await organizations.CreateAsync(new() { Id = id, PlanId = OrganizationRecord.StarterPlanId, PaymentMethodValidated = true }, cancellationToken);
    created.Add(id);
    using var response = await control.PostAsJsonAsync("/billing-subscription", new SubscriptionInput(id), BillingJson.Default.SubscriptionInput, cancellationToken);
    response.EnsureSuccessStatusCode();
    var vendor = await response.Content.ReadFromJsonAsync(BillingJson.Default.SubscriptionIds, cancellationToken) ?? throw new IOException("Billing subscription is unavailable.");
    var binding = new BillingBinding
    {
        OrganizationId = id,
        Version = AccountService.NewId(),
        RequestId = AccountService.NewId(),
        State = "active",
        CustomerId = vendor.Customer,
        SubscriptionId = vendor.Subscription,
        StartedAt = start,
        EffectivePlanSince = since,
        EffectivePlan = "starter",
        EffectiveInterval = "monthly",
        Status = "active",
        Plan = "starter",
        Interval = "monthly"
    };
    BillingMonth.Observe(binding, PlanCatalog.For("starter"), since ?? start);
    binding.EffectivePlanSince = since;
    await using var db = await storage.CreateDbContextAsync(cancellationToken);
    db.Billing.Add(binding);
    db.StripeCustomers.Add(new() { Id = AccountService.NewId(), OrganizationId = id, CustomerId = vendor.Customer, CreatedAt = start.UtcDateTime });
    await db.SaveChangesAsync(cancellationToken);
    return binding;
}

async Task<BillingBinding> BindingAsync(string id)
{
    await using var db = await storage.CreateDbContextAsync(cancellationToken);
    return await db.Billing.AsNoTracking().SingleAsync(row => row.OrganizationId == id, cancellationToken);
}

async Task PlanAsync(string id, string key)
{
    var org = await organizations.FindAsync(id, cancellationToken) ?? throw new IOException("Billing organization is unavailable.");
    org.Organization.PlanId = PlanCatalog.For(key).Id;
    await organizations.ReplaceAsync(org, cancellationToken);
    await using var db = await storage.CreateDbContextAsync(cancellationToken);
    var binding = await db.Billing.SingleAsync(row => row.OrganizationId == id, cancellationToken);
    binding.EffectivePlan = key;
    BillingMonth.Observe(binding, PlanCatalog.For(key), clock.Now);
    binding.Version = AccountService.NewId();
    await db.SaveChangesAsync(cancellationToken);
}

async Task CheckAsync(BillingBinding binding, string month, long quantity)
{
    using var response = await control.GetAsync("/billing-items?customer=" + binding.CustomerId + "&month=" + month, cancellationToken);
    response.EnsureSuccessStatusCode();
    var items = await response.Content.ReadFromJsonAsync(BillingJson.Default.ItemTotals, cancellationToken) ?? throw new IOException("Billing items are unavailable.");
    if (items.Quantity != quantity || items.Count != (quantity > 0 ? 1 : 0))
    {
        throw new InvalidOperationException($"Overage {month}: expected one charge of {quantity}, got {items.Count} items totaling {items.Quantity}.");
    }
}

async Task ControlAsync(string path, object input)
{
    var json = JsonSerializer.SerializeToElement(input, input.GetType(), BillingJson.Default);
    using var response = await control.PostAsJsonAsync(path, json, BillingJson.Default.JsonElement, cancellationToken);
    response.EnsureSuccessStatusCode();
}

async Task<OverageCharge> ChargeAsync(BillingBinding binding, DateTimeOffset month)
{
    await using var db = await storage.CreateDbContextAsync(cancellationToken);
    return await db.OverageCharges.AsNoTracking().SingleAsync(row => row.OrganizationId == binding.OrganizationId && row.SubscriptionId == binding.SubscriptionId && row.Month == month && row.Meter == OperationUnitOverage.Kind, cancellationToken);
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
        throw new InvalidOperationException("Payment grace entitlement mismatch.");
    }
}

async Task EventAsync(BillingBinding binding, string eventId)
{
    var body = JsonSerializer.Serialize(new EventInput(eventId, "2026-09-30.endive", new(new("subscription", binding.SubscriptionId, binding.CustomerId)), false, "customer.subscription.updated", clock.Now.ToUnixTimeSeconds()), BillingJson.Default.EventInput);
    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
    var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secrets[7]), Encoding.UTF8.GetBytes(timestamp + "." + body)));
    await billing.HandleWebhookAsync(body, "t=" + timestamp + ",v1=" + signature, cancellationToken);
}

try
{
    await AdvanceAsync("2028-01-15T12:00:00Z");
    var binding = await CreateAsync(clock.Now, clock.Now);
    var january = new DateTimeOffset(2028, 1, 1, 0, 0, 0, TimeSpan.Zero);
    await AdvanceAsync("2028-02-01T00:00:00Z");
    await BillingMonth.RecordAsync(storage, await BindingAsync(binding.OrganizationId), clock.Now, cancellationToken);
    await Task.WhenAll(reporter.ReportAsync(binding.OrganizationId, 1_250_000, 0, january, cancellationToken), otherReporter.ReportAsync(binding.OrganizationId, 2_250_000, 0, january, cancellationToken));
    await using (var db = await storage.CreateDbContextAsync(cancellationToken))
    {
        if (await db.OverageCharges.CountAsync(row => row.OrganizationId == binding.OrganizationId && row.Month == january, cancellationToken) != 2) { throw new InvalidOperationException("The racing freeze must leave exactly one row per meter."); }
    }
    var frozen = await ChargeAsync(binding, january);
    await CheckAsync(binding, "2028-01", frozen.Quantity);
    if (frozen.ProviderId is null || frozen.Allowance != 250_000 || frozen.AmountCents != (long)Math.Round(frozen.Quantity * frozen.UnitRate, MidpointRounding.AwayFromZero)) { throw new InvalidOperationException("Frozen pricing mismatch."); }
    Console.WriteLine("billing ledger journey: racing reporters -> one provider object, one row per meter, winner quantity and amount");

    var delayed = await CreateAsync(january, january);
    await BillingMonth.RecordAsync(storage, await BindingAsync(delayed.OrganizationId), clock.Now, cancellationToken);
    await ControlAsync("/failure", new FailureInput("POST", "/v1/invoiceitems", 400, 1, false));
    await reporter.ReportAsync(delayed.OrganizationId, 1_250_000, 0, january, cancellationToken);
    if ((await ChargeAsync(delayed, january)).ProviderId is not null) { throw new InvalidOperationException("Failed January must remain pending."); }
    await AdvanceAsync("2028-03-01T00:00:00Z");
    await BillingMonth.RecordAsync(storage, await BindingAsync(delayed.OrganizationId), clock.Now, cancellationToken);
    await reporter.ReportAsync(delayed.OrganizationId, 2_250_000, 0, january.AddMonths(1), cancellationToken);
    await CheckAsync(delayed, "2028-02", 2_000_000);
    await reporter.ReportAsync(delayed.OrganizationId, 3_250_000, 0, january.AddMonths(1), cancellationToken);
    await CheckAsync(delayed, "2028-02", 2_000_000);
    await reporter.RunCurrentAsync(cancellationToken);
    await CheckAsync(delayed, "2028-01", 1_000_000);
    Console.WriteLine("billing ledger journey: January failed; February succeeded; late February ignored; January recovered independently");

    await BillingMonth.RecordAsync(storage, await BindingAsync(binding.OrganizationId), clock.Now, cancellationToken);
    await using (var db = await storage.CreateDbContextAsync(cancellationToken))
    {
        var changed = await db.Billing.SingleAsync(row => row.OrganizationId == binding.OrganizationId, cancellationToken);
        changed.ObservedOperationRate = 10m;
        changed.ObservedOperationsAllowance = 1;
        changed.ObservedPlanName = "Changed catalog";
        changed.ObservedPricingVersion = "changed";
        changed.Version = AccountService.NewId();
        await db.SaveChangesAsync(cancellationToken);
    }
    await reporter.ReportAsync(binding.OrganizationId, 1_250_000, 0, january.AddMonths(1), cancellationToken);
    var historical = await ChargeAsync(binding, january.AddMonths(1));
    if (historical.UnitRate != frozen.UnitRate || historical.Allowance != frozen.Allowance || historical.Quantity != 1_000_000) { throw new InvalidOperationException("Unreported closed month used today's catalog."); }
    await CheckAsync(binding, "2028-02", 1_000_000);
    await reporter.ReportAsync(binding.OrganizationId, 9_250_000, 0, january, cancellationToken);
    var unchanged = await ChargeAsync(binding, january);
    if (unchanged.UnitRate != frozen.UnitRate || unchanged.AmountCents != frozen.AmountCents || unchanged.PlanName != frozen.PlanName) { throw new InvalidOperationException("Catalog change rewrote the charge."); }
    await CheckAsync(binding, "2028-01", frozen.Quantity);
    await PlanAsync(delayed.OrganizationId, "pro");
    await AdvanceAsync("2028-04-01T00:00:00Z");
    await BillingMonth.RecordAsync(storage, await BindingAsync(delayed.OrganizationId), clock.Now, cancellationToken);
    await PlanAsync(delayed.OrganizationId, "starter");
    await reporter.ReportAsync(delayed.OrganizationId, 1_250_000, 0, january.AddMonths(2), cancellationToken);
    await CheckAsync(delayed, "2028-03", 0);
    Console.WriteLine("billing ledger journey: changed catalog preserves closed-month terms and charge; downgrade uses observed plan");

    await using (var db = await storage.CreateDbContextAsync(cancellationToken))
    {
        var accepted = await db.OverageCharges.SingleAsync(row => row.Id == frozen.Id, cancellationToken);
        accepted.ProviderId = null;
        accepted.AcceptedAt = null;
        await db.SaveChangesAsync(cancellationToken);
    }
    await ControlAsync("/billing-forget-idempotency", new EmptyInput());
    await reporter.RunCurrentAsync(cancellationToken);
    await CheckAsync(binding, "2028-01", frozen.Quantity);
    if ((await ChargeAsync(binding, january)).ProviderId != frozen.ProviderId) { throw new InvalidOperationException("Metadata recovery lost the accepted object."); }
    Console.WriteLine("billing ledger journey: accepted object without local receipt; provider forgot key; metadata lookup -> no create");

    var existing = await CreateAsync(january.AddYears(-1), null);
    await using (var db = await storage.CreateDbContextAsync(cancellationToken))
    {
        var sql = new RecordBillingTerms().UpOperations.OfType<SqlOperation>().Single().Sql.Replace("'now'", "{0}", StringComparison.Ordinal) + " AND organization_id = {1}";
        await db.Database.ExecuteSqlRawAsync(sql, [clock.Now.ToString("O", CultureInfo.InvariantCulture), existing.OrganizationId], cancellationToken);
    }
    if ((await BindingAsync(existing.OrganizationId)).OverageStartsAt != january.AddMonths(4)) { throw new InvalidOperationException("Deployment must exclude the deployment month and all earlier months."); }
    await reporter.ReportAsync(existing.OrganizationId, 1_250_000, 0, january, cancellationToken);
    await CheckAsync(existing, "2028-01", 0);
    await reporter.ReportAsync(existing.OrganizationId, 1_250_000, 0, january.AddMonths(3), cancellationToken);
    await CheckAsync(existing, "2028-04", 0);
    await AdvanceAsync("2028-06-01T00:00:00Z");
    await reporter.ReportAsync(existing.OrganizationId, 1_250_000, 0, january.AddMonths(4), cancellationToken);
    await using (var db = await storage.CreateDbContextAsync(cancellationToken))
    {
        if (!await db.SkippedBillingMonths.AnyAsync(row => row.OrganizationId == existing.OrganizationId && row.Month == january.AddMonths(4), cancellationToken)) { throw new InvalidOperationException("Unknown plan must have a permanent skipped-month record."); }
    }
    Console.WriteLine("billing migration journey: deployment month and earlier excluded; missing historical plan permanently skipped");

    await SetGraceAsync(binding);
    var paid = await billing.ResolveExpiredGraceAsync(binding.OrganizationId, cancellationToken);
    if (paid.PaymentGraceUntil is not null || paid.PlanId != OrganizationRecord.StarterPlanId || paid.BillingStatus != "active") { throw new InvalidOperationException("Canonical paid subscription must clear expired grace."); }
    Console.WriteLine("billing grace journey: expired grace with provider paid -> clear grace, retain plan");
    await ControlAsync("/payment", new PaymentInput(binding.SubscriptionId, false, false));
    await SetGraceAsync(binding);
    await ControlAsync("/failure", new FailureInput("GET", "/v1/subscriptions/" + binding.SubscriptionId, 400, 3, true));
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

    await using (var db = await storage.CreateDbContextAsync(cancellationToken))
    {
        var sql = new AddPaymentGrace().UpOperations.OfType<SqlOperation>().Single().Sql.Replace("'now'", "{0}", StringComparison.Ordinal) + " AND id = {1}";
        await db.Database.ExecuteSqlRawAsync(sql, [clock.Now.ToString("O", CultureInfo.InvariantCulture), binding.OrganizationId], cancellationToken);
    }
    var migrated = await organizations.FindAsync(binding.OrganizationId, cancellationToken) ?? throw new IOException("Organization missing.");
    var deadline = new DateTimeOffset(clock.Now.UtcDateTime.Date.AddDays(8).AddMilliseconds(-1), TimeSpan.Zero);
    if (migrated.Organization.PaymentGraceUntil != deadline || migrated.Organization.BillingStatus != "active") { throw new InvalidOperationException("Migrated payment failure must receive the full seven-day grace."); }
    Console.WriteLine("billing grace journey: existing payment failure at migration -> failure day + 7 days, UTC day end");

    await ControlAsync("/payment", new PaymentInput(binding.SubscriptionId, true, false));
    await ControlAsync("/failure", new FailureInput("POST", "/v1/subscriptions/" + binding.SubscriptionId, 400, 3, true));
    await ControlAsync("/subscription-tax", new TaxInput(binding.SubscriptionId, "supported", "new"));
    await AssertGraceAsync(binding, false, "active");
    using (var response = await control.GetAsync("/subscription-tax-state?subscription=" + binding.SubscriptionId, cancellationToken))
    {
        response.EnsureSuccessStatusCode();
        var tax = await response.Content.ReadFromJsonAsync(BillingJson.Default.TaxState, cancellationToken) ?? throw new IOException("Tax state missing.");
        if (tax.Enabled) { throw new InvalidOperationException("The transport failure must leave tax repair pending after entitlement commits."); }
    }
    Console.WriteLine("billing tax journey: tax repair transport failure -> entitlement restored and grace cleared");
    var eventId = "evt_receipt_" + AccountService.NewId();
    await EventAsync(binding, eventId);
    var version = (await BindingAsync(binding.OrganizationId)).Version;
    await EventAsync(binding, eventId);
    if ((await BindingAsync(binding.OrganizationId)).Version != version) { throw new InvalidOperationException("A replayed event changed billing state."); }
    Console.WriteLine("billing webhook journey: replayed provider event -> acknowledged, no billing write");
}

finally
{
    await using var db = await storage.CreateDbContextAsync(cancellationToken);
    db.Organizations.RemoveRange(await db.Organizations.Where(row => created.Contains(row.Id)).ToListAsync(cancellationToken));
    await db.SaveChangesAsync(cancellationToken);
}

internal sealed class BillingClock : TimeProvider
{
    public DateTimeOffset Now { get; set; }
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed record ClockInput(string Now);
internal sealed record SubscriptionInput(string Organization);
internal sealed record SubscriptionIds(string Customer, string Subscription);
internal sealed record ItemTotals(int Count, long Quantity);

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
internal sealed partial class BillingJson : JsonSerializerContext;

internal sealed record EmptyInput;
internal sealed record FailureInput(string Method, string Path, int Status, int Remaining, bool Transport);
internal sealed record PaymentInput(string Subscription, bool Paid, bool Deliver);
internal sealed record TaxState(int Updates, bool Enabled);
internal sealed record TaxInput(string Subscription, string Location, string Policy);
internal sealed record EventInput(string Id, [property: JsonPropertyName("api_version")] string ApiVersion, EventData Data, bool Livemode, string Type, long Created);
internal sealed record EventData(EventObject Object);
internal sealed record EventObject(string Object, string Id, string Customer);
