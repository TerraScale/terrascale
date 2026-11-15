using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUlid;
using TerraScale.Dashboard;
using TerraScale.Dashboard.Common.Accounts;
using TerraScale.Dashboard.Features.Logs;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.DotNet.TerraBase;
using TerraScale.Dashboard.Features.Notifications;
using TerraScale.Dashboard.Platform.Http;
using TerraScale.Dashboard.Platform.Persistence;

internal static class BackgroundJourney
{
    public static async Task WorkAsync(string kind, DateTimeOffset at, string path, CancellationToken cancellationToken)
    {
        var clock = new BillingClock { Now = at };
        var owner = await OwnerAsync(path, cancellationToken);
        var commands = new BackgroundCommands();
        await using var application = DashboardApplication.Build(["--environment", "Development", "--contentRoot", Environment.GetEnvironmentVariable("E2E_BUILD") + "/dashboard"],
            builder => { builder.Services.AddSingleton<TimeProvider>(clock); BackgroundStorage.Configure(builder.Services, owner, commands); });
        switch (kind)
        {
            case "drain":
                await application.Services.GetRequiredService<LogDrains>().RunOnceAsync(cancellationToken);
                break;
            case "skew":
                await application.Services.GetRequiredService<BackgroundJobs>().RunAsync("clock-skew", TimeSpan.FromHours(1), async (claim, token) =>
                {
                    claim.State.Cursor = "skewed-clock";
                    claim.State.LeaseUntil = at.AddMinutes(2).UtcDateTime;
                    claim.State.AvailableAt = at.AddHours(1).UtcDateTime;
                    await claim.SaveAsync(token);
                }, cancellationToken);
                break;
            case "alert":
                await application.Services.GetRequiredService<AlertRules>().RunOnceAsync(cancellationToken);
                break;
            default:
                throw new InvalidOperationException("Unknown background worker.");
        }
    }

    public static async Task RunAsync(string path, CancellationToken cancellationToken)
    {
        var owner = await OwnerAsync(path, cancellationToken);
        var now = TimeProvider.System.GetUtcNow();
        var future = now.AddDays(1);
        await using var original = DashboardApplication.Build(["--environment", "Development", "--contentRoot", Environment.GetEnvironmentVariable("E2E_BUILD") + "/dashboard"]);
        var organization = (await original.Services.GetRequiredService<OrganizationStore>().FindAsync(owner.OrganizationId, cancellationToken))?.Organization
            ?? throw new IOException("The journey organization is missing.");
        organization.Projects[owner.ProjectId].Databases = organization.Projects[owner.ProjectId].Databases.Where(pair => pair.Key == owner.DatabaseId).ToDictionary();
        using var client = new HttpClient(TerraBaseHttp.CreateHandler());
        var sql = new TerraBaseSqlClient(client, new() { ApiKey = owner.ApiKey, Region = owner.DatabaseRegion.ToLowerInvariant() }).Database(owner.ProjectId, owner.DatabaseName);
        await sql.ExecuteAsync("SELECT 121", cancellationToken: cancellationToken);
        await sql.ExecuteAsync("SELECT 122", cancellationToken: cancellationToken);
        var commands = new BackgroundCommands();
        await using var application = DashboardApplication.Build(["--environment", "Development", "--contentRoot", Environment.GetEnvironmentVariable("E2E_BUILD") + "/dashboard"],
            builder => BackgroundStorage.Configure(builder.Services, owner, commands));
        var storage = application.Services.GetRequiredService<IDbContextFactory<DashboardDbContext>>();
        var identity = await application.Services.GetRequiredService<IDashboardIdentity>().ReadAsync(cancellationToken);
        using var receiver = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:8029") };
        var run = AccountService.NewId();
        var drainPath = "/dashboard-drain/" + run;
        var alertPath = "/dashboard-alert/" + run;
        var revision = AccountService.NewId();
        var drainId = AccountService.NewId();
        var databaseId = owner.DatabaseId;
        var jobId = "log-drain/" + owner.ProjectId + "/" + revision + "/" + databaseId;
        string deliveryId;
        byte[] payload;
        string nextCursor;
        var ruleId = AccountService.NewId();
        var alertId = AccountService.NewId();
        var name = "C121 " + run;
        var secret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var state = Environment.GetEnvironmentVariable("E2E_STATE") ?? throw new IOException("E2E_STATE is required.");
        await File.WriteAllTextAsync(Path.Combine(state, "secrets.d", "background-" + run), secret, cancellationToken);
        var sealedSecret = SealedSecret.Seal(secret, identity.SessionEncryptionKey, "alert-webhook/" + owner.ProjectId + "/" + ruleId);
        await ConfigureAsync(receiver, drainPath, true, cancellationToken);
        await ConfigureAsync(receiver, alertPath, true, cancellationToken);
        try
        {
            await using (var schema = await storage.CreateDbContextAsync(cancellationToken)) { await schema.Database.MigrateAsync(cancellationToken); }
            await application.Services.GetRequiredService<OrganizationStore>().CreateAsync(organization, cancellationToken);
            await using (var db = await storage.CreateDbContextAsync(cancellationToken))
            {
                db.LogDrains.Add(new()
                {
                    Id = drainId,
                    ProjectId = owner.ProjectId,
                    OrganizationId = owner.OrganizationId,
                    Revision = revision,
                    Url = "https://hooks.journey.terrascale.tech" + drainPath,
                    CreatedAt = now.AddMinutes(-5).UtcDateTime
                });
                await db.SaveChangesAsync(cancellationToken);
            }
            using var crashed = Start("drain", future, path);
            await ReceivedAsync(receiver, drainPath, cancellationToken);
            await using (var db = await storage.CreateDbContextAsync(cancellationToken))
            {
                var pending = await db.BackgroundJobs.AsNoTracking().SingleAsync(row => row.Id == jobId, cancellationToken);
                payload = pending.Payload ?? throw new IOException("The worker did not persist its source payload.");
                deliveryId = pending.DeliveryId ?? throw new IOException("The worker did not persist its delivery identity.");
                nextCursor = pending.NextCursor ?? throw new IOException("The worker did not persist its source cursor.");
                var sourceRecords = JsonDocument.Parse(payload).RootElement.GetProperty("resourceLogs")[0].GetProperty("scopeLogs")[0].GetProperty("logRecords");
                Require(sourceRecords.GetArrayLength() >= 2, "The worker must read real SQL activity from the production log source.");
                var databaseNow = await db.BackgroundJobs.Where(row => row.Id == jobId).Select(_ => DateTime.UtcNow).SingleAsync(cancellationToken);
                Require(pending.LeaseUntil > databaseNow && pending.LeaseUntil <= databaseNow.AddMinutes(2), "A forward-skewed worker extended its lease beyond database time.");
                Require(!await db.LogDrainCursors.AnyAsync(row => row.DrainId == drainId && row.DatabaseId == databaseId, cancellationToken), "The cursor advanced before receiver acceptance.");
            }
            using (var rival = Start("drain", future, path)) { await FinishedAsync(rival, cancellationToken); }
            Require((await DeliveriesAsync(receiver, drainPath, cancellationToken)).Length == 1, "A second live dashboard sent an already claimed log batch.");
            crashed.Kill(entireProcessTree: true);
            await crashed.WaitForExitAsync(cancellationToken);
            await using (var db = await storage.CreateDbContextAsync(cancellationToken))
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE background_jobs SET lease_until = strftime('%Y-%m-%d %H:%M:%f', 'now', '-1 second') WHERE id = {jobId}", cancellationToken);
            }
            await ConfigureAsync(receiver, drainPath, false, cancellationToken);
            using (var recovered = Start("drain", now.AddDays(-1), path)) { await FinishedAsync(recovered, cancellationToken); }
            var attempts = await DeliveriesAsync(receiver, drainPath, cancellationToken);
            Require(attempts.Length == 2 && attempts.All(row => row.GetProperty("id").GetString() == deliveryId && row.GetProperty("valid").GetBoolean()),
                "Crash recovery must resend the identical delivery key and bytes exactly once.");
            var effects = await JsonAsync(receiver, "/dashboard-effects?path=" + Uri.EscapeDataString(drainPath), cancellationToken);
            Require(effects.GetArrayLength() == 1, "The replay created another receiver effect.");
            var records = effects[0].GetProperty("event").GetProperty("resourceLogs")[0].GetProperty("scopeLogs")[0].GetProperty("logRecords");
            var originalRecords = JsonDocument.Parse(payload).RootElement.GetProperty("resourceLogs")[0].GetProperty("scopeLogs")[0].GetProperty("logRecords");
            Require(records.GetRawText() == originalRecords.GetRawText(), "The recovered worker changed the claimed source events.");
            await using (var db = await storage.CreateDbContextAsync(cancellationToken))
            {
                var saved = await db.LogDrainCursors.AsNoTracking().SingleAsync(row => row.DrainId == drainId && row.DatabaseId == databaseId, cancellationToken);
                var completed = await db.BackgroundJobs.AsNoTracking().SingleAsync(row => row.Id == jobId, cancellationToken);
                Require(saved.Cursor == nextCursor && completed.Payload is null && completed.DeliveryId is null, "The cursor and completion must commit together.");
                db.AlertRules.Add(new()
                {
                    Id = ruleId,
                    OrganizationId = owner.OrganizationId,
                    ProjectId = owner.ProjectId,
                    Slot = 19,
                    Name = name,
                    Metric = "operation_units_percent",
                    Threshold = 1_000_000,
                    Email = true,
                    State = "ok",
                    CreatedAt = now.UtcDateTime,
                    Version = AccountService.NewId(),
                    WebhookUrl = "https://hooks.journey.terrascale.tech" + alertPath,
                    WebhookNonce = sealedSecret.Nonce,
                    WebhookCiphertext = sealedSecret.Ciphertext,
                    WebhookTag = sealedSecret.Tag
                });
                db.AlertEvents.Add(new()
                {
                    Id = AccountService.NewId(),
                    RuleId = ruleId,
                    AlertId = alertId,
                    State = "firing",
                    Value = 1_000_001,
                    At = now.UtcDateTime,
                    EmailStatus = "pending",
                    WebhookStatus = "pending",
                    WebhookAttempts = 0
                });
                await db.SaveChangesAsync(cancellationToken);
            }
            using var sending = Start("alert", now, path);
            await ReceivedAsync(receiver, alertPath, cancellationToken);
            using (var rival = Start("alert", now, path)) { await FinishedAsync(rival, cancellationToken); }
            Require((await DeliveriesAsync(receiver, alertPath, cancellationToken)).Length == 1, "Two dashboard instances sent one pending alert.");
            using (var released = await receiver.PostAsJsonAsync("/release", new BackgroundRelease(alertPath), BackgroundJson.Default.BackgroundRelease, cancellationToken)) { released.EnsureSuccessStatusCode(); }
            await FinishedAsync(sending, cancellationToken);
            using var mail = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:8025") };
            var messages = await JsonAsync(mail, "/api/v1/messages?limit=1000", cancellationToken);
            Require(messages.GetProperty("messages").EnumerateArray().Count(row => row.GetProperty("Subject").GetString()?.Contains(name, StringComparison.Ordinal) == true) == 1,
                "One alert transition must send one owner notification.");
            await using (var db = await storage.CreateDbContextAsync(cancellationToken))
            {
                Require(!await db.AlertEvents.AnyAsync(row => row.RuleId == ruleId, cancellationToken), "A delivered alert must leave the pending queue.");
            }
            using (var skewed = Start("skew", now.AddYears(10), path)) { await FinishedAsync(skewed, cancellationToken); }
            await using (var db = await storage.CreateDbContextAsync(cancellationToken))
            {
                var skewed = await db.BackgroundJobs.AsNoTracking().Where(row => row.Id == "clock-skew")
                    .Select(row => new { State = row, Now = DateTime.UtcNow }).SingleAsync(cancellationToken);
                Require(skewed.State.LeaseUntil <= skewed.Now && skewed.State.AvailableAt > skewed.Now && skewed.State.AvailableAt <= skewed.Now.AddHours(1),
                    "A skewed owner pushed its lease or next run beyond database-clock bounds.");
            }
            Console.WriteLine("background journey: two dashboard processes, one log batch effect, crash after receiver acceptance, real source payload, same delivery key and bytes, fenced cursor commit");
            Console.WriteLine("background journey: two dashboard processes, one alert webhook and one owner email, pending event removed after delivery");
        }
        finally
        {
            await using var db = await storage.CreateDbContextAsync(CancellationToken.None);
            await db.AlertRules.Where(row => row.Id == ruleId).ExecuteDeleteAsync(CancellationToken.None);
            await db.BackgroundJobs.Where(row => row.Id == "alerts/" + owner.ProjectId).ExecuteDeleteAsync(CancellationToken.None);
        }
    }

    public static async Task ClaimsAsync(string path, CancellationToken cancellationToken)
    {
        var owner = await OwnerAsync(path, cancellationToken);
        var commands = new BackgroundCommands();
        await using var application = DashboardApplication.Build(["--environment", "Development", "--contentRoot", Environment.GetEnvironmentVariable("E2E_BUILD") + "/dashboard"],
            builder => BackgroundStorage.Configure(builder.Services, owner, commands));
        var storage = application.Services.GetRequiredService<IDbContextFactory<DashboardDbContext>>();
        var organization = (await application.Services.GetRequiredService<OrganizationStore>().FindAsync(owner.OrganizationId, cancellationToken))?.Organization
            ?? throw new IOException("The isolated journey organization is missing.");
        try
        {
            await LeaseProofAsync(application.Services, commands, storage, cancellationToken);
            await CountsAsync(application.Services, commands, organization, cancellationToken);
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            var drain = await db.LogDrains.SingleAsync(cancellationToken);
            drain.Url = "://invalid-stored-url";
            var cursor = await db.LogDrainCursors.SingleAsync(row => row.DrainId == drain.Id && row.DatabaseId == owner.DatabaseId, cancellationToken);
            cursor.Cursor = Ulid.MinValue.ToString();
            await db.SaveChangesAsync(cancellationToken);
            await application.Services.GetRequiredService<LogDrains>().RunOnceAsync(cancellationToken);
            await db.Entry(drain).ReloadAsync(cancellationToken);
            Require(drain.LastErrorStatus == 0 && drain.LastErrorAt is not null, "An invalid drain URL escaped No response recording.");
            Console.WriteLine("background transport: invalid stored URL is contained and records LastErrorAt with LastErrorStatus=0");
        }
        finally
        {
            await using var db = await storage.CreateDbContextAsync(CancellationToken.None);
            await db.LogDrains.ExecuteDeleteAsync(CancellationToken.None);
            await db.BackgroundJobs.ExecuteDeleteAsync(CancellationToken.None);
        }
    }

    private static async Task<BackgroundOwner> OwnerAsync(string path, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize(await File.ReadAllTextAsync(path, cancellationToken), BackgroundJson.Default.BackgroundOwner)
            ?? throw new IOException("The journey owner is missing.");

    private static async Task LeaseProofAsync(IServiceProvider services, BackgroundCommands commands, IDbContextFactory<DashboardDbContext> storage, CancellationToken cancellationToken)
    {
        var jobs = services.GetRequiredService<BackgroundJobs>();
        var id = "fence/" + AccountService.NewId();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = jobs.RunAsync(id, TimeSpan.Zero, async (claim, token) =>
        {
            claim.State.Cursor = "first";
            await claim.SaveAsync(token);
            entered.SetResult();
            await resume.Task.WaitAsync(token);
            claim.State.Cursor = "stale";
            await claim.SaveAsync(token);
        }, cancellationToken);
        await entered.Task.WaitAsync(cancellationToken);
        await using (var db = await storage.CreateDbContextAsync(cancellationToken))
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE background_jobs SET lease_until = strftime('%Y-%m-%d %H:%M:%f', 'now', '-1 second') WHERE id = {id}", cancellationToken);
        }
        Require(await jobs.RunAsync(id, TimeSpan.Zero, async (claim, token) =>
        {
            claim.State.Cursor = "successor";
            await claim.SaveAsync(token);
        }, cancellationToken), "A successor failed to acquire an expired lease.");
        resume.SetResult();
        var fenced = false;
        try { await stale; }
        catch (DbUpdateConcurrencyException) { fenced = true; }
        Require(fenced, "A real stale Claim.SaveAsync must fail its owner/checkpoint fence.");
        var renewal = "renewal/" + AccountService.NewId();
        commands.RefuseRenewal();
        await jobs.RunAsync(renewal, TimeSpan.Zero, async (claim, token) =>
        {
            claim.State.Cursor = "before-renewal";
            await claim.SaveAsync(token);
            await commands.RenewalObserved.Task.WaitAsync(token);
            Require(!token.IsCancellationRequested, "One transient renewal failure canceled a still-proven lease.");
            claim.State.Cursor = "after-renewal";
            await claim.SaveAsync(token);
        }, cancellationToken);
        Console.WriteLine("background lease: real stale Claim.SaveAsync refused; forward/backward wall clocks cannot steal or extend leases; transient renewal failure retains proven work");
    }

    private static async Task CountsAsync(IServiceProvider services, BackgroundCommands commands, OrganizationRecord organization, CancellationToken cancellationToken)
    {
        var plans = services.GetRequiredService<TerraBasePlanSync>();
        await plans.PublishAsync(organization, cancellationToken);
        await using (var db = await services.GetRequiredService<IDbContextFactory<DashboardDbContext>>().CreateDbContextAsync(cancellationToken))
        {
            Require(await db.BackgroundJobs.AsNoTracking().AnyAsync(row => row.Id == "plan/" + organization.Id && row.SucceededAt != null, cancellationToken),
                "The call-count proof requires a successfully published plan.");
        }
        commands.Reset();
        await plans.PublishAsync(organization, cancellationToken);
        var request = commands.Counts;
        Require(request.Writes == 0, "A settled plan view wrote a claim.");
        commands.Reset();
        await ActivatorUtilities.CreateInstance<PlanReconciler>(services).RunOnceAsync(cancellationToken);
        var pass = commands.Counts;
        Require(pass.Writes == 0, "An idle reconciler pass wrote claims.");
        var drains = services.GetRequiredService<LogDrains>();
        await drains.RunOnceAsync(cancellationToken);
        await using (var db = await services.GetRequiredService<IDbContextFactory<DashboardDbContext>>().CreateDbContextAsync(cancellationToken))
        {
            var cursor = await db.LogDrainCursors.AsNoTracking().SingleAsync(cancellationToken);
            var project = organization.Projects.Values.Single();
            var batch = await services.GetRequiredService<ILogDrainSource>().ReadAsync(project.Id, cursor.DatabaseId, cursor.Cursor, cancellationToken);
            Require(batch.Items.Count == 0 && !batch.Gap, $"Idle measurement requires an empty source: items={batch.Items.Count}, gap={batch.Gap}.");
        }
        commands.Reset();
        for (var database = 0; database < 100; database++) { await services.GetRequiredService<LogDrains>().RunOnceAsync(cancellationToken); }
        var idle = commands.Counts;
        Require(idle.Writes == 0, $"Idle source reads wrote drain claims or cursors: calls={idle.Calls}, writes={idle.Writes}, last={commands.LastWrite}.");
        Console.WriteLine($"background calls: settled plan request calls={request.Calls} writes={request.Writes}; idle reconciliation pass calls={pass.Calls} writes={pass.Writes}; 100 idle drain checks calls={idle.Calls} writes={idle.Writes}; projected 100 idle databases writes/min=0");
    }

    private static Process Start(string kind, DateTimeOffset at, string path)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(Environment.GetEnvironmentVariable("E2E_BUILD") ?? throw new IOException("E2E_BUILD is required."), "billing", "BillingJourney.dll"));
        start.ArgumentList.Add("background-worker");
        start.ArgumentList.Add(kind);
        start.ArgumentList.Add(at.ToString("O", CultureInfo.InvariantCulture));
        start.ArgumentList.Add(path);
        var process = Process.Start(start) ?? throw new IOException("The dashboard worker did not start.");
        process.OutputDataReceived += (_, output) => { if (output.Data is { } line) { Console.Error.WriteLine(line); } };
        process.ErrorDataReceived += (_, output) => { if (output.Data is { } line) { Console.Error.WriteLine(line); } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task FinishedAsync(Process process, CancellationToken cancellationToken)
    {
        await process.WaitForExitAsync(cancellationToken);
        Require(process.ExitCode == 0, "The dashboard worker failed with exit " + process.ExitCode.ToString(CultureInfo.InvariantCulture));
    }

    private static async Task ConfigureAsync(HttpClient receiver, string path, bool hold, CancellationToken cancellationToken)
    {
        using var response = await receiver.PostAsJsonAsync("/configure", new BackgroundReceiver(path, true, hold), BackgroundJson.Default.BackgroundReceiver, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task ReceivedAsync(HttpClient receiver, string path, CancellationToken cancellationToken)
    {
        await JsonAsync(receiver, "/wait?path=" + Uri.EscapeDataString(path), cancellationToken);
    }

    private static async Task<JsonElement[]> DeliveriesAsync(HttpClient receiver, string path, CancellationToken cancellationToken) =>
        [.. (await JsonAsync(receiver, "/deliveries", cancellationToken)).EnumerateArray().Where(row => row.GetProperty("path").GetString() == path)];

    private static async Task<JsonElement> JsonAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(BackgroundJson.Default.JsonElement, cancellationToken);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}

internal sealed record BackgroundOwner(string OrganizationId, string ProjectId, string DatabaseId, string DatabaseName, string SecondDatabaseName, string DatabaseRegion, string ApiKey);
internal sealed record BackgroundReceiver(string Path, bool Dashboard, bool Hold);
internal sealed record BackgroundRelease(string Path);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(BackgroundOwner))]
[JsonSerializable(typeof(BackgroundReceiver))]
[JsonSerializable(typeof(BackgroundRelease))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class BackgroundJson : JsonSerializerContext;
