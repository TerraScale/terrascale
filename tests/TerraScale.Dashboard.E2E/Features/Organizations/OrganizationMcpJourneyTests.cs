using System.Net;
using System.Text.Json;
using TerraScale.Dashboard.E2E.Features.Billing;
using TUnit.Core;

namespace TerraScale.Dashboard.E2E.Features.Organizations;

[ClassDataSource<DashboardE2EFixture>]
public sealed class OrganizationMcpJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public async Task NativePatProjectScopesAndConfirmedDeletionEndBillingAndRemoveTheOrganization()
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
        { organizationId = personalId, name = "Explicit " + suffix, region = "SJC", backupRegion = "GRU", regions = new[] { "SJC", "GRU", "SJC" } });
        var projectId = project.GetProperty("id").GetString()!;
        await Assert.That(project.GetProperty("organizationId").GetString()).IsEqualTo(personalId);
        await Assert.That(project.GetProperty("region").GetString()).IsEqualTo("SJC");
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
        await Assert.That(renamed.GetProperty("region").GetString()).IsEqualTo("SJC");
        var checkout = await ToolAsync(owner, secret, "create_checkout", new { organizationId = businessId, plan = "starter", interval = "annual" });
        using var vendor = await owner.GetAsync(checkout.GetProperty("checkoutUrl").GetString());
        await Assert.That(vendor.StatusCode).IsEqualTo(HttpStatusCode.Found);
        using var returned = await replica.GetAsync(vendor.Headers.Location);
        await Assert.That(returned.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await ToolAsync(owner, secret, "create_project", new { organizationId = businessId, name = "Deleted with the organization " + suffix, regions = new[] { "GRU" } });
        await ToolAsync(owner, secret, "rename_organization", new { organizationId = businessId, name = name + " renamed" });
        await ErrorAsync(owner, secret, "request_organization_deletion", new { organizationId = businessId, confirmName = name });
        await ErrorAsync(owner, secret, "list_audit_events", new { organizationId = businessId, pageSize = 101 });
        // The confirmed request deletes the organization at once: Stripe ends its plan, ts-auth drops it and the account moves on (Ku TS-916).
        var csrf = await HttpJourney.CsrfAsync(owner, "/organization");
        using (var native = await HttpJourney.PostFormAsync(owner, "/api/v1/dashboard/organization/deletion", new()
        { ["_csrf"] = csrf, ["confirmation"] = name + " renamed" }))
        {
            await Assert.That(native.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(native.Headers.Location?.OriginalString).StartsWith("/organization?deleted=1&organizationId=" + personalId);
        }
        var deleted = await ContextAsync(replica, secret);
        await Assert.That(deleted.GetProperty("selection").GetProperty("organizationId").GetString()).IsEqualTo(personalId);
        await Assert.That(deleted.GetProperty("organizations").EnumerateArray().Any(item => item.GetProperty("id").GetString() == businessId)).IsFalse();
        using (var subscriptions = JsonDocument.Parse(await owner.GetStringAsync("/__test/billing/plans/subscriptions/" + businessId)))
            await Assert.That(subscriptions.RootElement.EnumerateArray().Select(item => item.GetString()!).ToArray()).IsEquivalentTo(new[] { "canceled" });
        await ErrorAsync(replica, secret, "request_organization_deletion", new { organizationId = businessId, confirmName = name + " renamed" });
        await ErrorAsync(owner, secret, "create_project", new { organizationId = businessId, name = "Deletion denied", regions = new[] { "GRU" } });
        await ErrorAsync(owner, secret, "request_organization_deletion", new { organizationId = personalId, confirmName = "Personal" });
        await Assert.That(Organization(await ContextAsync(owner, secret), personalId).GetProperty("projects").EnumerateArray()
            .Any(item => item.GetProperty("id").GetString() == personalProject)).IsTrue();
    }

    [Test]
    public async Task NativePatInvitationReplacementTransferLeaveAndRemovalUseFreshProviderMembership()
    {
        using var owner = fixture.CreateClient();
        using var member = fixture.CreateClient();
        using var teammate = fixture.CreateClient();
        using var replica = fixture.CreateClient(replica: true);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var password = "E2E-org-access-" + suffix;
        var ownerEmail = "org-access-owner-" + suffix + "@example.test";
        var memberEmail = "org-access-member-" + suffix + "@example.test";
        var teammateEmail = "org-access-teammate-" + suffix + "@example.test";
        await BillingPlanJourneyTests.SignUpPaidAsync(owner, ownerEmail, password, "owner-" + suffix);
        await BillingPlanJourneyTests.SignUpPaidAsync(member, memberEmail, password, "member-" + suffix);
        await BillingPlanJourneyTests.SignUpPaidAsync(teammate, teammateEmail, password, "teammate-" + suffix);
        using var login = await HttpJourney.LoginAsync(replica, ownerEmail, password);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var ownerSecret = await HttpJourney.CreatePatAsync(owner, "Owner access " + suffix, password);
        var memberSecret = await HttpJourney.CreatePatAsync(member, "Member access " + suffix, password);
        var teammateSecret = await HttpJourney.CreatePatAsync(teammate, "Teammate access " + suffix, password);
        var teammateId = (await ContextAsync(teammate, teammateSecret)).GetProperty("user").GetProperty("id").GetString()!;
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
        await Assert.That(adminDetails.Contains("commandfor=\"rename-organization\"", StringComparison.Ordinal)).IsTrue();
        await Assert.That(adminDetails.Contains("/mfa-policy", StringComparison.Ordinal)).IsFalse();
        var adminMembers = await member.GetStringAsync("/organization?tab=members");
        await Assert.That(adminMembers.Contains("id=\"team-invite\"", StringComparison.Ordinal)).IsTrue();
        // Neither the owner nor the caller can be edited, even by an admin.
        var protectedMembers = (await ToolAsync(member, memberSecret, "list_members", new { organizationId = businessId })).GetProperty("members");
        await Assert.That(protectedMembers.EnumerateArray().All(item => !item.GetProperty("canEdit").GetBoolean())).IsTrue();
        var adminCsrf = await HttpJourney.CsrfAsync(member, "/organization?tab=members");
        using (var renamed = await HttpJourney.PostFormAsync(member, "/api/v1/dashboard/organization/rename", new()
        { ["_csrf"] = adminCsrf, ["name"] = "Admin renamed " + suffix }))
            await Assert.That(renamed.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(Organization(await ContextAsync(replica, ownerSecret), businessId).GetProperty("name").GetString()).IsEqualTo("Admin renamed " + suffix);
        await ToolAsync(member, memberSecret, "rename_organization", new { organizationId = businessId, name = "Access company " + suffix });
        await ErrorAsync(member, memberSecret, "create_project", new { organizationId = businessId, name = "Unpaid denied", regions = new[] { "GRU" } });
        await ErrorAsync(member, memberSecret, "create_checkout", new { organizationId = businessId, plan = "starter", interval = "annual" });
        // Only the owner activates billing; management permission never bypasses provisioning policy.
        var checkout = await ToolAsync(owner, ownerSecret, "create_checkout", new { organizationId = businessId, plan = "starter", interval = "annual" });
        using (var vendor = await owner.GetAsync(checkout.GetProperty("checkoutUrl").GetString()))
        {
            await Assert.That(vendor.StatusCode).IsEqualTo(HttpStatusCode.Found);
            using var returned = await owner.GetAsync(vendor.Headers.Location);
            await Assert.That(returned.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        }
        var adminProject = await ToolAsync(member, memberSecret, "create_project", new { organizationId = businessId, name = "Admin project " + suffix, regions = new[] { "GRU" } });
        var adminProjectId = adminProject.GetProperty("id").GetString()!;
        using (var renamed = await HttpJourney.PostFormAsync(member, "/api/v1/dashboard/organization/projects/" + adminProjectId + "/rename", new()
        { ["_csrf"] = adminCsrf, ["name"] = "Admin project renamed " + suffix, ["region"] = "GRU" }))
            await Assert.That(renamed.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await ToolAsync(member, memberSecret, "update_project", new { organizationId = businessId, projectId = adminProjectId, name = "Admin project renamed " + suffix, regions = new[] { "GRU", "SJC" } });
        var savedProject = Organization(await ContextAsync(replica, ownerSecret), businessId).GetProperty("projects").EnumerateArray().Single(item => item.GetProperty("id").GetString() == adminProjectId);
        await Assert.That(savedProject.GetProperty("name").GetString()).IsEqualTo("Admin project renamed " + suffix);
        await Assert.That(savedProject.GetProperty("regions").GetArrayLength()).IsEqualTo(2);
        var adminProjects = await member.GetStringAsync("/organization?tab=projects");
        await Assert.That(adminProjects.Contains("commandfor=\"create-project\"", StringComparison.Ordinal)).IsTrue();
        await Assert.That(adminProjects.Contains("commandfor=\"rename-project-" + adminProjectId, StringComparison.Ordinal)).IsTrue();
        // Starter allows five projects; the sixth is refused before the provider mutation.
        for (var index = 1; index < 5; index++)
        {
            using var created = await HttpJourney.PostFormAsync(member, "/api/v1/dashboard/organization/projects", new()
            { ["_csrf"] = adminCsrf, ["name"] = "Admin quota " + suffix + index, ["regions"] = "GRU" });
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        }
        using (var full = await HttpJourney.PostFormAsync(member, "/api/v1/dashboard/organization/projects", new()
        { ["_csrf"] = adminCsrf, ["name"] = "Admin quota overflow", ["regions"] = "GRU" }))
        {
            await Assert.That(full.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            await Assert.That(await full.Content.ReadAsStringAsync()).Contains("project limit (5 projects)");
        }
        await Assert.That(Organization(await ContextAsync(replica, ownerSecret), businessId).GetProperty("projects").GetArrayLength()).IsEqualTo(5);
        foreach (var protectedId in new[] { ownerId, memberId })
        {
            await ErrorAsync(member, memberSecret, "update_member_role", new { organizationId = businessId, userId = protectedId, role = "viewer" });
            await ErrorAsync(member, memberSecret, "remove_member", new { organizationId = businessId, userId = protectedId });
        }
        await ErrorAsync(member, memberSecret, "request_ownership_transfer", new { organizationId = businessId, userId = ownerId });
        using (var deletion = await HttpJourney.PostFormAsync(member, "/api/v1/dashboard/organization/deletion", new()
        { ["_csrf"] = adminCsrf, ["confirmation"] = "Access company " + suffix }))
            await Assert.That(deletion.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        foreach (var role in new[] { "viewer", "member", "admin" })
        {
            using (var invited = await HttpJourney.PostFormAsync(member, "/api/v1/access/invitations?tab=invitations", new()
            { ["_csrf"] = adminCsrf, ["email"] = teammateEmail, ["role"] = role }))
                await Assert.That(invited.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var invitation = (await ToolAsync(member, memberSecret, "list_invitations", new { organizationId = businessId })).GetProperty("invitations").EnumerateArray().Single(item => item.GetProperty("open").GetBoolean());
            await Assert.That(invitation.GetProperty("role").GetString()).IsEqualTo(role);
            var replacement = await ToolAsync(member, memberSecret, "resend_invitation", new { organizationId = businessId, invitationId = invitation.GetProperty("id").GetString() });
            var replacementId = replacement.GetProperty("invitation").GetProperty("id").GetString()!;
            if (role == "admin")
                await ToolAsync(teammate, teammateSecret, "respond_to_invitation", new { token = replacement.GetProperty("inviteUrl").GetString(), action = "accept" });
            else
            {
                if (role == "viewer")
                {
                    using var canceledInvitation = await HttpJourney.PostFormAsync(member, "/api/v1/access/invitations/" + replacementId + "/revoke?tab=invitations", new() { ["_csrf"] = adminCsrf });
                    await Assert.That(canceledInvitation.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
                }
                else await ToolAsync(member, memberSecret, "revoke_invitation", new { organizationId = businessId, invitationId = replacementId });
                await ErrorAsync(teammate, teammateSecret, "get_invitation", new { token = replacement.GetProperty("inviteUrl").GetString() });
            }
        }
        await ErrorAsync(member, memberSecret, "request_ownership_transfer", new { organizationId = businessId, userId = teammateId });
        using (var transfer = await HttpJourney.PostFormAsync(member, "/api/v1/dashboard/organization/members/ownership", new()
        { ["_csrf"] = adminCsrf, ["userID"] = teammateId }))
            await Assert.That(transfer.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        var editable = await ToolAsync(member, memberSecret, "list_members", new { organizationId = businessId, search = teammateEmail });
        await Assert.That(editable.GetProperty("members")[0].GetProperty("canEdit").GetBoolean()).IsTrue();
        await Assert.That((await member.GetStringAsync("/organization?tab=members")).Contains("/api/v1/access/members/" + teammateId + "/role", StringComparison.Ordinal)).IsTrue();
        using (var changed = await HttpJourney.PostFormAsync(member, "/api/v1/access/members/" + teammateId + "/role?tab=members", new()
        { ["_csrf"] = adminCsrf, ["role"] = "viewer" }))
            await Assert.That(changed.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(Organization(await ContextAsync(teammate, teammateSecret), businessId).GetProperty("role").GetString()).IsEqualTo("viewer");
        await ToolAsync(member, memberSecret, "update_member_role", new { organizationId = businessId, userId = teammateId, role = "admin" });
        // The same session and PAT immediately lose management when ts-auth demotes the admin.
        foreach (var role in new[] { "viewer", "member" })
        {
            await ToolAsync(owner, ownerSecret, "update_member_role", new { organizationId = businessId, userId = memberId, role });
            await Assert.That(Organization(await ContextAsync(member, memberSecret), businessId).GetProperty("role").GetString()).IsEqualTo(role);
            await Assert.That((await member.GetStringAsync("/organization?tab=members")).Contains("id=\"team-invite\"", StringComparison.Ordinal)).IsFalse();
            await Assert.That((await member.GetStringAsync("/organization")).Contains("commandfor=\"rename-organization\"", StringComparison.Ordinal)).IsFalse();
            using (var denied = await HttpJourney.PostFormAsync(member, "/api/v1/dashboard/organization/rename", new()
            { ["_csrf"] = adminCsrf, ["name"] = "Forbidden" }))
                await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            foreach (var (tool, arguments) in new (string, object)[]
            {
                ("rename_organization", new { organizationId = businessId, name = "Forbidden" }),
                ("create_project", new { organizationId = businessId, name = "Forbidden", regions = new[] { "GRU" } }),
                ("update_project", new { organizationId = businessId, projectId = adminProjectId, name = "Forbidden" }),
                ("invite_member", new { organizationId = businessId, email = "forbidden@example.test" }),
                ("update_member_role", new { organizationId = businessId, userId = teammateId, role = "admin" }),
                ("remove_member", new { organizationId = businessId, userId = teammateId }),
                ("request_ownership_transfer", new { organizationId = businessId, userId = ownerId }),
                ("request_organization_deletion", new { organizationId = businessId, confirmName = "Access company " + suffix }),
                ("list_audit_events", new { organizationId = businessId })
            }) await ErrorAsync(member, memberSecret, tool, arguments);
        }
        await ToolAsync(owner, ownerSecret, "update_member_role", new { organizationId = businessId, userId = memberId, role = "admin" });
        using (var removed = await HttpJourney.PostFormAsync(member, "/api/v1/access/members/" + teammateId + "/revoke?tab=members", new() { ["_csrf"] = adminCsrf }))
            await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using (var signedOutTeammate = await teammate.GetAsync("/api/v1/auth/me"))
            await Assert.That(signedOutTeammate.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await ErrorAsync(teammate, teammateSecret, "list_members", new { organizationId = businessId });
        await ToolAsync(owner, ownerSecret, "update_member_role", new { organizationId = businessId, userId = memberId, role = "member" });
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
        await ToolAsync(replica, ownerSecret, "rename_organization", new { organizationId = businessId, name = "Former owner manages as admin " + suffix });
        await ErrorAsync(replica, ownerSecret, "request_ownership_transfer", new { organizationId = businessId, userId = memberId });
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
