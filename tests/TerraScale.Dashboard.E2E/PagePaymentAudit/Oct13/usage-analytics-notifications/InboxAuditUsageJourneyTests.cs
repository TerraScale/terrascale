using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace TerraScale.Dashboard.E2E.PagePaymentAudit.Oct13
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class InboxAuditUsageJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task NativeNotificationPaginationClampsAfterRepeatReadsAndKeepsForeignScopeUnread()
        {
            await using var host = await PageAuditHost.StartAsync(fixture.Root);
            using var client = host.CreateClient();
            using var replica = host.CreateClient(replica: true);
            var csrf = await SeedAsync(client);
            using var replicaLogin = await HttpJourney.LoginAsync(replica);
            await Assert.That(replicaLogin.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            var page = await client.GetStringAsync("/notifications?status=unread&page=2");
            await Assert.That(Regex.Matches(page, "<article class=\"ts-notification ").Count).IsEqualTo(2);
            await Assert.That(page).Contains("name=\"page\" value=\"2\"");
            await Assert.That(page).Contains("status=unread");
            await Assert.That(page).Contains("UTC</time>");
            using (var rejected = await HttpJourney.PostFormAsync(client, "/api/v1/notifications/audit-notice-10/read", new() { ["_csrf"] = "invalid" }))
            {
                await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            }

            using (var hidden = await HttpJourney.PostFormAsync(client, "/api/v1/notifications/other-project-notice/read", new() { ["_csrf"] = csrf }))
            {
                await Assert.That(hidden.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            }

            foreach (var (id, expected) in new[]
            {
                ("audit-notice-10", "/notifications?page=2&status=unread"),
                ("audit-notice-10", "/notifications?page=2&status=unread"),
                ("audit-notice-11", "/notifications?status=unread")
            })
            {
                using var marked = await HttpJourney.PostFormAsync(client, "/api/v1/notifications/" + id + "/read", new()
                { ["_csrf"] = csrf, ["status"] = "unread", ["page"] = "2" });
                await Assert.That(marked.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
                await Assert.That(marked.Headers.Location?.OriginalString).StartsWith(expected + "&organizationId=");
            }
            var observed = await replica.GetStringAsync("/notifications?status=unread&page=2");
            await Assert.That(Regex.Matches(observed, "<article class=\"ts-notification ").Count).IsEqualTo(10);
            await Assert.That(observed.Contains("audit-notice-10/read", StringComparison.Ordinal)).IsFalse();
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var marked = await HttpJourney.PostFormAsync(client, "/api/v1/notifications/read-all", new()
                { ["_csrf"] = csrf, ["status"] = "unread", ["page"] = "2" });
                await Assert.That(marked.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
                await Assert.That(marked.Headers.Location?.OriginalString).StartsWith("/notifications?status=unread&organizationId=");
            }
            await Assert.That(await replica.GetStringAsync("/notifications?status=unread")).Contains("No unread notifications in this scope.");
            using var statuses = JsonDocument.Parse(await replica.GetStringAsync("/__test/oct13/page-audit/notifications"));
            var records = statuses.RootElement.EnumerateArray().ToArray();
            await Assert.That(records.Where(item => item.GetProperty("id").GetString()!.StartsWith("audit-notice-", StringComparison.Ordinal))
                .All(item => item.GetProperty("status").GetString() == "Read")).IsTrue();
            await Assert.That(records.Where(item => item.GetProperty("id").GetString()!.StartsWith("other-", StringComparison.Ordinal))
                .All(item => item.GetProperty("status").GetString() == "unread")).IsTrue();
        }

        [Test]
        public async Task AuditRangeOffsetBoundaryPaginationAndExportsUseTheSameFilters()
        {
            await using var host = await PageAuditHost.StartAsync(fixture.Root);
            using var client = host.CreateClient();
            await SeedAsync(client);
            using var alias = await client.GetAsync("/audit?range=24h&action=audit.fixture&search=Synthetic&severity=info&page=2");
            await Assert.That(alias.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(alias.Headers.Location?.OriginalString).IsEqualTo("/organization?tab=audit&range=24h&action=audit.fixture&search=Synthetic&severity=info&page=2");
            var html = WebUtility.HtmlDecode(await client.GetStringAsync(alias.Headers.Location));
            await Assert.That(html).Contains("Showing 2 of 12 events · page 2 of 2");
            var previous = Regex.Match(html, "rel=\"prev\" href=\"([^\"]+)\"").Groups[1].Value;
            var query = QueryHelpers.ParseQuery(new Uri(client.BaseAddress!, previous).Query);
            foreach (var (key, value) in new[] { ("range", "24h"), ("action", "audit.fixture"), ("search", "Synthetic"), ("severity", "info") })
            {
                await Assert.That(query[key].ToString()).IsEqualTo(value);
            }

            using var json = await client.GetAsync("/audit/export?format=json&range=24h&action=audit.fixture&search=Synthetic&severity=info&page=2");
            await Assert.That(json.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(json.Headers.CacheControl?.NoStore).IsTrue();
            using var export = JsonDocument.Parse(await json.Content.ReadAsStringAsync());
            await Assert.That(export.RootElement.GetProperty("count").GetInt32()).IsEqualTo(12)
                .Because("Export includes all matching events rather than just the current UI page.");
            using var boundary = JsonDocument.Parse(await client.GetStringAsync("/audit/export?format=json&range=24h&action=audit.boundary"));
            var events = boundary.RootElement.GetProperty("events");
            await Assert.That(events.GetArrayLength()).IsEqualTo(1);
            await Assert.That(events[0].GetProperty("id").GetString()).IsEqualTo("audit-boundary")
                .Because("Equivalent -03:00 timestamps are compared as instants; the lower bound is inclusive.");
            using var csv = await client.GetAsync("/audit/export?format=csv&range=24h&action=audit.fixture");
            await Assert.That(csv.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That((await csv.Content.ReadAsStringAsync()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length).IsEqualTo(13);
        }

        [Test]
        public async Task MissingAuditSeverityDoesNotBecomeInformationalInExports()
        {
            await using var host = await PageAuditHost.StartAsync(fixture.Root);
            using var client = host.CreateClient();
            await SeedAsync(client);
            const string route = "/audit/export?format=csv&range=24h&action=audit.unknown";
            using var response = await client.GetAsync(route);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var csv = await response.Content.ReadAsStringAsync();
            var directory = Path.Combine(fixture.Root, "docs/reviews/2026-10-13/usage-analytics-notifications/obj/runtime-evidence");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "unknown-severity.csv"), csv);
            using var filtered = JsonDocument.Parse(await client.GetStringAsync("/audit/export?format=json&range=24h&severity=info&action=audit.unknown"));
            var page = WebUtility.HtmlDecode(await client.GetStringAsync("/organization?tab=audit&range=24h&action=audit.unknown"));
            await File.WriteAllTextAsync(Path.Combine(directory, "unknown-severity.json"), JsonSerializer.Serialize(new
            {
                route,
                fixtureRisk = (string?)null,
                informationalFilterCount = filtered.RootElement.GetProperty("count").GetInt32(),
                pageReportsMissingSeverity = page.Contains("Severity is not reported for some provider events.", StringComparison.Ordinal),
                pageReportsOneInformationalEvent = page.Contains("<dt class=\"ts-stat-label\">Informational</dt><dd class=\"ts-stat-value\">1</dd>", StringComparison.Ordinal),
                pageReportsOneHundredPercent = page.Contains("100% · 1 of 1 filtered events", StringComparison.Ordinal)
            }, new JsonSerializerOptions { WriteIndented = true }));
            await Assert.That(csv.Contains("\"info\"", StringComparison.Ordinal)).IsFalse()
                .Because("Risk=null is missing provider data, not an informational severity observation.");
            await Assert.That(filtered.RootElement.GetProperty("count").GetInt32()).IsEqualTo(0);
        }

        [Test]
        public async Task UsageTabsPreserveTheSelectedPeriodAndDatabase()
        {
            await using var host = await PageAuditHost.StartAsync(fixture.Root);
            using var client = host.CreateClient();
            var csrf = await SeedAsync(client);
            using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
            { ["_csrf"] = csrf, ["name"] = "usage-filter", ["region"] = "GRU" });
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
            using var database = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var id = database.RootElement.GetProperty("id").GetString()!;
            var html = WebUtility.HtmlDecode(await client.GetStringAsync("/usage?range=30d&database=" + id));
            await Assert.That(html).Contains("value=\"30d\" selected=\"selected\"");
            await Assert.That(html).Contains("value=\"" + id + "\" selected=\"selected\"");
            var settings = Regex.Match(html, "<a class=\"ts-tab\" href=\"([^\"]+)\"[^>]*>(?:<svg\\b[^>]*>(?:(?!</svg>).)*</svg>)?<span>Spending</span>", RegexOptions.Singleline).Groups[1].Value;
            await Assert.That(settings.Length).IsGreaterThan(0);
            var query = QueryHelpers.ParseQuery(new Uri(client.BaseAddress!, settings).Query);
            await Assert.That(query["tab"].ToString()).IsEqualTo("settings");
            var spending = WebUtility.HtmlDecode(await client.GetStringAsync(settings));
            var overview = Regex.Match(spending, "<a class=\"ts-tab\" href=\"([^\"]+)\"[^>]*>(?:<svg\\b[^>]*>(?:(?!</svg>).)*</svg>)?<span>Overview</span>", RegexOptions.Singleline).Groups[1].Value;
            await Assert.That(overview.Length).IsGreaterThan(0);
            await Assert.That(QueryHelpers.ParseQuery(new Uri(client.BaseAddress!, overview).Query)["tab"].ToString()).IsEqualTo("overview");
            var restored = await client.GetStringAsync(overview);
            var directory = Path.Combine(fixture.Root, "docs/reviews/2026-10-13/usage-analytics-notifications/obj/runtime-evidence");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "usage-filter.json"), JsonSerializer.Serialize(new
            {
                requested = "/usage?range=30d&database=" + id,
                spendingControlsHref = settings,
                overviewHref = overview,
                query = query.ToDictionary(item => item.Key, item => item.Value.ToString()),
                restoredRange = Regex.Match(restored, "<option value=\"([^\"]*)\" selected=\"selected\">Last [^<]+</option>").Groups[1].Value,
                overviewDatabaseQuery = QueryHelpers.ParseQuery(new Uri(client.BaseAddress!, overview).Query).GetValueOrDefault("database").ToString()
            }, new JsonSerializerOptions { WriteIndented = true }));
            await Assert.That(query.GetValueOrDefault("range").ToString()).IsEqualTo("30d");
            await Assert.That(query.GetValueOrDefault("database").ToString()).IsEqualTo(id);
            await Assert.That(restored).Contains("value=\"30d\" selected=\"selected\"");
            await Assert.That(restored).Contains("value=\"" + id + "\" selected=\"selected\"");
        }

        private static async Task<string> SeedAsync(HttpClient client)
        {
            using var login = await HttpJourney.LoginAsync(client);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            var csrf = await HttpJourney.CsrfAsync(client, "/notifications");
            using var seeded = await HttpJourney.PostFormAsync(client, "/__test/oct13/page-audit/seed", new() { ["_csrf"] = csrf });
            await Assert.That(seeded.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            return csrf;
        }
    }
}
