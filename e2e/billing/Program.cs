using System.Globalization;
using System.Net.Http.Json;
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
var storage = application.Services.GetRequiredService<IDbContextFactory<DashboardDbContext>>();
var organizations = application.Services.GetRequiredService<OrganizationStore>();
var reporter = application.Services.GetRequiredService<UsageOverageReporter>();
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
    await using var db = await storage.CreateDbContextAsync(cancellationToken);
    db.Billing.Add(binding);
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
    binding.EffectivePlanSince = clock.Now;
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

try
{
    await AdvanceAsync("2026-01-15T12:00:00Z");
    var binding = await CreateAsync(clock.Now, clock.Now);
    var january = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    await AdvanceAsync("2026-02-01T00:00:00Z");
    await BillingMonth.RecordAsync(storage, await BindingAsync(binding.OrganizationId), clock.Now, cancellationToken);
    await reporter.ReportAsync(binding.OrganizationId, 250_010, 0, january, cancellationToken);
    await CheckAsync(binding, "2026-01", 10);
    await reporter.ReportAsync(binding.OrganizationId, 250_010, 0, january, cancellationToken);
    await CheckAsync(binding, "2026-01", 10);
    await AdvanceAsync("2026-03-01T00:00:00Z");
    await BillingMonth.RecordAsync(storage, await BindingAsync(binding.OrganizationId), clock.Now, cancellationToken);
    await AdvanceAsync("2026-04-01T00:00:00Z");
    await BillingMonth.RecordAsync(storage, await BindingAsync(binding.OrganizationId), clock.Now, cancellationToken);
    await reporter.ReportAsync(binding.OrganizationId, 250_020, 0, january.AddMonths(1), cancellationToken);
    await reporter.ReportAsync(binding.OrganizationId, 250_030, 0, january.AddMonths(2), cancellationToken);
    await CheckAsync(binding, "2026-02", 20);
    await CheckAsync(binding, "2026-03", 30);
    await PlanAsync(binding.OrganizationId, "pro");
    await AdvanceAsync("2026-05-01T00:00:00Z");
    await BillingMonth.RecordAsync(storage, await BindingAsync(binding.OrganizationId), clock.Now, cancellationToken);
    await PlanAsync(binding.OrganizationId, "starter");
    await reporter.ReportAsync(binding.OrganizationId, 250_100, 0, january.AddMonths(3), cancellationToken);
    await CheckAsync(binding, "2026-04", 0);

    var missed = await CreateAsync(january, january);
    await BillingMonth.RecordAsync(storage, await BindingAsync(missed.OrganizationId), clock.Now, cancellationToken);
    for (var month = january; month < january.AddMonths(3); month = month.AddMonths(1))
    {
        await reporter.ReportAsync(missed.OrganizationId, 250_200, 0, month, cancellationToken);
        await CheckAsync(missed, month.ToString("yyyy-MM", CultureInfo.InvariantCulture), 0);
    }
    await reporter.ReportAsync(missed.OrganizationId, 250_040, 0, january.AddMonths(3), cancellationToken);
    await CheckAsync(missed, "2026-04", 40);

    var existing = await CreateAsync(january.AddYears(-1), null);
    await using (var db = await storage.CreateDbContextAsync(cancellationToken))
    {
        var sql = new AddBillingProgress().UpOperations.OfType<SqlOperation>().Single().Sql
            .Replace("'now'", "{0}", StringComparison.Ordinal) + " AND organization_id = {1}";
        await db.Database.ExecuteSqlRawAsync(sql, [clock.Now.ToString("O", CultureInfo.InvariantCulture), existing.OrganizationId], cancellationToken);
    }
    var migrated = await BindingAsync(existing.OrganizationId);
    if (migrated.OverageReportedThrough != january.AddMonths(2))
    {
        throw new InvalidOperationException("Deployment watermark must exclude every month before the latest complete one.");
    }
    await reporter.ReportAsync(existing.OrganizationId, 250_200, 0, january.AddMonths(1), cancellationToken);
    await CheckAsync(existing, "2026-02", 0);
    await reporter.ReportAsync(existing.OrganizationId, 250_200, 0, january.AddMonths(3), cancellationToken);
    await CheckAsync(existing, "2026-04", 0);
    if ((await BindingAsync(existing.OrganizationId)).OverageReportedThrough != january.AddMonths(3))
    {
        throw new InvalidOperationException("A missing historical plan must be skipped permanently.");
    }
    Console.WriteLine("billing overage journey: completed month once; repeated pass unchanged; skipped pass caught up; downgrade uses recorded plan; migration excludes old months; unknown plan skipped");
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
[JsonSerializable(typeof(ClockInput))]
[JsonSerializable(typeof(SubscriptionInput))]
[JsonSerializable(typeof(SubscriptionIds))]
[JsonSerializable(typeof(ItemTotals))]
internal sealed partial class BillingJson : JsonSerializerContext;
