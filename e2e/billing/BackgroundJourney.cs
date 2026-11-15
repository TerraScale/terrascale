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
using TerraScale.Dashboard.Features.Notifications;
using TerraScale.Dashboard.Platform.Http;
using TerraScale.Dashboard.Platform.Persistence;

internal static class BackgroundJourney
{
    private static readonly string EndCursor = Ulid.NewUlid(new DateTimeOffset(2090, 1, 1, 0, 0, 0, TimeSpan.Zero)).ToString();

    public static async Task WorkAsync(string kind, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var clock = new BillingClock { Now = at };
        await using var application = DashboardApplication.Build(["--environment", "Development", "--contentRoot", Environment.GetEnvironmentVariable("E2E_BUILD") + "/dashboard"],
            builder => builder.Services.AddSingleton<TimeProvider>(clock));
        switch (kind)
        {
            case "drain":
                await application.Services.GetRequiredService<LogDrains>().RunOnceAsync(cancellationToken);
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
        var owner = JsonSerializer.Deserialize(await File.ReadAllTextAsync(path, cancellationToken), BackgroundJson.Default.BackgroundOwner)
            ?? throw new IOException("The journey owner is missing.");
        var now = TimeProvider.System.GetUtcNow();
        var future = now.AddDays(1);
        await using var application = DashboardApplication.Build(["--environment", "Development", "--contentRoot", Environment.GetEnvironmentVariable("E2E_BUILD") + "/dashboard"]);
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
        var deliveryId = AccountService.NewId();
        var ruleId = AccountService.NewId();
        var alertId = AccountService.NewId();
        var name = "C121 " + run;
        var firstEntry = AccountService.NewId();
        var secondEntry = AccountService.NewId();
        var payload = LogDrains.Otlp(owner.ProjectId, new() { Id = databaseId, Name = "Claimed log batch" },
            [new(firstEntry, now, "gru", "POST", "/v1/query", 200, 1, 1, true), new(secondEntry, now, "fra", "POST", "/v1/query", 200, 2, 1, true)]);
        var secret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var state = Environment.GetEnvironmentVariable("E2E_STATE") ?? throw new IOException("E2E_STATE is required.");
        await File.WriteAllTextAsync(Path.Combine(state, "secrets.d", "background-" + run), secret, cancellationToken);
        var sealedSecret = SealedSecret.Seal(secret, identity.SessionEncryptionKey, "alert-webhook/" + owner.ProjectId + "/" + ruleId);
        await ConfigureAsync(receiver, drainPath, true, cancellationToken);
        await ConfigureAsync(receiver, alertPath, true, cancellationToken);
        try
        {
            await using (var db = await storage.CreateDbContextAsync(cancellationToken))
            {
                db.LogDrains.Add(new()
                {
                    Id = drainId,
                    ProjectId = owner.ProjectId,
                    OrganizationId = owner.OrganizationId,
                    Revision = revision,
                    Url = "https://hooks.journey.terrascale.tech" + drainPath,
                    CreatedAt = now.UtcDateTime
                });
                db.BackgroundJobs.Add(new()
                {
                    Id = jobId,
                    Owner = AccountService.NewId(),
                    Checkpoint = AccountService.NewId(),
                    LeaseUntil = future.AddSeconds(-1).UtcDateTime,
                    Payload = payload,
                    DeliveryId = deliveryId,
                    NextCursor = EndCursor
                });
                var organization = await application.Services.GetRequiredService<OrganizationStore>().FindAsync(owner.OrganizationId, cancellationToken)
                    ?? throw new IOException("The journey organization is missing.");
                foreach (var database in organization.Organization.Projects[owner.ProjectId].Databases.Values.Where(database => database.Id != databaseId))
                {
                    db.LogDrainCursors.Add(new() { Id = AccountService.NewId(), DrainId = drainId, DatabaseId = database.Id, Cursor = EndCursor });
                }
                await db.SaveChangesAsync(cancellationToken);
            }
            using var crashed = Start("drain", future);
            await ReceivedAsync(receiver, drainPath, cancellationToken);
            string oldOwner;
            await using (var db = await storage.CreateDbContextAsync(cancellationToken))
            {
                var pending = await db.BackgroundJobs.AsNoTracking().SingleAsync(row => row.Id == jobId, cancellationToken);
                oldOwner = pending.Owner;
                Require(pending.Payload is { } body && body.SequenceEqual(payload) && pending.DeliveryId == deliveryId && pending.NextCursor == EndCursor,
                    "A log batch must be persisted unchanged before the receiver accepts it.");
                Require(!await db.LogDrainCursors.AnyAsync(row => row.DrainId == drainId && row.DatabaseId == databaseId, cancellationToken), "The cursor advanced before receiver acceptance.");
            }
            using (var rival = Start("drain", future)) { await FinishedAsync(rival, cancellationToken); }
            Require((await DeliveriesAsync(receiver, drainPath, cancellationToken)).Length == 1, "A second live dashboard sent an already claimed log batch.");
            crashed.Kill(entireProcessTree: true);
            await crashed.WaitForExitAsync(cancellationToken);
            await ConfigureAsync(receiver, drainPath, false, cancellationToken);
            using (var recovered = Start("drain", future.AddMinutes(3))) { await FinishedAsync(recovered, cancellationToken); }
            var attempts = await DeliveriesAsync(receiver, drainPath, cancellationToken);
            Require(attempts.Length == 2 && attempts.All(row => row.GetProperty("id").GetString() == deliveryId && row.GetProperty("valid").GetBoolean()),
                "Crash recovery must resend the identical delivery key and bytes exactly once.");
            var effects = await JsonAsync(receiver, "/dashboard-effects?path=" + Uri.EscapeDataString(drainPath), cancellationToken);
            Require(effects.GetArrayLength() == 1, "The replay created another receiver effect.");
            var records = effects[0].GetProperty("event").GetProperty("resourceLogs")[0].GetProperty("scopeLogs")[0].GetProperty("logRecords");
            Require(records.GetArrayLength() == 2 && records.EnumerateArray().Select(row => row.GetProperty("attributes")[0].GetProperty("value").GetProperty("stringValue").GetString()).ToHashSet(StringComparer.Ordinal).SetEquals([firstEntry, secondEntry]),
                "Each event in the claimed log range must produce one receiver effect.");
            await using (var db = await storage.CreateDbContextAsync(cancellationToken))
            {
                var saved = await db.LogDrainCursors.AsNoTracking().SingleAsync(row => row.DrainId == drainId && row.DatabaseId == databaseId, cancellationToken);
                var completed = await db.BackgroundJobs.AsNoTracking().SingleAsync(row => row.Id == jobId, cancellationToken);
                Require(saved.Cursor == EndCursor && completed.Payload is null && completed.DeliveryId is null, "The cursor and completion must commit together.");
                Require(await db.BackgroundJobs.Where(row => row.Id == jobId && row.Owner == oldOwner)
                    .ExecuteUpdateAsync(update => update.SetProperty(row => row.Cursor, "stale-owner"), cancellationToken) == 0, "An expired owner committed over its successor.");
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
                var alertJob = await db.BackgroundJobs.SingleOrDefaultAsync(row => row.Id == "alerts/" + owner.ProjectId, cancellationToken);
                if (alertJob is null) { db.BackgroundJobs.Add(new() { Id = "alerts/" + owner.ProjectId, Owner = AccountService.NewId(), Checkpoint = AccountService.NewId(), LeaseUntil = future.AddSeconds(-1).UtcDateTime }); }
                else { alertJob.LeaseUntil = future.AddSeconds(-1).UtcDateTime; alertJob.Owner = AccountService.NewId(); alertJob.Checkpoint = AccountService.NewId(); }
                await db.SaveChangesAsync(cancellationToken);
            }
            using var sending = Start("alert", future);
            await ReceivedAsync(receiver, alertPath, cancellationToken);
            using (var rival = Start("alert", future)) { await FinishedAsync(rival, cancellationToken); }
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
            Console.WriteLine("background journey: two dashboard processes, one log batch effect, crash after receiver acceptance, same delivery key and bytes, fenced cursor commit");
            Console.WriteLine("background journey: two dashboard processes, one alert webhook and one owner email, pending event removed after delivery");
        }
        finally
        {
            await using var db = await storage.CreateDbContextAsync(CancellationToken.None);
            await db.AlertRules.Where(row => row.Id == ruleId).ExecuteDeleteAsync(CancellationToken.None);
            await db.LogDrains.Where(row => row.Id == drainId).ExecuteDeleteAsync(CancellationToken.None);
            await db.BackgroundJobs.Where(row => row.Id == jobId || row.Id == "alerts/" + owner.ProjectId).ExecuteDeleteAsync(CancellationToken.None);
        }
    }

    private static Process Start(string kind, DateTimeOffset at)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(Environment.GetEnvironmentVariable("E2E_BUILD") ?? throw new IOException("E2E_BUILD is required."), "billing", "BillingJourney.dll"));
        start.ArgumentList.Add("background-worker");
        start.ArgumentList.Add(kind);
        start.ArgumentList.Add(at.ToString("O", CultureInfo.InvariantCulture));
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

internal sealed record BackgroundOwner(string OrganizationId, string ProjectId, string DatabaseId);
internal sealed record BackgroundReceiver(string Path, bool Dashboard, bool Hold);
internal sealed record BackgroundRelease(string Path);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(BackgroundOwner))]
[JsonSerializable(typeof(BackgroundReceiver))]
[JsonSerializable(typeof(BackgroundRelease))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class BackgroundJson : JsonSerializerContext;
