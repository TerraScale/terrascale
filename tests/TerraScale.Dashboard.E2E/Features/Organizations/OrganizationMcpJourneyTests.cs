using System.Net;
using System.Text.Json;
using TerraScale.Dashboard.E2E.Features.Billing;
using TUnit.Core;

namespace TerraScale.Dashboard.E2E.Features.Organizations;

[ClassDataSource<DashboardE2EFixture>]
public sealed class OrganizationMcpJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public async Task NativePatProjectScopesAndQueuedDeletionPreserveResourcesAndDenyNewProvisioning()
    {
        using var owner = fixture.CreateClient();
        using var replica = fixture.CreateClient(replica: true);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var email = "org-project-mcp-" + suffix + "@example.test";
        var password = "E2E-org-project-" + suffix;
        await BillingPlanJourneyTests.SignUpPaidAsync(owner, email, password, suffix);
        using var login = await HttpJourney.LoginAsync(replica, email, password);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var secret = await HttpJourney.CreatePatAsync(owner, "Organization project E2E " + suffix, password);
        var original = await ContextAsync(owner, secret);
        var personalId = original.GetProperty("selection").GetProperty("organizationId").GetString()!;
        var personalProject = original.GetProperty("selection").GetProperty("projectId").GetString()!;
        var name = "Native queued company " + suffix;
        var businessId = await CreateBusinessAsync(owner, name);
        var inventory = (await HttpJourney.McpAsync(owner, secret, "tools/list", new { })).GetProperty("result").GetProperty("tools")
            .EnumerateArray().ToDictionary(item => item.GetProperty("name").GetString()!);
        foreach (var tool in new[] { "create_project", "update_project", "rename_organization", "request_organization_deletion", "leave_organization",
            "list_members", "list_invitations", "invite_member", "resend_invitation", "revoke_invitation", "get_invitation", "respond_to_invitation",
            "update_member_role", "remove_member", "request_ownership_transfer", "confirm_ownership_transfer", "list_audit_events" })
        {
            await Assert.That(inventory.ContainsKey(tool)).IsTrue().Because(tool);
            await Assert.That(inventory[tool].TryGetProperty("outputSchema", out _)).IsTrue().Because(tool);
        }
        foreach (var field in new[] { "id", "organizationId", "name", "region", "backupRegion", "regions" })
            await Assert.That(inventory["create_project"].GetProperty("outputSchema").GetProperty("properties").TryGetProperty(field, out _)).IsTrue();
        await ErrorAsync(owner, secret, "create_project", new { organizationId = businessId, name = "Unpaid denied", regions = new[] { "GRU" } });
        var project = await ToolAsync(replica, secret, "create_project", new
        { organizationId = personalId, name = "Explicit " + suffix, region = "SFO", backupRegion = "GRU", regions = new[] { "SFO", "GRU", "SFO" } });
        var projectId = project.GetProperty("id").GetString()!;
        await Assert.That(project.GetProperty("organizationId").GetString()).IsEqualTo(personalId);
        await Assert.That(project.GetProperty("region").GetString()).IsEqualTo("SFO");
        await Assert.That(project.GetProperty("backupRegion").GetString()).IsEqualTo("GRU");
        await Assert.That(project.GetProperty("regions").GetArrayLength()).IsEqualTo(2);
        var afterCreate = await ContextAsync(owner, secret);
        await Assert.That(afterCreate.GetProperty("selection").GetProperty("organizationId").GetString()).IsEqualTo(businessId);
        await ErrorAsync(owner, secret, "create_project", new { organizationId = personalId, name = "Explicit " + suffix, regions = new[] { "GRU" } });
        await ErrorAsync(owner, secret, "create_project", new { organizationId = personalId, name = "Unknown region", regions = new[] { "ATLANTIS" } });
        await ErrorAsync(owner, secret, "update_project", new { organizationId = businessId, projectId, name = "Wrong organization" });
        await ToolAsync(owner, secret, "update_project", new { organizationId = personalId, projectId, name = "Renamed " + suffix });
        var renamed = Organization(await ContextAsync(replica, secret), personalId).GetProperty("projects").EnumerateArray().Single(item => item.GetProperty("id").GetString() == projectId);
        await Assert.That(renamed.GetProperty("name").GetString()).IsEqualTo("Renamed " + suffix);
        await Assert.That(renamed.GetProperty("regions").GetArrayLength()).IsEqualTo(2);
        await Assert.That(renamed.GetProperty("region").GetString()).IsEqualTo("SFO");
        var checkout = await ToolAsync(owner, secret, "create_checkout", new { organizationId = businessId, plan = "starter", interval = "monthly" });
        using var vendor = await owner.GetAsync(checkout.GetProperty("checkoutUrl").GetString());
        await Assert.That(vendor.StatusCode).IsEqualTo(HttpStatusCode.Found);
        using var returned = await replica.GetAsync(vendor.Headers.Location);
        await Assert.That(returned.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var businessProject = await ToolAsync(owner, secret, "create_project", new { organizationId = businessId, name = "Preserved data " + suffix, regions = new[] { "GRU" } });
        await ToolAsync(owner, secret, "rename_organization", new { organizationId = businessId, name = name + " renamed" });
        await ErrorAsync(owner, secret, "request_organization_deletion", new { organizationId = businessId, confirmName = name });
        var csrf = await HttpJourney.CsrfAsync(owner, "/organization");
        using var native = await HttpJourney.PostFormAsync(owner, "/api/v1/dashboard/organization/deletion", new()
        { ["_csrf"] = csrf, ["confirmation"] = name + " renamed" });
        await Assert.That(native.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var requested = Organization(await ContextAsync(replica, secret), businessId);
        await Assert.That(requested.GetProperty("deletionRequested").GetBoolean()).IsTrue();
        await Assert.That(requested.GetProperty("status").GetString()).IsEqualTo("active");
        await Assert.That(requested.GetProperty("canProvision").GetBoolean()).IsFalse();
        await Assert.That(requested.GetProperty("projects")[0].GetProperty("id").GetString()).IsEqualTo(businessProject.GetProperty("id").GetString());
        await ToolAsync(replica, secret, "request_organization_deletion", new { organizationId = businessId, confirmName = name + " renamed" });
        await ErrorAsync(owner, secret, "create_project", new { organizationId = businessId, name = "Deletion denied", regions = new[] { "GRU" } });
        foreach (var billingPath in new[] { "/organization?tab=billing", "/organization?tab=billing&review=1&plan=pro&interval=monthly" })
        {
            using var billingRead = await owner.GetAsync(billingPath);
            await Assert.That(billingRead.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var billingBody = await billingRead.Content.ReadAsStringAsync();
            await Assert.That(billingBody.Contains("id=\"billing-summary\"", StringComparison.Ordinal)).IsTrue();
            foreach (var mutationControl in new[] { "commandfor=\"change-billing-plan\"", "href=\"/billing/portal\"", "action=\"/billing/checkout\"", "action=\"/billing/change\"", "id=\"review-billing-plan\"" })
                await Assert.That(billingBody.Contains(mutationControl, StringComparison.Ordinal)).IsFalse().Because(mutationControl);
        }
        var pendingBillingRead = await ToolAsync(owner, secret, "get_billing", new { organizationId = businessId });
        await Assert.That(pendingBillingRead.GetProperty("canManage").GetBoolean()).IsTrue().Because("The source MCP summary identifies ownership; native mutation controls also reject a pending deletion.");
        var vendorBefore = await owner.GetStringAsync("/__test/billing/plans/probe");
        await ErrorAsync(owner, secret, "change_plan", new { organizationId = businessId, plan = "pro", interval = "monthly" });
        await ErrorAsync(owner, secret, "create_checkout", new { organizationId = businessId, plan = "pro", interval = "monthly" });
        await ErrorAsync(owner, secret, "get_billing_portal_url", new { organizationId = businessId });
        using var nativeBilling = await HttpJourney.PostFormAsync(owner, "/billing/change", new()
        { ["_csrf"] = csrf, ["plan"] = "pro", ["interval"] = "monthly" });
        await Assert.That(nativeBilling.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await owner.GetStringAsync("/__test/billing/plans/probe")).IsEqualTo(vendorBefore);
        // The dashboard records an explicit severity for its own events; requesting deletion is critical.
        var audit = await ToolAsync(replica, secret, "list_audit_events", new { organizationId = businessId, action = "organization.deletion_requested", severity = "critical", range = "7d", page = 999, pageSize = 1 });
        await Assert.That(audit.GetProperty("matching").GetInt32()).IsEqualTo(1);
        await Assert.That(audit.GetProperty("critical").GetInt32()).IsEqualTo(1);
        await Assert.That(audit.GetProperty("unknown").GetInt32()).IsEqualTo(0);
        await Assert.That(audit.GetProperty("page").GetInt32()).IsEqualTo(1);
        await Assert.That(audit.GetProperty("retentionDays").GetInt32()).IsEqualTo(90);
        await Assert.That(audit.GetProperty("events")[0].GetProperty("severity").GetString()).IsEqualTo("critical");
        await ErrorAsync(owner, secret, "list_audit_events", new { organizationId = businessId, pageSize = 101 });
        await ErrorAsync(owner, secret, "request_organization_deletion", new { organizationId = personalId, confirmName = "Personal" });
        await Assert.That(Organization(await ContextAsync(owner, secret), personalId).GetProperty("projects").EnumerateArray()
            .Any(item => item.GetProperty("id").GetString() == personalProject)).IsTrue();
    }

    [Test]
    public async Task NativePatInvitationReplacementTransferLeaveAndRemovalUseFreshProviderMembership()
    {
        using var owner = fixture.CreateClient();
        using var member = fixture.CreateClient();
        using var replica = fixture.CreateClient(replica: true);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var password = "E2E-org-access-" + suffix;
        var ownerEmail = "org-access-owner-" + suffix + "@example.test";
        var memberEmail = "org-access-member-" + suffix + "@example.test";
        await BillingPlanJourneyTests.SignUpPaidAsync(owner, ownerEmail, password, "owner-" + suffix);
        await BillingPlanJourneyTests.SignUpPaidAsync(member, memberEmail, password, "member-" + suffix);
        using var login = await HttpJourney.LoginAsync(replica, ownerEmail, password);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var ownerSecret = await HttpJourney.CreatePatAsync(owner, "Owner access " + suffix, password);
        var memberSecret = await HttpJourney.CreatePatAsync(member, "Member access " + suffix, password);
        var ownerContext = await ContextAsync(owner, ownerSecret);
        var memberContext = await ContextAsync(member, memberSecret);
        var ownerId = ownerContext.GetProperty("user").GetProperty("id").GetString()!;
        var memberId = memberContext.GetProperty("user").GetProperty("id").GetString()!;
        var memberPersonal = memberContext.GetProperty("selection").GetProperty("organizationId").GetString()!;
        var businessId = await CreateBusinessAsync(owner, "Access company " + suffix);
        await ErrorAsync(owner, ownerSecret, "invite_member", new { organizationId = businessId, email = memberEmail, role = "owner" });
        await Assert.That((await ToolAsync(owner, ownerSecret, "list_invitations", new { organizationId = businessId })).GetProperty("invitations").GetArrayLength()).IsEqualTo(0);
        var first = await ToolAsync(owner, ownerSecret, "invite_member", new { organizationId = businessId, email = memberEmail });
        var firstPath = first.GetProperty("inviteUrl").GetString()!;
        await ErrorAsync(replica, ownerSecret, "invite_member", new { organizationId = businessId, email = memberEmail });
        var wrong = await ToolAsync(owner, ownerSecret, "get_invitation", new { token = firstPath });
        await Assert.That(wrong.GetProperty("canRespond").GetBoolean()).IsFalse();
        await ErrorAsync(owner, ownerSecret, "respond_to_invitation", new { token = firstPath, action = "accept" });
        var declined = await ToolAsync(member, memberSecret, "respond_to_invitation", new { token = fixture.Origin + firstPath[1..], action = " DECLINE " });
        await Assert.That(declined.GetProperty("action").GetString()).IsEqualTo("decline");
        await ErrorAsync(member, memberSecret, "get_invitation", new { token = firstPath });
        await Assert.That((await ContextAsync(member, memberSecret)).GetProperty("selection").GetProperty("organizationId").GetString()).IsEqualTo(memberPersonal);
        var issued = await ToolAsync(owner, ownerSecret, "invite_member", new { organizationId = businessId, email = memberEmail });
        var nativeContext = await owner.GetStringAsync("/api/v1/dashboard/context");
        foreach (var internalField in new[] { "tokenHash", "respondedByUserId", "sharedResultConfigs", "accessToken", "customerId" })
            await Assert.That(nativeContext.Contains('"' + internalField + '"', StringComparison.Ordinal)).IsFalse();
        await Assert.That(nativeContext.Contains(issued.GetProperty("inviteUrl").GetString()!["/invitations/".Length..], StringComparison.Ordinal)).IsFalse();
        var replaced = await ToolAsync(replica, ownerSecret, "resend_invitation", new { organizationId = businessId, invitationId = issued.GetProperty("invitation").GetProperty("id").GetString() });
        await ErrorAsync(member, memberSecret, "get_invitation", new { token = issued.GetProperty("inviteUrl").GetString() });
        var token = replaced.GetProperty("inviteUrl").GetString()!;
        var preview = await ToolAsync(member, memberSecret, "get_invitation", new { token = fixture.Origin + token[1..] + "?from=e2e" });
        await Assert.That(preview.GetProperty("canRespond").GetBoolean()).IsTrue();
        var joined = await ToolAsync(member, memberSecret, "respond_to_invitation", new { token, action = "accept" });
        await Assert.That(joined.GetProperty("organizationId").GetString()).IsEqualTo(businessId);
        // ts-auth consumed the token; a repeated answer fails clearly and changes nothing.
        await ErrorAsync(member, memberSecret, "respond_to_invitation", new { token, action = "accept" });
        var members = await ToolAsync(replica, ownerSecret, "list_members", new { organizationId = businessId, search = memberEmail.ToUpperInvariant(), role = "member" });
        await Assert.That(members.GetProperty("members").GetArrayLength()).IsEqualTo(1);
        await Assert.That(members.GetProperty("members")[0].GetProperty("id").GetString()).IsEqualTo(memberId);
        await Assert.That(members.GetProperty("members")[0].GetProperty("joinedAt").GetString()!.Length).IsEqualTo(10);
        await Assert.That(members.GetProperty("roleCounts").GetProperty("owner").GetInt32()).IsEqualTo(1);
        await Assert.That(members.GetProperty("roleCounts").GetProperty("member").GetInt32()).IsEqualTo(1);
        await Assert.That(members.GetProperty("roleCounts").GetProperty("viewer").GetInt32()).IsEqualTo(0);
        var ownerDetails = await owner.GetStringAsync("/organization");
        await Assert.That(ownerDetails.Contains("commandfor=\"rename-organization\"", StringComparison.Ordinal)).IsTrue();
        await Assert.That(ownerDetails.Contains("commandfor=\"transfer-ownership\"", StringComparison.Ordinal)).IsFalse();
        await Assert.That(ownerDetails.Contains("commandfor=\"delete-organization\"", StringComparison.Ordinal)).IsFalse();
        var ownerDanger = await owner.GetStringAsync("/organization?tab=danger");
        await Assert.That(ownerDanger.Contains("id=\"organization-danger\"", StringComparison.Ordinal)).IsTrue();
        await Assert.That(ownerDanger.Contains("commandfor=\"transfer-ownership\"", StringComparison.Ordinal)).IsTrue();
        await Assert.That(ownerDanger.Contains("commandfor=\"delete-organization\"", StringComparison.Ordinal)).IsTrue();
        var dangerNavigation = ownerDanger[ownerDanger.IndexOf("aria-label=\"Organization sections\"", StringComparison.Ordinal)..];
        dangerNavigation = dangerNavigation[..dangerNavigation.IndexOf("</nav>", StringComparison.Ordinal)];
        await Assert.That(dangerNavigation.LastIndexOf("href=\"/organization?tab=danger\"", StringComparison.Ordinal))
            .IsGreaterThan(dangerNavigation.LastIndexOf("href=\"/organization?tab=audit\"", StringComparison.Ordinal));
        using (var invalidDeletion = await HttpJourney.PostFormAsync(owner, "/api/v1/dashboard/organization/deletion", new()
        { ["_csrf"] = await HttpJourney.CsrfAsync(owner, "/organization?tab=danger"), ["confirmation"] = "Wrong name" }))
        {
            await Assert.That(invalidDeletion.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            var invalidBody = await invalidDeletion.Content.ReadAsStringAsync();
            await Assert.That(invalidBody.Contains("id=\"organization-danger\"", StringComparison.Ordinal)).IsTrue();
            await Assert.That(invalidBody.Contains("role=\"alert\"", StringComparison.Ordinal)).IsTrue();
        }
        var ownerMembers = await owner.GetStringAsync("/organization?tab=members");
        await Assert.That(ownerMembers.Contains("id=\"team-invite\"", StringComparison.Ordinal)).IsTrue();
        await Assert.That(ownerMembers.Contains("name=\"role\" required", StringComparison.Ordinal)).IsTrue();
        var ownerCsrf = await HttpJourney.CsrfAsync(owner, "/organization?tab=members");
        // The native member form offers every role ts-auth grants below owner (page audit 7ab7352c: Viewer answered 422).
        using (var admitAdmin = await HttpJourney.PostFormAsync(owner, "/api/v1/access/members/" + Uri.EscapeDataString(memberId) + "/role?tab=members", new()
        { ["_csrf"] = ownerCsrf, ["role"] = "admin" }))
            await Assert.That(admitAdmin.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await ToolAsync(member, memberSecret, "select_organization", new { organizationId = businessId });
        var admittedAdmin = await ContextAsync(member, memberSecret);
        await Assert.That(Organization(admittedAdmin, businessId).GetProperty("role").GetString()).IsEqualTo("admin");
        var adminDetails = await member.GetStringAsync("/organization");
        await Assert.That(adminDetails.Contains("id=\"organization-details\"", StringComparison.Ordinal)).IsTrue();
        await Assert.That(adminDetails.Contains("commandfor=\"rename-organization\"", StringComparison.Ordinal)).IsFalse();
        var adminMembers = await member.GetStringAsync("/organization?tab=members");
        await Assert.That(adminMembers.Contains(ownerEmail, StringComparison.Ordinal)).IsTrue();
        await Assert.That(adminMembers.Contains(memberEmail, StringComparison.Ordinal)).IsTrue();
        await Assert.That(adminMembers.Contains("id=\"team-invite\"", StringComparison.Ordinal)).IsFalse();
        await Assert.That(adminMembers.Contains("name=\"role\" required", StringComparison.Ordinal)).IsFalse();
        await Assert.That(adminMembers.Contains("/api/v1/access/invitations", StringComparison.Ordinal)).IsFalse();
        var adminProjects = await member.GetStringAsync("/organization?tab=projects");
        await Assert.That(adminProjects.Contains("id=\"projects\"", StringComparison.Ordinal)).IsTrue();
        await Assert.That(adminProjects.Contains("commandfor=\"create-project\"", StringComparison.Ordinal)).IsFalse();
        await Assert.That(adminProjects.Contains("commandfor=\"rename-project-", StringComparison.Ordinal)).IsFalse();
        await ErrorAsync(member, memberSecret, "rename_organization", new { organizationId = businessId, name = "Admin cannot rename" });
        // ts-auth offers a read-only viewer role.
        await ToolAsync(owner, ownerSecret, "update_member_role", new { organizationId = businessId, userId = memberId, role = "viewer" });
        await Assert.That(Organization(await ContextAsync(member, memberSecret), businessId).GetProperty("role").GetString()).IsEqualTo("viewer");
        using (var restoreMember = await HttpJourney.PostFormAsync(owner, "/api/v1/access/members/" + Uri.EscapeDataString(memberId) + "/role?tab=members", new()
        { ["_csrf"] = ownerCsrf, ["role"] = "member" }))
            await Assert.That(restoreMember.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        foreach (var (tool, arguments) in new (string, object)[]
        {
            ("rename_organization", new { organizationId = businessId, name = "Forbidden" }),
            ("create_project", new { organizationId = businessId, name = "Forbidden", regions = new[] { "GRU" } }),
            ("invite_member", new { organizationId = businessId, email = "forbidden@example.test" }),
            ("remove_member", new { organizationId = businessId, userId = ownerId }),
            ("request_ownership_transfer", new { organizationId = businessId, userId = ownerId }),
            ("request_organization_deletion", new { organizationId = businessId, confirmName = "Access company " + suffix }),
            ("list_audit_events", new { organizationId = businessId })
        }) await ErrorAsync(member, memberSecret, tool, arguments);
        await ToolAsync(owner, ownerSecret, "request_ownership_transfer", new { organizationId = businessId, userId = memberId });
        var pendingOwner = await ToolAsync(owner, ownerSecret, "get_organization", new { organizationId = businessId });
        await Assert.That(pendingOwner.GetProperty("pendingOwnerUserId").GetString()).IsEqualTo(memberId);
        await Assert.That(pendingOwner.GetProperty("pendingOwnerExpiresAt").GetDateTimeOffset()).IsGreaterThan(DateTimeOffset.UtcNow);
        await Assert.That(pendingOwner.GetProperty("canConfirmOwnership").GetBoolean()).IsFalse();
        await Assert.That((await ToolAsync(member, memberSecret, "get_organization", new { organizationId = businessId })).GetProperty("canConfirmOwnership").GetBoolean()).IsTrue();
        var incomingDanger = await member.GetStringAsync("/organization?tab=danger");
        await Assert.That(incomingDanger.Contains("You are asked to take ownership", StringComparison.Ordinal)).IsTrue();
        await Assert.That(incomingDanger.Contains("/api/v1/dashboard/organization/members/ownership/confirm", StringComparison.Ordinal)).IsTrue();
        await Assert.That(incomingDanger.Contains("commandfor=\"delete-organization\"", StringComparison.Ordinal)).IsFalse();
        await ErrorAsync(owner, ownerSecret, "confirm_ownership_transfer", new { organizationId = businessId });
        await ToolAsync(member, memberSecret, "confirm_ownership_transfer", new { organizationId = businessId });
        var transferred = await ToolAsync(member, memberSecret, "get_organization", new { organizationId = businessId });
        await Assert.That(transferred.TryGetProperty("pendingOwnerUserId", out _)).IsFalse();
        await Assert.That(transferred.TryGetProperty("pendingOwnerExpiresAt", out _)).IsFalse();
        await ErrorAsync(replica, ownerSecret, "rename_organization", new { organizationId = businessId, name = "Former owner denied" });
        await ToolAsync(member, memberSecret, "request_ownership_transfer", new { organizationId = businessId, userId = ownerId });
        await ToolAsync(replica, ownerSecret, "confirm_ownership_transfer", new { organizationId = businessId });
        await ToolAsync(member, memberSecret, "leave_organization", new { organizationId = businessId });
        await Assert.That((await ContextAsync(member, memberSecret)).GetProperty("selection").GetProperty("organizationId").GetString()).IsEqualTo(memberPersonal);
        await ErrorAsync(member, memberSecret, "list_members", new { organizationId = businessId });
        var rejoin = await ToolAsync(owner, ownerSecret, "invite_member", new { organizationId = businessId, email = memberEmail });
        await ToolAsync(member, memberSecret, "respond_to_invitation", new { token = rejoin.GetProperty("inviteUrl").GetString(), action = "accept" });
        await ToolAsync(replica, ownerSecret, "remove_member", new { organizationId = businessId, userId = memberId });
        using var signedOut = await member.GetAsync("/api/v1/auth/me");
        await Assert.That(signedOut.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await ErrorAsync(member, memberSecret, "list_members", new { organizationId = businessId });
        var inviteCsrf = await HttpJourney.CsrfAsync(owner, "/organization?tab=invitations");
        using (var viewerInvite = await HttpJourney.PostFormAsync(owner, "/api/v1/access/invitations?tab=invitations", new()
        { ["_csrf"] = inviteCsrf, ["email"] = memberEmail, ["role"] = "viewer" }))
            await Assert.That(viewerInvite.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var revoked = (await ToolAsync(owner, ownerSecret, "list_invitations", new { organizationId = businessId })).GetProperty("invitations").EnumerateArray()
            .Single(invite => invite.GetProperty("open").GetBoolean());
        await Assert.That(revoked.GetProperty("role").GetString()).IsEqualTo("viewer");
        await ToolAsync(replica, ownerSecret, "revoke_invitation", new { organizationId = businessId, invitationId = revoked.GetProperty("id").GetString() });
        await Assert.That((await ToolAsync(owner, ownerSecret, "list_invitations", new { organizationId = businessId })).GetProperty("invitations").EnumerateArray()
            .Count(invite => invite.GetProperty("open").GetBoolean())).IsEqualTo(0);
    }

    [Test]
    public async Task NativeExpiredInvitationsRemainManageableAndReplacementRejectsTheOldLink()
    {
        using var owner = fixture.CreateClient();
        using var replica = fixture.CreateClient(replica: true);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var email = "org-expiry-" + suffix + "@example.test";
        var password = "E2E-org-expiry-" + suffix;
        await BillingPlanJourneyTests.SignUpPaidAsync(owner, email, password, suffix);
        using var login = await HttpJourney.LoginAsync(replica, email, password);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var secret = await HttpJourney.CreatePatAsync(owner, "Invitation expiry " + suffix, password);
        var orgId = (await ContextAsync(owner, secret)).GetProperty("selection").GetProperty("organizationId").GetString()!;
        var issued = await ToolAsync(owner, secret, "invite_member", new { organizationId = orgId, email = "invited-expiry-" + suffix + "@example.test" });
        var id = issued.GetProperty("invitation").GetProperty("id").GetString()!;
        var path = issued.GetProperty("inviteUrl").GetString()!;
        await ExpireAsync(id);
        using (var unavailable = await replica.GetAsync(path)) await Assert.That(unavailable.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await ErrorAsync(owner, secret, "get_invitation", new { token = path });
        var list = await ToolAsync(replica, secret, "list_invitations", new { organizationId = orgId });
        await Assert.That(list.GetProperty("invitations").EnumerateArray().Single(invite => invite.GetProperty("id").GetString() == id)
            .GetProperty("status").GetString()).IsEqualTo("Expired");
        var html = await replica.GetStringAsync("/organization?tab=invitations");
        await Assert.That(html.Contains("/api/v1/access/invitations/" + id + "/resend?tab=invitations", StringComparison.Ordinal)).IsTrue();
        await Assert.That(html.Contains("/api/v1/access/invitations/" + id + "/revoke?tab=invitations", StringComparison.Ordinal)).IsTrue();
        var replaced = await ToolAsync(replica, secret, "resend_invitation", new { organizationId = orgId, invitationId = id });
        await Assert.That(replaced.GetProperty("invitation").GetProperty("id").GetString() == id).IsFalse();
        await ToolAsync(owner, secret, "get_invitation", new { token = replaced.GetProperty("inviteUrl").GetString() });
        await ErrorAsync(owner, secret, "get_invitation", new { token = path });
        var revoked = await ToolAsync(owner, secret, "invite_member", new { organizationId = orgId, email = "cancel-expiry-" + suffix + "@example.test" });
        var revokedId = revoked.GetProperty("invitation").GetProperty("id").GetString()!;
        await ExpireAsync(revokedId);
        var csrf = await HttpJourney.CsrfAsync(replica, "/organization?tab=invitations");
        using (var canceled = await HttpJourney.PostFormAsync(replica, "/api/v1/access/invitations/" + revokedId + "/revoke?tab=invitations", new() { ["_csrf"] = csrf }))
            await Assert.That(canceled.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var canceledList = await ToolAsync(owner, secret, "list_invitations", new { organizationId = orgId });
        await Assert.That(canceledList.GetProperty("invitations").EnumerateArray().Single(invite => invite.GetProperty("id").GetString() == revokedId)
            .GetProperty("status").GetString()).IsEqualTo("Canceled");

        async Task ExpireAsync(string invitationId)
        {
            var token = await HttpJourney.CsrfAsync(owner, "/organization?tab=invitations");
            using var response = await HttpJourney.PostFormAsync(owner, "/__test/organization/invitations/expire", new()
            { ["_csrf"] = token, ["invitationId"] = invitationId });
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        }
    }

    [Test]
    public async Task NativePatTwoReplicaMemberCapacityCountsPendingInvitationsAndRejectsDuplicates()
    {
        using var owner = fixture.CreateClient();
        using var replica = fixture.CreateClient(replica: true);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var email = "org-quota-owner-" + suffix + "@example.test";
        var password = "E2E-org-quota-" + suffix;
        await BillingPlanJourneyTests.SignUpPaidAsync(owner, email, password, suffix);
        using var login = await HttpJourney.LoginAsync(replica, email, password);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var secret = await HttpJourney.CreatePatAsync(owner, "Quota E2E " + suffix, password);
        var context = await ContextAsync(owner, secret);
        var orgId = context.GetProperty("selection").GetProperty("organizationId").GetString()!;
        await ErrorAsync(owner, secret, "invite_member", new { organizationId = orgId, email });
        for (var index = 0; index < 3; index++)
            await ToolAsync(owner, secret, "invite_member", new { organizationId = orgId, email = "quota-" + suffix + "-" + index + "@example.test" });
        var lastSlot = await Task.WhenAll(
            HttpJourney.McpAsync(owner, secret, "tools/call", new { name = "invite_member", arguments = new { organizationId = orgId, email = "quota-" + suffix + "-a@example.test" } }),
            HttpJourney.McpAsync(replica, secret, "tools/call", new { name = "invite_member", arguments = new { organizationId = orgId, email = "quota-" + suffix + "-b@example.test" } }));
        await Assert.That(lastSlot.Count(item => item.GetProperty("result").TryGetProperty("isError", out var error) && error.GetBoolean())).IsEqualTo(1);
        var pending = await ToolAsync(owner, secret, "list_invitations", new { organizationId = orgId });
        await Assert.That(pending.GetProperty("invitations").EnumerateArray().Count(item => item.GetProperty("status").GetString() == "Pending")).IsEqualTo(4);
        await ErrorAsync(owner, secret, "invite_member", new { organizationId = orgId, email = "quota-" + suffix + "-overflow@example.test" });
        await ToolAsync(owner, secret, "revoke_invitation", new { organizationId = orgId, invitationId = pending.GetProperty("invitations")[0].GetProperty("id").GetString() });
        await ToolAsync(replica, secret, "invite_member", new { organizationId = orgId, email = "quota-" + suffix + "-replacement@example.test" });
    }

    private static async Task<string> CreateBusinessAsync(HttpClient client, string name)
    {
        var csrf = await HttpJourney.CsrfAsync(client, "/organization");
        using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/organization/new-organization", new()
        { ["_csrf"] = csrf, ["step"] = "organization", ["name"] = name });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var me = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me"));
        return me.RootElement.GetProperty("organizationId").GetString()!;
    }
    private static Task<JsonElement> ContextAsync(HttpClient client, string secret) => ToolAsync(client, secret, "get_context", new { });
    private static JsonElement Organization(JsonElement context, string id) => context.GetProperty("organizations").EnumerateArray().Single(item => item.GetProperty("id").GetString() == id);
    private static async Task<JsonElement> ToolAsync(HttpClient client, string secret, string name, object arguments)
    {
        var response = await HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments });
        var result = response.GetProperty("result");
        await Assert.That(result.TryGetProperty("isError", out var error) && error.GetBoolean()).IsFalse().Because(response.ToString());
        return result.GetProperty("structuredContent");
    }
    private static async Task ErrorAsync(HttpClient client, string secret, string name, object arguments)
    {
        var response = await HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments });
        await Assert.That(response.GetProperty("result").GetProperty("isError").GetBoolean()).IsTrue().Because(response.ToString());
    }
}
