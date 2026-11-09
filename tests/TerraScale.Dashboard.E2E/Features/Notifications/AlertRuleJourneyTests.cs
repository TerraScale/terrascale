using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.E2E.Features.Databases;
using TerraScale.Dashboard.Features.Notifications;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.E2E.Features.Notifications;

[ClassDataSource<DashboardE2EFixture>]
public sealed class AlertRuleJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public async Task ProjectKeepsAtMostTwentyAlertRulesAndFreesASlotOnDelete()
    {
        using var client = fixture.CreateClient();
        await DatabaseJourney.NativeWorkspaceAsync(client);
        using var me = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me"));
        var organizationId = me.RootElement.GetProperty("organizationId").GetString()!;
        var projectId = me.RootElement.GetProperty("projectId").GetString()!;
        var storage = fixture.Service<IDbContextFactory<DashboardDbContext>>();
        await using (var db = await storage.CreateDbContextAsync())
        {
            db.AlertRules.AddRange(Enumerable.Range(0, AlertRules.RuleLimit - 1).Select(slot => new AlertRuleRow
            {
                Id = AccountService.NewId(),
                OrganizationId = organizationId,
                ProjectId = projectId,
                Slot = slot,
                Name = "Seeded " + slot,
                Metric = "error_rate_percent",
                Threshold = 5,
                Email = true,
                CreatedAt = DateTime.UtcNow,
                State = "ok",
                Version = AccountService.NewId(),
            }));
            await db.SaveChangesAsync();
        }

        var csrf = await HttpJourney.CsrfAsync(client, "/notifications/alerts");
        using (var last = await CreateAsync(client, csrf, "Last rule"))
        {
            await Assert.That(last.StatusCode).IsEqualTo(HttpStatusCode.Created);
        }

        using (var over = await CreateAsync(client, csrf, "One too many"))
        {
            await Assert.That(over.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            await Assert.That(WebUtility.HtmlDecode(await over.Content.ReadAsStringAsync())).Contains("A project can have up to 20 alert rules.");
        }

        AlertRuleRow created;
        await using (var db = await storage.CreateDbContextAsync())
        {
            var rows = await db.AlertRules.AsNoTracking().Where(row => row.ProjectId == projectId).ToListAsync();
            await Assert.That(rows.Select(row => row.Slot).Order().ToArray()).IsEquivalentTo(Enumerable.Range(0, AlertRules.RuleLimit).ToArray());
            created = rows.Single(row => row.Name == "Last rule");
            await Assert.That(created.Slot).IsEqualTo(AlertRules.RuleLimit - 1);
            await Assert.That(created.WebhookUrl).IsEqualTo("https://hooks.example.com/alerts");
            await Assert.That(created.WebhookCiphertext).IsNotNull();
            db.AlertEvents.Add(new AlertEventRow
            {
                Id = AccountService.NewId(),
                RuleId = created.Id,
                AlertId = AccountService.NewId(),
                State = "firing",
                Value = 9,
                At = DateTime.UtcNow,
                EmailStatus = "pending",
                WebhookStatus = "pending",
                WebhookAttempts = 0,
            });
            await db.SaveChangesAsync();
        }

        using (var deleted = await HttpJourney.PostFormAsync(client, $"/api/v1/notifications/alerts/{created.Id}/delete", new() { ["_csrf"] = csrf }))
        {
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        }

        await using (var db = await storage.CreateDbContextAsync())
        {
            await Assert.That(await db.AlertEvents.CountAsync(row => row.RuleId == created.Id)).IsEqualTo(0).Because("A deleted rule takes its pending events with it.");
        }

        using (var again = await CreateAsync(client, csrf, "Replacement"))
        {
            await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.Created);
        }

        await using (var db = await storage.CreateDbContextAsync())
        {
            await Assert.That((await db.AlertRules.SingleAsync(row => row.ProjectId == projectId && row.Name == "Replacement")).Slot).IsEqualTo(AlertRules.RuleLimit - 1);
        }
    }

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, string csrf, string name)
    {
        return HttpJourney.PostFormAsync(client, "/api/v1/notifications/alerts", new()
        {
            ["_csrf"] = csrf,
            ["name"] = name,
            ["metric"] = "p95_latency_ms",
            ["threshold"] = "250",
            ["email"] = "on",
            ["webhook"] = "https://hooks.example.com/alerts",
        });
    }
}
