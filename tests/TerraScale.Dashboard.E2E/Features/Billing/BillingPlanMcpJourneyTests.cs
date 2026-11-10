using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using TUnit.Core.Enums;

namespace TerraScale.Dashboard.E2E.Features.Billing
{
    [ClassDataSource<DashboardE2EFixture>]
    public sealed class BillingPlanMcpJourneyTests(DashboardE2EFixture fixture)
    {
        private static readonly string[] BillingIntervals = ["month", "year"];
        [ExecutionPriority(Priority.High)]
        [Test]
        public async Task NativePatUsesOriginalBillingDtosFreshOwnerScopesAndTheSameCrossReplicaReceipts()
        {
            using var owner = fixture.CreateClient();
            using var replica = fixture.CreateClient(replica: true);
            using var member = fixture.CreateClient();
            var suffix = Guid.NewGuid().ToString("N")[..12];
            var email = "billing-mcp-" + suffix + "@example.test";
            var password = "E2E-billing-mcp-" + suffix;
            await BillingPlanJourneyTests.SignUpPaidAsync(owner, email, password, suffix);
            using var login = await HttpJourney.LoginAsync(replica, email, password);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            var secret = await HttpJourney.CreatePatAsync(owner, "TUnit billing MCP " + suffix, password);
            var csrf = await HttpJourney.CsrfAsync(owner, "/organization?tab=billing");
            using var personal = JsonDocument.Parse(await owner.GetStringAsync("/api/v1/auth/me"));
            var personalId = personal.RootElement.GetProperty("organizationId").GetString()!;
            var personalProject = personal.RootElement.GetProperty("projectId").GetString()!;
            var discovery = await McpAsync(owner, secret, "server/discover", new { });
            await Assert.That(discovery.GetProperty("result").GetProperty("supportedVersions").EnumerateArray().Any(version => version.GetString() == "2026-07-28")).IsTrue();
            var tools = (await McpAsync(owner, secret, "tools/list", new { })).GetProperty("result").GetProperty("tools")
                .EnumerateArray().ToDictionary(item => item.GetProperty("name").GetString()!);
            foreach (var name in new[] { "create_checkout", "cancel_checkout", "preview_plan_change", "change_plan", "undo_plan_change" })
            {
                await Assert.That(tools.ContainsKey(name)).IsTrue();
                await Assert.That(tools[name].TryGetProperty("outputSchema", out _)).IsTrue();
            }
            await Assert.That(tools["create_checkout"].GetProperty("outputSchema").GetProperty("properties").TryGetProperty("checkoutUrl", out _)).IsTrue();
            foreach (var field in new[] { "planKey", "interval", "immediate", "effectiveAt", "amountDueCents", "prorationCents", "currency", "note" })
            {
                await Assert.That(tools["preview_plan_change"].GetProperty("outputSchema").GetProperty("properties").TryGetProperty(field, out _)).IsTrue();
            }

            foreach (var field in new[] { "organizationId", "organizationName", "organizationKind", "planKey", "planName", "status", "hasSubscription", "trialUsed", "checkoutInProgress", "canManage", "scheduledChange", "pendingChange", "paymentMethod" })
            {
                await Assert.That(tools["get_billing"].GetProperty("outputSchema").GetProperty("properties").TryGetProperty(field, out _)).IsTrue();
            }

            var listedPlans = await ToolAsync("list_plans", new { organizationId = personalId, projectId = personalProject });
            await Assert.That(listedPlans.GetProperty("currentPlanKey").GetString()).IsEqualTo("starter");
            await Assert.That(listedPlans.GetProperty("currentInterval").GetString()).IsEqualTo("annual");
            await Assert.That(listedPlans.GetProperty("plans").GetArrayLength()).IsEqualTo(3);
            var starter = listedPlans.GetProperty("plans").EnumerateArray().Single(plan => plan.GetProperty("key").GetString() == "starter");
            await Assert.That(starter.GetProperty("current").GetBoolean()).IsTrue();
            await Assert.That(starter.GetProperty("billingIntervals").EnumerateArray().Select(interval => interval.GetString()!).ToArray()).IsEquivalentTo(BillingIntervals);
            await Assert.That(starter.GetProperty("priceCents").GetInt64()).IsEqualTo(300);
            await Assert.That(starter.GetProperty("annualPriceCents").GetInt64()).IsEqualTo(3000);
            await Assert.That(starter.GetProperty("monthlyEgressBytes").GetInt64()).IsEqualTo(5_000_000_000);
            await Assert.That(starter.GetProperty("maxMembers").GetInt32()).IsEqualTo(5);
            await Assert.That(starter.GetProperty("operationUnitsPerMonth").GetInt64()).IsEqualTo(250_000);
            await Assert.That(starter.GetProperty("operationUnitBytes").GetInt32()).IsEqualTo(4096);
            await Assert.That(starter.TryGetProperty("readsPerMonth", out _)).IsFalse();
            await Assert.That(starter.TryGetProperty("writesPerMonth", out _)).IsFalse();
            var currentBilling = await ToolAsync("get_billing", new { organizationId = personalId });
            await Assert.That(currentBilling.GetProperty("hasSubscription").GetBoolean()).IsTrue();
            await Assert.That(currentBilling.GetProperty("planKey").GetString()).IsEqualTo("starter");
            await Assert.That(currentBilling.GetProperty("checkoutInProgress").GetBoolean()).IsFalse();
            await Assert.That(currentBilling.GetProperty("paymentMethod").GetProperty("validated").GetBoolean()).IsTrue();
            var portal = await ToolAsync("get_billing_portal_url", new { organizationId = personalId });
            await Assert.That(portal.GetProperty("url").GetString()!.Length).IsGreaterThan(0);
            var preview = await ToolAsync("preview_plan_change", new { organizationId = personalId, projectId = personalProject, plan = "pro", interval = "monthly" });
            await Assert.That(preview.GetProperty("immediate").GetBoolean()).IsTrue();
            await Assert.That(preview.GetProperty("amountDueCents").GetInt64()).IsEqualTo(666L);
            await Assert.That(preview.GetProperty("prorationCents").GetInt64()).IsEqualTo(166L);

            using var armed = await HttpJourney.PostFormAsync(owner, "/__test/billing/plans/scenario", new() { ["_csrf"] = csrf, ["scenario"] = "source-race" });
            await Assert.That(armed.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            var resume = await ConfirmedCallbackAsync("/billing/change", new() { ["_csrf"] = csrf, ["plan"] = "pro", ["interval"] = "monthly" });
            var held = owner.GetAsync(resume);
            var released = false;
            try
            {
                using var waiting = await owner.GetAsync("/__test/billing/plans/source-race-wait").WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.That(waiting.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
                using var competing = await HttpJourney.PostConfirmedFormAsync(owner, "/billing/change", new() { ["_csrf"] = csrf, ["plan"] = "team", ["interval"] = "monthly" });
                await Assert.That(competing.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
                await Assert.That(await competing.Content.ReadAsStringAsync()).Contains("Another billing change is in progress.");
                using var release = await HttpJourney.PostFormAsync(owner, "/__test/billing/plans/scenario", new() { ["_csrf"] = csrf, ["scenario"] = "release-source-race" });
                await Assert.That(release.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
                released = true;
                using var refused = await held;
                await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
                await Assert.That(await refused.Content.ReadAsStringAsync()).Contains("The subscription changed in Stripe.");
                using var beforeRetry = JsonDocument.Parse(await owner.GetStringAsync("/__test/billing/plans/probe"));
                await Assert.That(beforeRetry.RootElement.GetProperty("mutations").EnumerateArray().Count(item => item.GetString()!.StartsWith("/v1/subscriptions/", StringComparison.Ordinal))).IsEqualTo(0);
                var mcpRefused = await McpAsync(owner, secret, "tools/call", new
                { name = "change_plan", arguments = new { organizationId = personalId, plan = "pro", interval = "monthly" } });
                await Assert.That(mcpRefused.GetProperty("result").GetProperty("isError").GetBoolean()).IsTrue();
                await Assert.That(mcpRefused.ToString()).Contains("Confirm this in the dashboard");
                using var changed = await HttpJourney.PostConfirmedFormAsync(owner, "/billing/change", new() { ["_csrf"] = csrf, ["plan"] = "pro", ["interval"] = "monthly" });
                await Assert.That(changed.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
                await Assert.That(changed.Headers.Location!.OriginalString).Contains("billing=pending");
                using var realized = JsonDocument.Parse(await owner.GetStringAsync("/api/v1/dashboard/context"));
                await Assert.That(realized.RootElement.GetProperty("organizations").EnumerateArray()
                    .Single(org => org.GetProperty("id").GetString() == personalId).GetProperty("planId").GetString())
                    .IsEqualTo("7CNW2H87JE68ATZX224CV2G625");
                using var replay = await HttpJourney.PostConfirmedFormAsync(owner, "/billing/change", new() { ["_csrf"] = csrf, ["plan"] = "pro", ["interval"] = "monthly" });
                await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
                await Assert.That(replay.Headers.Location!.OriginalString.Contains("&billing=", StringComparison.Ordinal)).IsFalse().Because("the replayed change is already the current plan");
            }
            finally
            {
                if (!released)
                {
                    using var release = await HttpJourney.PostFormAsync(owner, "/__test/billing/plans/scenario", new() { ["_csrf"] = csrf, ["scenario"] = "release-source-race" });
                    try { using var ignored = await held; } catch (HttpRequestException) { }
                }
            }
            using var creation = await HttpJourney.PostFormAsync(owner, "/api/v1/dashboard/organization/new-organization", new()
            { ["_csrf"] = csrf, ["step"] = "organization", ["name"] = "MCP billing " + suffix });
            await Assert.That(creation.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            using var business = JsonDocument.Parse(await owner.GetStringAsync("/api/v1/auth/me"));
            var businessId = business.RootElement.GetProperty("organizationId").GetString()!;
            var downgrade = await ToolAsync("preview_plan_change", new { organizationId = personalId, plan = "starter", interval = "annual" });
            await Assert.That(downgrade.GetProperty("immediate").GetBoolean()).IsFalse();
            await Assert.That(downgrade.GetProperty("amountDueCents").GetInt64()).IsEqualTo(0L);
            var mcpUndoRefused = await McpAsync(owner, secret, "tools/call", new { name = "undo_plan_change", arguments = new { organizationId = personalId } });
            await Assert.That(mcpUndoRefused.GetProperty("result").GetProperty("isError").GetBoolean()).IsTrue();
            await Assert.That(mcpUndoRefused.ToString()).Contains("Confirm this in the dashboard");
            using var scheduled = await HttpJourney.PostConfirmedFormAsync(owner, "/billing/change?organizationId=" + personalId, new() { ["_csrf"] = csrf, ["plan"] = "starter", ["interval"] = "annual" });
            await Assert.That(scheduled.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(scheduled.Headers.Location!.OriginalString).Contains("billing=scheduled");
            var scheduledBilling = await ToolAsync("get_billing", new { organizationId = personalId });
            await Assert.That(scheduledBilling.GetProperty("scheduledChange").GetProperty("planKey").GetString()).IsEqualTo("starter");
            using var undone = await HttpJourney.PostConfirmedFormAsync(owner, "/billing/change/undo?organizationId=" + personalId, new() { ["_csrf"] = csrf });
            await Assert.That(undone.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(undone.Headers.Location!.OriginalString).Contains("billing=undone");
            using var unchanged = JsonDocument.Parse(await owner.GetStringAsync("/api/v1/auth/me"));
            await Assert.That(unchanged.RootElement.GetProperty("organizationId").GetString()).IsEqualTo(businessId);
            using var selectPersonal = await HttpJourney.PostFormAsync(owner, "/api/v1/dashboard/switch-organization", new()
            { ["_csrf"] = csrf, ["organizationId"] = personalId, ["returnUrl"] = "/organization?tab=billing" });
            await Assert.That(selectPersonal.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await ErrorAsync(owner, secret, "create_checkout", new { organizationId = businessId, plan = "free", interval = "monthly" });
            var checkout = await ToolAsync("create_checkout", new { organizationId = businessId, plan = "starter", interval = "annual" });
            var firstCheckout = checkout.GetProperty("checkoutUrl").GetString()!;
            var canceled = await ToolAsync("cancel_checkout", new { organizationId = businessId }, useReplica: true);
            await Assert.That(canceled.GetProperty("canceled").GetBoolean()).IsTrue();
            var replacement = await ToolAsync("create_checkout", new { organizationId = businessId, plan = "pro", interval = "monthly" }, useReplica: true);
            var checkoutUrl = replacement.GetProperty("checkoutUrl").GetString()!;
            await Assert.That(checkoutUrl == firstCheckout).IsFalse();
            using var beforePayment = JsonDocument.Parse(await owner.GetStringAsync("/api/v1/dashboard/context"));
            await Assert.That(beforePayment.RootElement.GetProperty("selection").GetProperty("organizationId").GetString()).IsEqualTo(personalId);
            await Assert.That(beforePayment.RootElement.GetProperty("organizations").EnumerateArray().Single(org => org.GetProperty("id").GetString() == businessId).GetProperty("billingStatus").GetString()).IsEqualTo("billing_required");
            using var completed = await owner.GetAsync(checkoutUrl);
            await Assert.That(completed.StatusCode).IsEqualTo(HttpStatusCode.Found);
            using var callback = await replica.GetAsync(completed.Headers.Location);
            await Assert.That(callback.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(BrowserAudit.WithoutWorkspace(new Uri(fixture.Origin, callback.Headers.Location!.OriginalString).ToString())).IsEqualTo(new Uri(fixture.Origin, "/organization?tab=billing&payment=success").ToString());
            using var afterPayment = JsonDocument.Parse(await owner.GetStringAsync("/api/v1/dashboard/context"));
            await Assert.That(afterPayment.RootElement.GetProperty("selection").GetProperty("organizationId").GetString()).IsEqualTo(businessId);
            var organizations = afterPayment.RootElement.GetProperty("organizations").EnumerateArray().ToArray();
            await Assert.That(organizations.Single(org => org.GetProperty("id").GetString() == businessId).GetProperty("planId").GetString()).IsEqualTo("7CNW2H87JE68ATZX224CV2G625");
            await Assert.That(organizations.Single(org => org.GetProperty("id").GetString() == personalId).GetProperty("planId").GetString()).IsEqualTo("7CNW2H87JE68ATZX224CV2G625");

            var memberEmail = "billing-mcp-member-" + suffix + "@example.test";
            await BillingPlanJourneyTests.SignUpPaidAsync(member, memberEmail, password, "member-" + suffix);
            var memberSecret = await HttpJourney.CreatePatAsync(member, "TUnit member billing MCP " + suffix, password);
            await ErrorAsync(member, memberSecret, "preview_plan_change", new { organizationId = businessId, plan = "pro", interval = "monthly" });
            using var invitation = await HttpJourney.PostFormAsync(owner, "/api/v1/access/invitations", new()
            { ["_csrf"] = csrf, ["email"] = memberEmail, ["role"] = "member" });
            await Assert.That(invitation.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var invitationBody = await invitation.Content.ReadAsStringAsync();
            var invitationLink = Regex.Match(invitationBody, "id=\"invitation-link\"[^>]+href=\"([^\"]+)\"").Groups[1].Value;
            await Assert.That(invitationLink.Length).IsGreaterThan(0);
            var invitationPath = new Uri(WebUtility.HtmlDecode(invitationLink)).AbsolutePath;
            var memberCsrf = await HttpJourney.CsrfAsync(member, invitationPath);
            using var acceptance = await HttpJourney.PostFormAsync(member, invitationPath + "/respond", new() { ["_csrf"] = memberCsrf, ["action"] = "accept" });
            await Assert.That(acceptance.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            var memberBilling = (await McpAsync(member, memberSecret, "tools/call", new { name = "get_billing", arguments = new { organizationId = businessId } })).GetProperty("result").GetProperty("structuredContent");
            await Assert.That(memberBilling.GetProperty("canManage").GetBoolean()).IsFalse();
            await Assert.That(memberBilling.TryGetProperty("paymentMethod", out _)).IsFalse();
            await ErrorAsync(member, memberSecret, "get_billing_portal_url", new { organizationId = businessId });
            foreach (var name in new[] { "preview_plan_change", "change_plan", "undo_plan_change", "cancel_checkout", "create_checkout" })
            {
                await ErrorAsync(member, memberSecret, name, new { organizationId = businessId, plan = "pro", interval = "monthly" });
            }

            var deniedCallback = await member.GetAsync(completed.Headers.Location);
            using (deniedCallback)
            {
                await Assert.That(deniedCallback.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            }

            async Task<JsonElement> ToolAsync(string name, object arguments, bool useReplica = false)
            {
                var response = await McpAsync(useReplica ? replica : owner, secret, "tools/call", new { name, arguments });
                var result = response.GetProperty("result");
                await Assert.That(result.TryGetProperty("isError", out var error) && error.GetBoolean()).IsFalse().Because(response.ToString());
                return result.GetProperty("structuredContent");
            }

            async Task<string> ConfirmedCallbackAsync(string path, Dictionary<string, string> body)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(body) };
                request.Headers.Add("Origin", owner.BaseAddress!.GetLeftPart(UriPartial.Authority));
                request.Headers.Add("Sec-Fetch-Mode", "navigate");
                using var started = await owner.SendAsync(request);
                await Assert.That(started.StatusCode).IsEqualTo(HttpStatusCode.Redirect).Because(path);
                var hosted = started.Headers.Location!;
                await Assert.That(hosted.AbsolutePath.StartsWith("/ui/terrascale/step-up/", StringComparison.Ordinal)).IsTrue();
                using var approved = await owner.GetAsync("/__test/step-up/" + hosted.Segments.Last() + "?approve=yes");
                await Assert.That(approved.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
                return approved.Headers.Location!.PathAndQuery;
            }
            static async Task ErrorAsync(HttpClient client, string secret, string name, object arguments)
            {
                var response = await McpAsync(client, secret, "tools/call", new { name, arguments });
                await Assert.That(response.GetProperty("result").GetProperty("isError").GetBoolean()).IsTrue().Because(response.ToString());
            }
        }

        private static Task<JsonElement> McpAsync(HttpClient client, string secret, string method, object arguments)
        {
            return HttpJourney.McpAsync(client, secret, method, arguments);
        }
    }
}
