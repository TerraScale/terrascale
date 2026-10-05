using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

/// <summary>The Management client against a real TerraBase process; nothing on the HTTP path is mocked.</summary>
[ClassDataSource<TerraBaseServer>(Shared = SharedType.PerTestSession)]
public sealed class TerraBaseManagementTests(TerraBaseServer server)
{
    private static string NewUser() => "user-" + Guid.NewGuid().ToString("N");

    [Test]
    public async Task OrganizationsAreCreatedListedRenamedSwitchedAndDeleted()
    {
        server.Require();
        var management = server.Management();
        var owner = NewUser();

        var organization = await management.CreateOrganizationAsync(owner, "Acme");
        await Assert.That(organization.CreatedBy).IsEqualTo(owner);
        await Assert.That(organization.Plan).IsEqualTo(TerraBasePlan.Hobby);
        var taken = await Assert.ThrowsAsync<TerraBaseConflictException>(() => management.CreateOrganizationAsync(owner, "acme"));
        await Assert.That(taken!.Message).IsEqualTo("name is already taken");
        await Assert.That(taken.ErrorCode).IsNull();

        var second = await management.CreateOrganizationAsync(owner, "Beta");
        var page = await management.ListOrganizationsAsync(owner, limit: 1);
        await Assert.That(page.Items.Single().Id).IsEqualTo(organization.Id);
        var rest = await management.ListOrganizationsAsync(owner, after: page.Next);
        await Assert.That(rest.Items.Select(item => item.Id)).IsEquivalentTo([second.Id]);
        await Assert.That(rest.Next).IsNull();

        await Assert.That((await management.GetOrganizationAsync(owner, organization.Id)).Name).IsEqualTo("Acme");
        await Assert.That((await management.RenameOrganizationAsync(owner, organization.Id, "Acme Inc")).Name).IsEqualTo("Acme Inc");
        await Assert.That((await management.SetPlanAsync(owner, organization.Id, TerraBasePlan.Pro)).Plan).IsEqualTo(TerraBasePlan.Pro);

        await Assert.ThrowsAsync<TerraBaseNotFoundException>(() => management.GetCurrentOrganizationAsync(owner));
        await management.SetCurrentOrganizationAsync(owner, second.Id);
        await Assert.That((await management.GetCurrentOrganizationAsync(owner)).Id).IsEqualTo(second.Id);

        var stranger = await Assert.ThrowsAsync<TerraBaseNotFoundException>(() => management.GetOrganizationAsync(NewUser(), organization.Id));
        await Assert.That(stranger!.ErrorCode).IsEqualTo("unknown_route");
        await Assert.That(stranger.Message).IsEqualTo("TerraBase answered HTTP 404 (unknown_route).");
        await management.DeleteOrganizationAsync(owner, second.Id);
        await Assert.ThrowsAsync<TerraBaseNotFoundException>(() => management.GetOrganizationAsync(owner, second.Id));
    }

    [Test]
    public async Task ProjectsDatabasesAndBranchesAreManaged()
    {
        server.Require();
        var management = server.Management();
        var owner = NewUser();
        var organization = await management.CreateOrganizationAsync(owner, "Projects");

        var project = await management.CreateProjectAsync(owner, organization.Id, "Production");
        await Assert.That(project.OrganizationId).IsEqualTo(organization.Id);
        await Assert.ThrowsAsync<TerraBaseConflictException>(() => management.CreateProjectAsync(owner, organization.Id, "production"));
        await Assert.That((await management.ListProjectsAsync(owner, organization.Id)).Items.Single().Id).IsEqualTo(project.Id);
        await Assert.That((await management.GetProjectAsync(owner, organization.Id, project.Id)).Name).IsEqualTo("Production");
        await Assert.That((await management.RenameProjectAsync(owner, organization.Id, project.Id, "Prod")).Name).IsEqualTo("Prod");

        var main = await management.CreateDatabaseAsync(owner, organization.Id, project.Id, "main");
        await Assert.That(main.BranchedFrom).IsNull();

        // The data written through the Documents API is what the branch copies.
        var documents = server.Documents(await server.CreateApiKeyAsync(organization.Id)).Database(project.Id, "main");
        await documents.CreateCollectionAsync("items");
        using var document = System.Text.Json.JsonDocument.Parse("""{"a":1}""");
        var written = await documents.CreateDocumentAsync("items", document.RootElement);

        var branch = await management.BranchDatabaseAsync(owner, organization.Id, project.Id, main.Id, "feature");
        await Assert.That(branch.BranchedFrom).IsEqualTo(main.Id);
        var copied = server.Documents(await server.CreateApiKeyAsync(organization.Id)).Database(project.Id, "feature");
        await Assert.That((await copied.GetDocumentAsync("items", written.Id))!.Document.GetProperty("a").GetInt32()).IsEqualTo(1);

        var databases = await management.ListDatabasesAsync(owner, organization.Id, project.Id);
        await Assert.That(databases.Items.Select(database => database.Name).SequenceEqual(["main", "feature"])).IsTrue();
        await Assert.That((await management.GetDatabaseAsync(owner, organization.Id, project.Id, branch.Id)).Name).IsEqualTo("feature");

        var notEmpty = await Assert.ThrowsAsync<TerraBaseConflictException>(() => management.DeleteProjectAsync(owner, organization.Id, project.Id));
        await Assert.That(notEmpty!.Message).IsEqualTo("delete what it contains first");
        await Assert.ThrowsAsync<TerraBaseConflictException>(() => management.DeleteOrganizationAsync(owner, organization.Id));

        await management.DeleteDatabaseAsync(owner, organization.Id, project.Id, main.Id);
        await management.DeleteDatabaseAsync(owner, organization.Id, project.Id, branch.Id);
        await management.DeleteProjectAsync(owner, organization.Id, project.Id);
        await management.DeleteOrganizationAsync(owner, organization.Id);
    }

    [Test]
    public async Task MembersInvitationsOwnershipTransferAndAuditWorkTogether()
    {
        server.Require();
        var management = server.Management();
        var owner = NewUser();
        var invitee = NewUser();
        var organization = await management.CreateOrganizationAsync(owner, "Access");

        var invitation = await management.CreateInvitationAsync(owner, organization.Id, "ada@example.com", TerraBaseRole.Admin);
        await Assert.That(invitation.Invitation.Role).IsEqualTo(TerraBaseRole.Admin);
        await Assert.That(invitation.ToString()).DoesNotContain(invitation.Token);
        await Assert.ThrowsAsync<TerraBaseConflictException>(() => management.CreateInvitationAsync(owner, organization.Id, "ada@example.com"));
        await Assert.That((await management.ListInvitationsAsync(owner, organization.Id)).Items.Single().Id).IsEqualTo(invitation.Invitation.Id);

        await Assert.ThrowsAsync<TerraBaseForbiddenException>(() => management.AcceptInvitationAsync(invitee, invitation.Token, "bob@example.com"));
        var joined = await management.AcceptInvitationAsync(invitee, invitation.Token, "ADA@example.com");
        await Assert.That(joined).IsEqualTo(joined with { UserId = invitee, Role = TerraBaseRole.Admin });
        await Assert.That((await management.ListMembersAsync(owner, organization.Id)).Items.Count).IsEqualTo(2);

        var revoked = await management.CreateInvitationAsync(owner, organization.Id, "eve@example.com");
        await Assert.That(revoked.Invitation.Role).IsEqualTo(TerraBaseRole.Member);
        await management.RevokeInvitationAsync(owner, organization.Id, revoked.Invitation.Id);
        await Assert.ThrowsAsync<TerraBaseNotFoundException>(() => management.AcceptInvitationAsync(NewUser(), revoked.Token, "eve@example.com"));

        // An admin cannot touch owners; the last owner can neither leave nor be demoted.
        await Assert.ThrowsAsync<TerraBaseForbiddenException>(() => management.ChangeMemberRoleAsync(invitee, organization.Id, owner, TerraBaseRole.Member));
        await Assert.ThrowsAsync<TerraBaseConflictException>(() => management.LeaveOrganizationAsync(owner, organization.Id));
        await Assert.That((await management.ChangeMemberRoleAsync(owner, organization.Id, invitee, TerraBaseRole.Member)).Role).IsEqualTo(TerraBaseRole.Member);
        await Assert.ThrowsAsync<TerraBaseForbiddenException>(() => management.GetAuditRetentionAsync(invitee, organization.Id));

        var transfer = await management.StartOwnershipTransferAsync(owner, organization.Id, invitee);
        await Assert.That(transfer.ToUserId).IsEqualTo(invitee);
        await Assert.That((await management.GetOwnershipTransferAsync(invitee, organization.Id)).FromUserId).IsEqualTo(owner);
        await management.CancelOwnershipTransferAsync(invitee, organization.Id);
        await Assert.ThrowsAsync<TerraBaseNotFoundException>(() => management.GetOwnershipTransferAsync(owner, organization.Id));
        await management.StartOwnershipTransferAsync(owner, organization.Id, invitee);
        var confirmed = await management.ConfirmOwnershipTransferAsync(invitee, organization.Id);
        await Assert.That(confirmed.Role).IsEqualTo(TerraBaseRole.Owner);

        await Assert.That((await management.GetAuditRetentionAsync(invitee, organization.Id)).Days).IsEqualTo(90);
        await Assert.That((await management.SetAuditRetentionAsync(invitee, organization.Id, 30)).Days).IsEqualTo(30);
        await Assert.ThrowsAsync<TerraBaseValidationException>(() => management.SetAuditRetentionAsync(invitee, organization.Id, 91));
        var events = await management.ListAuditEventsAsync(invitee, organization.Id, limit: 2);
        await Assert.That(events.Items.Select(audit => audit.Action).SequenceEqual(["settings.audit_retention_updated", "ownership.transfer_confirmed"])).IsTrue();
        await Assert.That(events.Next).IsNotNull();
        var older = await management.ListAuditEventsAsync(invitee, organization.Id, after: events.Next);
        await Assert.That(older.Items.Select(audit => audit.Action)).Contains("invitation.accepted");

        await management.RemoveMemberAsync(invitee, organization.Id, owner);
        await Assert.ThrowsAsync<TerraBaseNotFoundException>(() => management.GetOrganizationAsync(owner, organization.Id));
    }

    [Test]
    public async Task IdentityAndCredentialErrorsMapToTypedExceptions()
    {
        server.Require();
        var wrongCredential = await Assert.ThrowsAsync<TerraBaseAuthenticationException>(
            () => server.Management(new string('x', 40)).ListOrganizationsAsync(NewUser()));
        await Assert.That(wrongCredential!.ErrorCode).IsEqualTo("invalid_key");

        // An API key grants no management access.
        var tenant = await server.CreateTenantAsync();
        await Assert.ThrowsAsync<TerraBaseAuthenticationException>(() => server.Management(tenant.ApiKey).ListOrganizationsAsync(tenant.UserId));

        var invalid = await Assert.ThrowsAsync<TerraBaseValidationException>(
            () => server.Management().CreateOrganizationAsync(tenant.UserId, "  "));
        await Assert.That(invalid!.ErrorCode).IsEqualTo("invalid_request");
    }
}
