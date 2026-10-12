using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TUnit.Core;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Organizations;

[ClassDataSource<DashboardE2EFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel("dashboard-demo")]
public sealed class InvitationSdkJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public async Task NativeSdkExpiredInvitationsReplaceAndCancelAfterTheProviderRemovesTheirResources()
    {
        var protocol = new InvitationSdkProtocol();
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, protocol.Configure, protocol.DispatchAsync);
        using var owner = scenario.CreateClient();
        using var replica = scenario.CreateClient(replica: true);
        using var invited = scenario.CreateClient();
        using var producer = scenario.CreateProviderClient();
        var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(owner);
        var other = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(invited);
        using var login = await HttpJourney.LoginAsync(replica, account.Email, account.Password);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var ownerPat = await HttpJourney.CreatePatAsync(owner, "Invitation SDK owner", account.Password);
        var invitedPat = await HttpJourney.CreatePatAsync(invited, "Invitation SDK invitee", other.Password);
        var context = await ToolAsync(owner, ownerPat, "get_context", new { });
        var orgId = context.GetProperty("selection").GetProperty("organizationId").GetString()!;
        var ownerUserId = context.GetProperty("user").GetProperty("id").GetString()!;
        var suffix = Guid.NewGuid().ToString("N")[..12];
        foreach (var command in new[] { "expire", "expire-and-remove" })
        {
            var issued = await ToolAsync(owner, ownerPat, "invite_member", new { organizationId = orgId, email = command + "-" + suffix + "@example.test" });
            var id = issued.GetProperty("invitation").GetProperty("id").GetString()!;
            var path = issued.GetProperty("inviteUrl").GetString()!;
            await ExpireAsync(id, command);
            using (var unavailable = await replica.GetAsync(path)) await Assert.That(unavailable.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            var html = await replica.GetStringAsync("/organization?tab=invitations");
            await Assert.That(html.Contains("/api/v1/access/invitations/" + id + "/resend?tab=invitations", StringComparison.Ordinal)).IsTrue();
            await Assert.That(html.Contains("/api/v1/access/invitations/" + id + "/revoke?tab=invitations", StringComparison.Ordinal)).IsTrue();
            var replaced = await ToolAsync(replica, ownerPat, "resend_invitation", new { organizationId = orgId, invitationId = id });
            await Assert.That(replaced.GetProperty("invitation").GetProperty("id").GetString() == id).IsFalse();
            await ToolAsync(owner, ownerPat, "get_invitation", new { token = replaced.GetProperty("inviteUrl").GetString() });
            await ErrorAsync(owner, ownerPat, "get_invitation", new { token = path });
        }
        var afterReplacements = await ProbeAsync();
        await Assert.That(afterReplacements.GetProperty("creates").GetInt32()).IsEqualTo(4);
        await Assert.That(afterReplacements.GetProperty("deletes").GetInt32()).IsEqualTo(1);
        await Assert.That(afterReplacements.GetProperty("missingDeletes").GetInt32()).IsEqualTo(1);
        foreach (var command in new[] { "expire", "expire-and-remove" })
        {
            var issued = await ToolAsync(owner, ownerPat, "invite_member", new { organizationId = orgId, email = "cancel-" + command + "-" + suffix + "@example.test" });
            var id = issued.GetProperty("invitation").GetProperty("id").GetString()!;
            await ExpireAsync(id, command);
            var csrf = await HttpJourney.CsrfAsync(replica, "/organization?tab=invitations");
            using (var canceled = await HttpJourney.PostFormAsync(replica, "/api/v1/access/invitations/" + id + "/revoke?tab=invitations", new() { ["_csrf"] = csrf }))
                await Assert.That(canceled.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            var beforeReplay = await producer.GetStringAsync("/__test/invitation-sdk/probe");
            using (var repeated = await HttpJourney.PostFormAsync(replica, "/api/v1/access/invitations/" + id + "/revoke?tab=invitations", new() { ["_csrf"] = csrf }))
                await Assert.That(repeated.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(await producer.GetStringAsync("/__test/invitation-sdk/probe")).IsEqualTo(beforeReplay);
        }
        var live = await ToolAsync(owner, ownerPat, "invite_member", new { organizationId = orgId, email = "live-missing-" + suffix + "@example.test" });
        var liveId = live.GetProperty("invitation").GetProperty("id").GetString()!;
        await ExpireAsync(liveId, "remove");
        var liveCsrf = await HttpJourney.CsrfAsync(owner, "/organization?tab=invitations");
        using (var disappeared = await HttpJourney.PostFormAsync(owner, "/api/v1/access/invitations/" + liveId + "/revoke?tab=invitations", new() { ["_csrf"] = liveCsrf }))
            await Assert.That(disappeared.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        var liveList = await ToolAsync(owner, ownerPat, "list_invitations", new { organizationId = orgId });
        await Assert.That(liveList.GetProperty("invitations").EnumerateArray().Single(item => item.GetProperty("id").GetString() == liveId).GetProperty("status").GetString()).IsEqualTo("Pending");
        var declined = await ToolAsync(owner, ownerPat, "invite_member", new { organizationId = orgId, email = other.Email });
        var declinedId = declined.GetProperty("invitation").GetProperty("id").GetString()!;
        var beforeForeign = await producer.GetStringAsync("/__test/invitation-sdk/probe");
        var foreignCsrf = await HttpJourney.CsrfAsync(invited, "/organization?tab=invitations");
        using (var foreign = await HttpJourney.PostFormAsync(invited, "/api/v1/access/invitations/" + declinedId + "/revoke?tab=invitations", new() { ["_csrf"] = foreignCsrf }))
            await Assert.That(foreign.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await producer.GetStringAsync("/__test/invitation-sdk/probe")).IsEqualTo(beforeForeign);
        await ToolAsync(invited, invitedPat, "respond_to_invitation", new { token = declined.GetProperty("inviteUrl").GetString(), action = "decline" });
        var beforeDeclinedCancel = await producer.GetStringAsync("/__test/invitation-sdk/probe");
        var ownerCsrf = await HttpJourney.CsrfAsync(owner, "/organization?tab=invitations");
        using (var nonpending = await HttpJourney.PostFormAsync(owner, "/api/v1/access/invitations/" + declinedId + "/revoke?tab=invitations", new() { ["_csrf"] = ownerCsrf }))
            await Assert.That(nonpending.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await producer.GetStringAsync("/__test/invitation-sdk/probe")).IsEqualTo(beforeDeclinedCancel);
        await Assert.That((await ProbeAsync()).GetProperty("failures").GetArrayLength()).IsEqualTo(0);
        using var observation = await producer.GetAsync("/__test/observations");
        observation.EnsureSuccessStatusCode();
        using var observed = JsonDocument.Parse(await observation.Content.ReadAsStringAsync());
        await Assert.That(observed.RootElement.GetProperty("failures").GetArrayLength()).IsEqualTo(0);

        async Task ExpireAsync(string invitationId, string command)
        {
            using var response = await producer.PostAsJsonAsync("/__test/invitation-sdk/expire", new { organizationId = orgId, invitationId, ownerUserId, command });
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        }
        async Task<JsonElement> ProbeAsync()
        {
            using var response = await producer.GetAsync("/__test/invitation-sdk/probe");
            response.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return body.RootElement.Clone();
        }
    }

    private static async Task<JsonElement> ToolAsync(HttpClient client, string secret, string name, object arguments)
    {
        var response = await HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments });
        await Assert.That(response.GetProperty("result").TryGetProperty("isError", out var error) && error.GetBoolean()).IsFalse().Because(response.GetRawText());
        return response.GetProperty("result").GetProperty("structuredContent");
    }
    private static async Task ErrorAsync(HttpClient client, string secret, string name, object arguments)
    {
        var response = await HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments });
        await Assert.That(response.GetProperty("result").GetProperty("isError").GetBoolean()).IsTrue();
    }
}
