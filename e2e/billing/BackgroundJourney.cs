using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Dashboard.Common.Accounts;
using TerraScale.Dashboard.Features.Logs;
using TerraScale.Dashboard.Features.Notifications;
using TerraScale.Dashboard.Platform.Http;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

internal static partial class BackgroundJourney
{
    public static async Task WorkAsync(string kind, DateTimeOffset at, string path, string id, CancellationToken cancellationToken)
    {
        var owner = await OwnerAsync(path, cancellationToken);
        using var application = BackgroundHost.Build(owner, clock: BackgroundClock.At(at));
        switch (kind)
        {
            case "drain":
                await application.Services.GetRequiredService<LogDrains>().RunOnceAsync(cancellationToken);
                break;
            case "alert":
                await application.Services.GetRequiredService<AlertRules>().RunOnceAsync(cancellationToken);
                break;
            case "claim":
            case "stale":
            case "skew":
                var fenced = false;
                try
                {
                    await application.Services.GetRequiredService<BackgroundJobs>().RunAsync(id, kind == "skew" ? TimeSpan.FromHours(1) : TimeSpan.Zero, async (claim, token) =>
                    {
                        claim.State.Cursor = "first";
                        if (kind == "skew")
                        {
                            claim.State.LeaseUntil = at.AddMinutes(2).UtcDateTime;
                            claim.State.AvailableAt = at.AddHours(1).UtcDateTime;
                        }
                        await claim.SaveAsync(token);
                        Console.WriteLine("claimed");
                        Require(await Console.In.ReadLineAsync(token) == "resume", "The worker did not receive its resume condition.");
                        if (kind == "stale")
                        {
                            claim.State.Cursor = "stale";
                            try { await claim.SaveAsync(token); }
                            catch (DBConcurrencyException) { fenced = true; throw; }
                        }
                    }, cancellationToken);
                }
                catch (DBConcurrencyException) when (kind == "stale" && fenced) { Console.WriteLine("stale save fenced"); }
                if (kind == "stale") { Require(fenced, "A real stale Claim.SaveAsync must fail its owner/checkpoint fence."); }
                break;
            default:
                throw new InvalidOperationException("Unknown background worker.");
        }
    }

    public static async Task CaseAsync(string kind, string path, CancellationToken cancellationToken)
    {
        var owner = await OwnerAsync(path, cancellationToken);
        var commands = new BackgroundCommands();
        var clock = new BackgroundClock(TimeSpan.Zero);
        using var application = BackgroundHost.Build(owner, commands, clock);
        var services = application.Services;
        if (kind == "source")
        {
            using var client = new HttpClient(TerraBaseHttp.CreateHandler());
            var sql = new TerraBaseSqlClient(client, new() { ApiKey = owner.ApiKey, Region = owner.DatabaseRegion.ToLowerInvariant() }).Database(owner.ProjectId, owner.DatabaseName);
            await sql.ExecuteAsync("SELECT 121", cancellationToken: cancellationToken);
            await sql.ExecuteAsync("SELECT 122", cancellationToken: cancellationToken);
            Console.WriteLine("background case: source");
            return;
        }
        if (kind == "source-ready")
        {
            var batch = await services.GetRequiredService<ILogDrainSource>().ReadAsync(owner.ProjectId, owner.DatabaseId, owner.DatabaseId, cancellationToken);
            Console.WriteLine(batch.Items.Count >= 2 ? "ready" : "pending");
            return;
        }
        if (kind == "quiet-ready")
        {
            Console.WriteLine(await QuietReadyAsync(services, path, cancellationToken) ? "ready" : "pending");
            return;
        }
        await services.GetRequiredService<DashboardDbUpRunner>().UpgradeAsync(cancellationToken);
        var organizations = services.GetRequiredService<OrganizationStore>();
        if (await organizations.FindAsync(owner.OrganizationId, cancellationToken) is null)
        {
            using var original = BackgroundHost.Build();
            var organization = (await original.Services.GetRequiredService<OrganizationStore>().FindAsync(owner.OrganizationId, cancellationToken))?.Organization
                ?? throw new IOException("The journey organization is missing.");
            organization.Projects[owner.ProjectId].Databases = organization.Projects[owner.ProjectId].Databases.Where(pair => pair.Key == owner.DatabaseId).ToDictionary();
            await organizations.CreateAsync(organization, cancellationToken);
        }
        switch (kind)
        {
            case "crash": await CrashAsync(services, owner, path, cancellationToken); break;
            case "deleted": await DeletedAsync(services, commands, owner, cancellationToken); break;
            case "alert": await AlertAsync(services, owner, path, cancellationToken); break;
            case "contention":
            case "stale":
            case "skew": await ClaimAsync(services, owner, kind, path, cancellationToken); break;
            case "renewal": await RenewalAsync(services, commands, clock, cancellationToken); break;
            case "plans": await PlansAsync(services, commands, owner, cancellationToken); break;
            case "idle": await IdleAsync(services, commands, owner, cancellationToken); break;
            case "quiet": await QuietAsync(services, commands, owner, path, cancellationToken); break;
            case "quiet-next": await QuietNextAsync(services, owner, path, cancellationToken); break;
            case "quiet-cleanup": await QuietCleanupAsync(services, owner, path, cancellationToken); break;
            case "gate": await GateAsync(services, commands, cancellationToken); break;
            default: throw new InvalidOperationException("Unknown background case.");
        }
        Console.WriteLine("background case: " + kind);
    }

    private static async Task CrashAsync(IServiceProvider services, BackgroundOwner owner, string path, CancellationToken cancellationToken)
    {
        var storage = services.GetRequiredService<DashboardDb>();
        using var receiver = Receiver();
        var drain = await DrainAsync(storage, owner, cancellationToken);
        var drainPath = new Uri(drain.Url).AbsolutePath;
        var jobId = "log-drain/" + owner.ProjectId + "/" + drain.Revision + "/" + owner.DatabaseId;
        await ConfigureAsync(receiver, drainPath, true, cancellationToken);
        using var crashed = Start("drain", TimeProvider.System.GetUtcNow().AddDays(1), path);
        try
        {
            await ReceivedAsync(receiver, drainPath, crashed, cancellationToken);
            var (pending, databaseNow) = await JourneySql.LiveJobAsync(storage, jobId, cancellationToken);
            if (pending is not { Payload: { } payload, DeliveryId: { } deliveryId, NextCursor: { } nextCursor })
            {
                throw new InvalidOperationException("The worker must persist source payload, delivery identity and next cursor before sending.");
            }
            Require(Records(payload).GetArrayLength() >= 2, "The worker must read real SQL activity from the production log source.");
            Require(pending.LeaseUntil > databaseNow && pending.LeaseUntil <= databaseNow.AddMinutes(2), "A forward-skewed worker extended its lease beyond database time.");
            Require(!await CursorExistsAsync(storage, drain.Id, owner.DatabaseId, cancellationToken), "The cursor advanced before receiver acceptance.");
            await services.GetRequiredService<LogDrains>().RunOnceAsync(cancellationToken);
            Require((await DeliveriesAsync(receiver, drainPath, cancellationToken)).Length == 1, "A second live dashboard sent an already claimed log batch.");
            crashed.Kill(entireProcessTree: true);
            await crashed.WaitForExitAsync(cancellationToken);
            await ExpireAsync(storage, jobId, cancellationToken);
            await ConfigureAsync(receiver, drainPath, false, cancellationToken);
            using var recovered = BackgroundHost.Build(owner, clock: BackgroundClock.At(TimeProvider.System.GetUtcNow().AddDays(-1)));
            await recovered.Services.GetRequiredService<LogDrains>().RunOnceAsync(cancellationToken);
            var attempts = await DeliveriesAsync(receiver, drainPath, cancellationToken);
            Require(attempts.Length == 2 && attempts.All(row => row.GetProperty("id").GetString() == deliveryId && row.GetProperty("valid").GetBoolean()), "Crash recovery must resend the identical delivery key and bytes exactly once.");
            var effects = await JsonAsync(receiver, "/dashboard-effects?path=" + Uri.EscapeDataString(drainPath), cancellationToken);
            Require(effects.GetArrayLength() == 1, "The replay created another receiver effect.");
            Require(effects[0].GetProperty("event").GetProperty("resourceLogs")[0].GetProperty("scopeLogs")[0].GetProperty("logRecords").GetRawText() == Records(payload).GetRawText(), "The recovered worker changed the claimed source events.");
            var cursor = await CursorAsync(storage, drain.Id, owner.DatabaseId, cancellationToken) ?? throw new InvalidOperationException("The recovered delivery committed no cursor.");
            var completed = await JourneySql.JobAsync(storage, jobId, cancellationToken) ?? throw new InvalidOperationException("The recovered delivery lost its job.");
            Require(cursor.Cursor == nextCursor && completed.Payload is null && completed.DeliveryId is null, "The cursor and completion must commit together.");
        }
        finally
        {
            await StopAsync(crashed);
            await DeleteDrainAsync(storage, drain, CancellationToken.None);
        }
    }

    private static async Task AlertAsync(IServiceProvider services, BackgroundOwner owner, string path, CancellationToken cancellationToken)
    {
        var storage = services.GetRequiredService<DashboardDb>();
        var identity = await services.GetRequiredService<IDashboardIdentity>().ReadAsync(cancellationToken);
        var now = TimeProvider.System.GetUtcNow();
        using var receiver = Receiver();
        var run = AccountService.NewId();
        var alertPath = "/dashboard-alert/" + run;
        var ruleId = AccountService.NewId();
        var alertId = AccountService.NewId();
        var name = "C121 " + run;
        var secret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var state = Environment.GetEnvironmentVariable("E2E_STATE") ?? throw new IOException("E2E_STATE is required.");
        await File.WriteAllTextAsync(Path.Combine(state, "secrets.d", "background-" + run), secret, cancellationToken);
        var sealedSecret = SealedSecret.Seal(secret, identity.SessionEncryptionKey, "alert-webhook/" + owner.ProjectId + "/" + ruleId);
        await ConfigureAsync(receiver, alertPath, true, cancellationToken);
        await storage.WriteAsync(async (connection, transaction) =>
        {
            await connection.ExecuteAsync(JourneySql.InsertAlertRule, new AlertRuleRow
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
            }, transaction);
            await connection.ExecuteAsync(JourneySql.InsertAlertEvent, new AlertEventRow
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
            }, transaction);
        }, cancellationToken);
        using var sending = Start("alert", now, path);
        try
        {
            await ReceivedAsync(receiver, alertPath, sending, cancellationToken);
            await services.GetRequiredService<AlertRules>().RunOnceAsync(cancellationToken);
            Require((await DeliveriesAsync(receiver, alertPath, cancellationToken)).Length == 1, "Two dashboard instances sent one pending alert.");
            using (var released = await receiver.PostAsJsonAsync("/release", new BackgroundRelease(alertPath), BackgroundJson.Default.BackgroundRelease, cancellationToken)) { released.EnsureSuccessStatusCode(); }
            await FinishedAsync(sending, cancellationToken);
            using var mail = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:8025") };
            var messages = await JsonAsync(mail, "/api/v1/messages?limit=1000", cancellationToken);
            Require(messages.GetProperty("messages").EnumerateArray().Count(row => row.GetProperty("Subject").GetString()?.Contains(name, StringComparison.Ordinal) == true) == 1,
                "One alert transition must send one owner notification.");
            Require(!(await storage.ReadAsync(connection => connection.ExecuteScalarAsync<long>("SELECT count(*) FROM alert_events WHERE rule_id = @RuleId", new { RuleId = ruleId }), cancellationToken) > 0), "A delivered alert must leave the pending queue.");
        }
        finally { await StopAsync(sending); }
        await storage.ReadAsync(connection => connection.ExecuteAsync("DELETE FROM alert_events WHERE rule_id = @RuleId; DELETE FROM alert_rules WHERE id = @RuleId; DELETE FROM background_jobs WHERE id = @JobId", new { RuleId = ruleId, JobId = "alerts/" + owner.ProjectId }), CancellationToken.None);
    }

    private static async Task<BackgroundOwner> OwnerAsync(string path, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize(await File.ReadAllTextAsync(path, cancellationToken), BackgroundJson.Default.BackgroundOwner)
            ?? throw new IOException("The journey owner is missing.");

    private static HttpClient Receiver() => new() { BaseAddress = new Uri("http://127.0.0.1:8029") };
    private static JsonElement Records(byte[] payload)
    {
        using var json = JsonDocument.Parse(payload);
        return json.RootElement.GetProperty("resourceLogs")[0].GetProperty("scopeLogs")[0].GetProperty("logRecords").Clone();
    }

    private static async Task<LogDrainRow> DrainAsync(DashboardDb storage, BackgroundOwner owner, CancellationToken cancellationToken)
    {
        var drain = new LogDrainRow
        {
            Id = AccountService.NewId(),
            ProjectId = owner.ProjectId,
            OrganizationId = owner.OrganizationId,
            Revision = AccountService.NewId(),
            Url = "https://hooks.journey.terrascale.tech/dashboard-drain/" + AccountService.NewId(),
            CreatedAt = Ulid.Parse(owner.DatabaseId).Time.UtcDateTime
        };
        await storage.ReadAsync(connection => connection.ExecuteAsync(JourneySql.InsertDrain, drain), cancellationToken);
        return drain;
    }

    private static async Task<LogDrainRow> StoredDrainAsync(DashboardDb storage, string id, CancellationToken cancellationToken)
    {
        await using var connection = await storage.OpenAsync(cancellationToken);
        return await connection.QuerySingleAsync<LogDrainRow>(JourneySql.Drains + " WHERE id = @Id", new { Id = id });
    }

    private static async Task<LogDrainCursorRow?> CursorAsync(DashboardDb storage, string drainId, string databaseId, CancellationToken cancellationToken)
    {
        await using var connection = await storage.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<LogDrainCursorRow>(JourneySql.Cursors + " WHERE drain_id = @DrainId AND database_id = @DatabaseId", new { DrainId = drainId, DatabaseId = databaseId });
    }

    private static async Task<bool> CursorExistsAsync(DashboardDb storage, string drainId, string databaseId, CancellationToken cancellationToken) =>
        await storage.ReadAsync(connection => connection.ExecuteScalarAsync<long>("SELECT count(*) FROM log_drain_cursors WHERE drain_id = @DrainId AND database_id = @DatabaseId",
            new { DrainId = drainId, DatabaseId = databaseId }), cancellationToken) > 0;

    private static async Task DeleteDrainAsync(DashboardDb storage, LogDrainRow drain, CancellationToken cancellationToken) =>
        await storage.ReadAsync(connection => connection.ExecuteAsync("DELETE FROM log_drain_cursors WHERE drain_id = @Id; DELETE FROM log_drains WHERE id = @Id; DELETE FROM background_jobs WHERE id LIKE @Prefix || '%'", new { drain.Id, Prefix = "log-drain/" + drain.ProjectId + "/" + drain.Revision + "/" }), cancellationToken);

    private static Process Start(string kind, DateTimeOffset at, string path, string id = "")
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(Environment.GetEnvironmentVariable("E2E_BUILD") ?? throw new IOException("E2E_BUILD is required."), "billing", "BillingJourney.dll"));
        foreach (var argument in new[] { "background-worker", kind, at.ToString("O", CultureInfo.InvariantCulture), path, id }) { start.ArgumentList.Add(argument); }
        var process = Process.Start(start) ?? throw new IOException("The dashboard worker did not start.");
        process.ErrorDataReceived += (_, output) => { if (output.Data is { } line) { Console.Error.WriteLine(line); } };
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task StopAsync(Process process)
    {
        if (!process.HasExited) { process.Kill(entireProcessTree: true); }
        await process.WaitForExitAsync(CancellationToken.None);
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

    private static async Task ReceivedAsync(HttpClient receiver, string path, Process process, CancellationToken cancellationToken)
    {
        var received = JsonAsync(receiver, "/wait?path=" + Uri.EscapeDataString(path), cancellationToken);
        if (await Task.WhenAny(received, process.WaitForExitAsync(cancellationToken)) != received)
        {
            await FinishedAsync(process, cancellationToken);
            throw new InvalidOperationException("The dashboard worker exited without sending its delivery.");
        }
        await received;
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
[JsonSerializable(typeof(BackgroundQuiet))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class BackgroundJson : JsonSerializerContext;
