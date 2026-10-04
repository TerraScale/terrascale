using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Bogus;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Fast.Management;

public sealed class AccessApiTests
{
    private const string Organizations = "/v1/management/organizations";
    private const string Accept = "/v1/management/invitations/accept";
    private const string UserHeader = "X-TerraBase-User-Id";
    private const string OrganizationHeader = "X-TerraBase-Organization-Id";
    private static readonly Faker Faker = new();

    private static readonly string[] DocumentedPaths =
    [
        $"{Organizations}/{{organization}}/members",
        $"{Organizations}/{{organization}}/members/{{user}}/role",
        Accept,
        $"{Organizations}/{{organization}}/ownership-transfer/confirm",
        $"{Organizations}/{{organization}}/audit-retention",
        $"{Organizations}/{{organization}}/audit-events",
    ];

    [Test]
    public async Task The_creator_owns_the_organization_and_promotes_invited_members()
    {
        await using var api = await AccessApi.StartAsync();
        var ownerId = User();
        var org = Org(await api.CreateOrganizationAsync(ownerId));
        using var owner = api.As(ownerId);
        var promotedId = await api.AddMemberAsync(owner, org, "member");
        var memberId = await api.AddMemberAsync(owner, org, "member");
        using var member = api.As(memberId);

        using var promoted = await owner.PutAsJsonAsync(Uri($"{org}/members/{promotedId}/role"), new { role = "admin" });
        var promotedMember = await promoted.Content.ReadFromJsonAsync<JsonElement>();
        var page = await member.GetFromJsonAsync<JsonElement>(Uri($"{org}/members"));
        var userIds = Strings(page, "userId").ToList();

        await Assert.That(promoted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(promotedMember.GetProperty("userId").GetString()).IsEqualTo(promotedId);
        await Assert.That(promotedMember.GetProperty("role").GetString()).IsEqualTo("admin");
        await Assert.That(Roles(page)).IsEquivalentTo([$"{ownerId}=owner", $"{promotedId}=admin", $"{memberId}=member"]);
        await Assert.That(userIds).IsOrderedBy(id => id, StringComparer.Ordinal);
        await Assert.That(page.GetProperty("next").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task Invitations_are_created_listed_accepted_and_revoked()
    {
        await using var api = await AccessApi.StartAsync();
        var ownerId = User();
        var org = Org(await api.CreateOrganizationAsync(ownerId));
        using var owner = api.As(ownerId);
        var inviteeId = User();
        using var invitee = api.As(inviteeId);
        using var stranger = api.As(User());
        var invitations = Uri($"{org}/invitations");
        var accept = Uri(Accept);
        var email = Email();
        var revokedEmail = Email();
        var laterEmail = Email();

        using var created = await owner.PostAsJsonAsync(invitations, new { email, role = "admin" });
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var invitation = body.GetProperty("invitation");
        var id = invitation.GetProperty("id").GetString()!;
        var token = body.GetProperty("token").GetString()!;
        using var duplicate = await owner.PostAsJsonAsync(invitations, new { email = email.ToUpperInvariant(), role = "member" });
        using var defaultRole = await owner.PostAsJsonAsync(invitations, new { email = revokedEmail });
        var revoked = await defaultRole.Content.ReadFromJsonAsync<JsonElement>();
        var revokedId = revoked.GetProperty("invitation").GetProperty("id").GetString()!;
        var pendingIds = Strings(await owner.GetFromJsonAsync<JsonElement>(invitations), "id").ToList();
        using var wrongEmail = await invitee.PostAsJsonAsync(accept, new { token, email = Email() });
        using var forged = await invitee.PostAsJsonAsync(accept, new { token = token[..^8] + Faker.Random.AlphaNumeric(8), email });
        using var accepted = await invitee.PostAsJsonAsync(accept, new { token, email = email.ToUpperInvariant() });
        var member = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        using var acceptedAgain = await invitee.PostAsJsonAsync(accept, new { token, email });
        var afterAccept = await owner.GetFromJsonAsync<JsonElement>(invitations);
        using var revoke = await owner.DeleteAsync(Uri($"{org}/invitations/{revokedId}"));
        using var revokeUnknown = await owner.DeleteAsync(Uri($"{org}/invitations/{Guid.NewGuid()}"));
        using var acceptRevoked = await stranger.PostAsJsonAsync(accept, new { token = revoked.GetProperty("token").GetString(), email = revokedEmail });
        var laterToken = await InviteAsync(owner, org, laterEmail, "member");
        using var alreadyMember = await invitee.PostAsJsonAsync(accept, new { token = laterToken, email = laterEmail });

        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created.Headers.Location!.ToString()).IsEqualTo($"{org}/invitations/{id}");
        await Assert.That(invitation.GetProperty("email").GetString()).IsEqualTo(email);
        await Assert.That(invitation.GetProperty("role").GetString()).IsEqualTo("admin");
        await Assert.That(invitation.GetProperty("invitedBy").GetString()).IsEqualTo(ownerId);
        await Assert.That(invitation.GetProperty("expiresAt").GetDateTimeOffset() - invitation.GetProperty("createdAt").GetDateTimeOffset())
            .IsEqualTo(TimeSpan.FromDays(7));
        await Assert.That(duplicate.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(defaultRole.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(revoked.GetProperty("invitation").GetProperty("role").GetString()).IsEqualTo("member");
        await Assert.That(pendingIds).IsEquivalentTo([id, revokedId]);
        await Assert.That(pendingIds[0]).IsEqualTo(id);
        await Assert.That(wrongEmail.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(forged.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(accepted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(member.GetProperty("userId").GetString()).IsEqualTo(inviteeId);
        await Assert.That(member.GetProperty("role").GetString()).IsEqualTo("admin");
        // An accepted invitation is gone: its token no longer names anything.
        await Assert.That(acceptedAgain.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(Strings(afterAccept, "id")).IsEquivalentTo([revokedId]);
        await Assert.That(revoke.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(revokeUnknown.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(acceptRevoked.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(alreadyMember.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task Roles_limit_who_may_manage_access()
    {
        await using var api = await AccessApi.StartAsync();
        var ownerId = User();
        var org = Org(await api.CreateOrganizationAsync(ownerId));
        using var owner = api.As(ownerId);
        var coOwnerId = await api.AddMemberAsync(owner, org, "owner");
        var adminId = await api.AddMemberAsync(owner, org, "admin");
        var memberId = await api.AddMemberAsync(owner, org, "member");
        var removableId = await api.AddMemberAsync(owner, org, "member");
        using var admin = api.As(adminId);
        using var member = api.As(memberId);
        using var stranger = api.As(User());

        List<HttpStatusCode> memberDenied =
        [
            await StatusAsync(member.GetAsync(Uri($"{org}/invitations"))),
            await StatusAsync(member.GetAsync(Uri($"{org}/audit-events"))),
            await StatusAsync(member.GetAsync(Uri($"{org}/audit-retention"))),
            await StatusAsync(member.PutAsJsonAsync(Uri($"{org}/audit-retention"), new { days = 30 })),
            await StatusAsync(member.PostAsJsonAsync(Uri($"{org}/invitations"), new { email = Email() })),
            await StatusAsync(member.PutAsJsonAsync(Uri($"{org}/members/{removableId}/role"), new { role = "admin" })),
            await StatusAsync(member.DeleteAsync(Uri($"{org}/members/{removableId}"))),
            await StatusAsync(member.PostAsJsonAsync(Uri($"{org}/ownership-transfer"), new { userId = removableId })),
            await StatusAsync(member.PatchAsJsonAsync(Uri(org), new { name = Faker.Company.CompanyName() })),
        ];
        List<HttpStatusCode> adminDenied =
        [
            await StatusAsync(admin.PostAsJsonAsync(Uri($"{org}/invitations"), new { email = Email(), role = "owner" })),
            await StatusAsync(admin.PutAsJsonAsync(Uri($"{org}/members/{memberId}/role"), new { role = "owner" })),
            await StatusAsync(admin.PutAsJsonAsync(Uri($"{org}/members/{coOwnerId}/role"), new { role = "admin" })),
            await StatusAsync(admin.DeleteAsync(Uri($"{org}/members/{coOwnerId}"))),
            await StatusAsync(admin.PostAsJsonAsync(Uri($"{org}/ownership-transfer"), new { userId = memberId })),
            await StatusAsync(admin.DeleteAsync(Uri(org))),
        ];
        using var adminInvitesMember = await admin.PostAsJsonAsync(Uri($"{org}/invitations"), new { email = Email(), role = "member" });
        using var adminInvitesAdmin = await admin.PostAsJsonAsync(Uri($"{org}/invitations"), new { email = Email(), role = "admin" });
        using var adminRemovesMember = await admin.DeleteAsync(Uri($"{org}/members/{removableId}"));
        using var notMember = await stranger.GetAsync(Uri($"{org}/members"));
        using var unknownOrganization = await owner.GetAsync(Uri($"{Org(Guid.NewGuid().ToString())}/members"));
        var page = await owner.GetFromJsonAsync<JsonElement>(Uri($"{org}/members"));

        await Assert.That(memberDenied).All().Satisfy(status => status.IsEqualTo(HttpStatusCode.Forbidden));
        await Assert.That(adminDenied).All().Satisfy(status => status.IsEqualTo(HttpStatusCode.Forbidden));
        await Assert.That(adminInvitesMember.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(adminInvitesAdmin.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(adminRemovesMember.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        // 404, not 403: a non-member cannot even confirm that the organization exists.
        await Assert.That(notMember.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(unknownOrganization.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        // The denied requests changed nothing.
        await Assert.That(Roles(page)).IsEquivalentTo([$"{ownerId}=owner", $"{coOwnerId}=owner", $"{adminId}=admin", $"{memberId}=member"]);
    }

    [Test]
    public async Task An_organization_always_keeps_an_owner()
    {
        await using var api = await AccessApi.StartAsync();
        var firstId = User();
        var org = Org(await api.CreateOrganizationAsync(firstId));
        using var first = api.As(firstId);
        var secondId = await api.AddMemberAsync(first, org, "admin");
        var memberId = await api.AddMemberAsync(first, org, "member");
        using var second = api.As(secondId);
        using var member = api.As(memberId);

        using var demoteSole = await first.PutAsJsonAsync(Uri($"{org}/members/{firstId}/role"), new { role = "admin" });
        using var leaveSole = await first.PostAsync(Uri($"{org}/leave"), null);
        using var removeSole = await first.DeleteAsync(Uri($"{org}/members/{firstId}"));
        using var memberLeaves = await member.PostAsync(Uri($"{org}/leave"), null);
        using var promoted = await first.PutAsJsonAsync(Uri($"{org}/members/{secondId}/role"), new { role = "owner" });
        using var firstLeaves = await first.PostAsync(Uri($"{org}/leave"), null);
        using var afterLeaving = await first.GetAsync(Uri($"{org}/members"));
        using var leaveSoleAgain = await second.PostAsync(Uri($"{org}/leave"), null);
        var page = await second.GetFromJsonAsync<JsonElement>(Uri($"{org}/members"));

        await Assert.That(demoteSole.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(leaveSole.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(removeSole.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(memberLeaves.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(promoted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(firstLeaves.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(afterLeaving.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(leaveSoleAgain.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(Roles(page)).IsEquivalentTo([$"{secondId}=owner"]);
    }

    [Test]
    public async Task Ownership_moves_only_when_the_nominee_confirms()
    {
        await using var api = await AccessApi.StartAsync();
        var ownerId = User();
        var org = Org(await api.CreateOrganizationAsync(ownerId));
        using var owner = api.As(ownerId);
        var coOwnerId = await api.AddMemberAsync(owner, org, "owner");
        var nomineeId = await api.AddMemberAsync(owner, org, "member");
        var adminId = await api.AddMemberAsync(owner, org, "admin");
        using var nominee = api.As(nomineeId);
        using var admin = api.As(adminId);
        var transfer = Uri($"{org}/ownership-transfer");
        var confirm = Uri($"{org}/ownership-transfer/confirm");

        using var none = await nominee.GetAsync(transfer);
        using var toSelf = await owner.PostAsJsonAsync(transfer, new { userId = ownerId });
        using var toOwner = await owner.PostAsJsonAsync(transfer, new { userId = coOwnerId });
        using var toStranger = await owner.PostAsJsonAsync(transfer, new { userId = User() });
        using var byAdmin = await admin.PostAsJsonAsync(transfer, new { userId = nomineeId });
        using var started = await owner.PostAsJsonAsync(transfer, new { userId = nomineeId });
        var offer = await started.Content.ReadFromJsonAsync<JsonElement>();
        var pending = await nominee.GetFromJsonAsync<JsonElement>(transfer);
        using var confirmedByAdmin = await admin.PostAsync(confirm, null);
        using var cancelledByAdmin = await admin.DeleteAsync(transfer);
        using var confirmed = await nominee.PostAsync(confirm, null);
        var newOwner = await confirmed.Content.ReadFromJsonAsync<JsonElement>();
        using var afterConfirm = await nominee.GetAsync(transfer);
        var members = await nominee.GetFromJsonAsync<JsonElement>(Uri($"{org}/members"));
        using var byFormerOwner = await owner.PostAsJsonAsync(transfer, new { userId = adminId });
        using var offered = await nominee.PostAsJsonAsync(transfer, new { userId = ownerId });
        using var replaced = await nominee.PostAsJsonAsync(transfer, new { userId = adminId });
        var replacement = await owner.GetFromJsonAsync<JsonElement>(transfer);
        using var cancelledByNominee = await admin.DeleteAsync(transfer);
        using var cancelledAgain = await admin.DeleteAsync(transfer);
        using var confirmedNone = await admin.PostAsync(confirm, null);

        await Assert.That(none.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(toSelf.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(toOwner.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(toStranger.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(byAdmin.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(started.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(started.Headers.Location!.ToString()).IsEqualTo($"{org}/ownership-transfer");
        await Assert.That(offer.GetProperty("fromUserId").GetString()).IsEqualTo(ownerId);
        await Assert.That(offer.GetProperty("toUserId").GetString()).IsEqualTo(nomineeId);
        await Assert.That(offer.GetProperty("expiresAt").GetDateTimeOffset()).IsGreaterThan(offer.GetProperty("createdAt").GetDateTimeOffset());
        await Assert.That(pending.GetProperty("toUserId").GetString()).IsEqualTo(nomineeId);
        await Assert.That(confirmedByAdmin.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(cancelledByAdmin.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(confirmed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(newOwner.GetProperty("userId").GetString()).IsEqualTo(nomineeId);
        await Assert.That(newOwner.GetProperty("role").GetString()).IsEqualTo("owner");
        await Assert.That(afterConfirm.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        // The owner who started the transfer stepped down; the other owner is untouched.
        await Assert.That(Roles(members)).IsEquivalentTo([$"{ownerId}=member", $"{coOwnerId}=owner", $"{nomineeId}=owner", $"{adminId}=admin"]);
        await Assert.That(byFormerOwner.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(offered.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(replaced.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(replacement.GetProperty("fromUserId").GetString()).IsEqualTo(nomineeId);
        await Assert.That(replacement.GetProperty("toUserId").GetString()).IsEqualTo(adminId);
        await Assert.That(cancelledByNominee.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(cancelledAgain.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(confirmedNone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task A_transfer_lapses_when_its_starter_stops_being_an_owner_or_either_side_leaves()
    {
        await using var api = await AccessApi.StartAsync();
        var starterId = User();
        var org = Org(await api.CreateOrganizationAsync(starterId));
        using var starter = api.As(starterId);
        var coOwnerId = await api.AddMemberAsync(starter, org, "owner");
        var nomineeId = await api.AddMemberAsync(starter, org, "member");
        using var coOwner = api.As(coOwnerId);
        using var nominee = api.As(nomineeId);
        var transfer = Uri($"{org}/ownership-transfer");
        var starterRole = Uri($"{org}/members/{starterId}/role");

        // Demoted and promoted again: the old offer must not come back.
        (await starter.PostAsJsonAsync(transfer, new { userId = nomineeId })).EnsureSuccessStatusCode();
        (await coOwner.PutAsJsonAsync(starterRole, new { role = "admin" })).EnsureSuccessStatusCode();
        (await coOwner.PutAsJsonAsync(starterRole, new { role = "owner" })).EnsureSuccessStatusCode();
        using var afterDemotion = await nominee.GetAsync(transfer);
        using var confirmedAfterDemotion = await nominee.PostAsync(Uri($"{org}/ownership-transfer/confirm"), null);
        (await starter.PostAsJsonAsync(transfer, new { userId = nomineeId })).EnsureSuccessStatusCode();
        (await coOwner.DeleteAsync(Uri($"{org}/members/{starterId}"))).EnsureSuccessStatusCode();
        using var afterStarterRemoved = await nominee.GetAsync(transfer);
        (await coOwner.PostAsJsonAsync(transfer, new { userId = nomineeId })).EnsureSuccessStatusCode();
        (await nominee.PostAsync(Uri($"{org}/leave"), null)).EnsureSuccessStatusCode();
        using var afterNomineeLeft = await coOwner.GetAsync(transfer);
        var members = await coOwner.GetFromJsonAsync<JsonElement>(Uri($"{org}/members"));

        await Assert.That(afterDemotion.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(confirmedAfterDemotion.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(afterStarterRemoved.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(afterNomineeLeft.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(Roles(members)).IsEquivalentTo([$"{coOwnerId}=owner"]);
    }

    [Test]
    public async Task Access_changes_are_audited_newest_first()
    {
        await using var api = await AccessApi.StartAsync();
        var ownerId = User();
        var adminId = User();
        var memberId = User();
        var adminEmail = Email();
        var memberEmail = Email();
        var revokedEmail = Email();
        var org = Org(await api.CreateOrganizationAsync(ownerId));
        using var owner = api.As(ownerId);
        using var admin = api.As(adminId);
        using var member = api.As(memberId);
        var retention = Uri($"{org}/audit-retention");
        var transfer = Uri($"{org}/ownership-transfer");

        await api.JoinAsync(owner, org, adminId, adminEmail, "admin");
        await api.JoinAsync(admin, org, memberId, memberEmail, "member");
        using var invited = await admin.PostAsJsonAsync(Uri($"{org}/invitations"), new { email = revokedEmail });
        var invitationId = (await invited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("invitation").GetProperty("id").GetString();
        (await admin.DeleteAsync(Uri($"{org}/invitations/{invitationId}"))).EnsureSuccessStatusCode();
        var defaultRetention = await admin.GetFromJsonAsync<JsonElement>(retention);
        using var memberReadsRetention = await member.GetAsync(retention);
        using var updated = await admin.PutAsJsonAsync(retention, new { days = 30 });
        var updatedRetention = await updated.Content.ReadFromJsonAsync<JsonElement>();
        List<HttpStatusCode> invalid =
        [
            await StatusAsync(admin.PutAsJsonAsync(retention, new { days = 0 })),
            await StatusAsync(admin.PutAsJsonAsync(retention, new { days = 91 })),
            await StatusAsync(admin.PutAsJsonAsync(retention, new { })),
        ];
        var readBack = await admin.GetFromJsonAsync<JsonElement>(retention);
        (await owner.PutAsJsonAsync(Uri($"{org}/members/{memberId}/role"), new { role = "admin" })).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync(transfer, new { userId = memberId })).EnsureSuccessStatusCode();
        (await owner.DeleteAsync(transfer)).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync(transfer, new { userId = memberId })).EnsureSuccessStatusCode();
        (await member.PostAsync(Uri($"{org}/ownership-transfer/confirm"), null)).EnsureSuccessStatusCode();
        (await member.DeleteAsync(Uri($"{org}/members/{adminId}"))).EnsureSuccessStatusCode();
        (await owner.PostAsync(Uri($"{org}/leave"), null)).EnsureSuccessStatusCode();
        var events = await member.GetFromJsonAsync<JsonElement>(Uri($"{org}/audit-events"));
        var first = await member.GetFromJsonAsync<JsonElement>(Uri($"{org}/audit-events?limit=1"));
        var next = first.GetProperty("next").GetString();
        var second = await member.GetFromJsonAsync<JsonElement>(Uri($"{org}/audit-events?limit=1&after={System.Uri.EscapeDataString(next!)}"));

        string[] expected =
        [
            $"{ownerId}|member.left||",
            $"{memberId}|member.removed|{adminId}|",
            $"{memberId}|ownership.transfer_confirmed|{ownerId}|",
            $"{ownerId}|ownership.transfer_started|{memberId}|",
            $"{ownerId}|ownership.transfer_cancelled|{memberId}|",
            $"{ownerId}|ownership.transfer_started|{memberId}|",
            $"{ownerId}|member.role_changed|{memberId}|admin",
            $"{adminId}|settings.audit_retention_updated||30",
            $"{adminId}|invitation.revoked|{revokedEmail}|",
            $"{adminId}|invitation.created|{revokedEmail}|member",
            $"{memberId}|invitation.accepted|{memberEmail}|member",
            $"{adminId}|invitation.created|{memberEmail}|member",
            $"{adminId}|invitation.accepted|{adminEmail}|admin",
            $"{ownerId}|invitation.created|{adminEmail}|admin",
        ];
        await Assert.That(defaultRetention.GetProperty("days").GetInt32()).IsEqualTo(90);
        await Assert.That(memberReadsRetention.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(updated.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(updatedRetention.GetProperty("days").GetInt32()).IsEqualTo(30);
        await Assert.That(invalid).All().Satisfy(status => status.IsEqualTo(HttpStatusCode.BadRequest));
        await Assert.That(readBack.GetProperty("days").GetInt32()).IsEqualTo(30);
        // Compared as one string so that both the content and the newest-first order are checked.
        await Assert.That(string.Join('\n', Events(events))).IsEqualTo(string.Join('\n', expected));
        await Assert.That(next).IsNotNull();
        // next is an opaque cursor: the second page starts at the second-newest event.
        await Assert.That(Events(first)).IsEquivalentTo([expected[0]]);
        await Assert.That(Events(second)).IsEquivalentTo([expected[1]]);
    }

    [Test]
    public async Task Access_needs_the_dashboard_credential_and_both_headers_on_the_client_listener()
    {
        await using var api = await AccessApi.StartAsync();
        var ownerId = User();
        var org = Org(await api.CreateOrganizationAsync(ownerId));
        var members = Uri($"{org}/members");
        using var owner = api.As(ownerId);
        using var noCredential = api.Connect(null, ownerId);
        using var wrongCredential = api.Connect(AccessApi.Credential + "x", ownerId);
        using var documentsKey = api.Connect(await api.Server.IssueAsync("documents:read"), ownerId);
        using var noUser = api.Connect(AccessApi.Credential, null);
        using var noOrganization = api.Connect(AccessApi.Credential, ownerId, headers => headers.Remove(OrganizationHeader));
        using var otherOrganization = api.Connect(AccessApi.Credential, ownerId, headers =>
        {
            headers.Remove(OrganizationHeader);
            headers.Add(OrganizationHeader, Guid.NewGuid().ToString());
        });
        using var adminRequest = new HttpRequestMessage(HttpMethod.Get, members);
        adminRequest.Headers.Authorization = new("Bearer", AccessApi.Credential);
        adminRequest.Headers.Add(UserHeader, ownerId);
        adminRequest.Headers.Add(OrganizationHeader, org[(Organizations.Length + 1)..]);

        using var allowed = await owner.GetAsync(members);
        using var anonymous = await noCredential.GetAsync(members);
        using var anonymousAccept = await noCredential.PostAsJsonAsync(Uri(Accept), new { token = "token", email = Email() });
        using var badCredential = await wrongCredential.GetAsync(members);
        using var apiKey = await documentsKey.GetAsync(members);
        using var missingUser = await noUser.GetAsync(members);
        using var missingOrganization = await noOrganization.GetAsync(members);
        using var mismatchedOrganization = await otherOrganization.GetAsync(members);
        using var onAdmin = await api.Server.Client.SendAsync(adminRequest);

        await Assert.That(allowed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(anonymous.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(anonymousAccept.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(badCredential.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(apiKey.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(missingUser.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(missingOrganization.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(mismatchedOrganization.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(onAdmin.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Access_is_closed_without_a_dashboard_credential()
    {
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var client = new HttpClient(new DashboardHeaders(User()) { InnerHandler = tls.CreateHandler() }) { BaseAddress = server.ClientAddress };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AccessApi.Credential);

        using var members = await client.GetAsync(Uri($"{Org(Guid.NewGuid().ToString())}/members"));
        using var accept = await client.PostAsJsonAsync(Uri(Accept), new { token = "token", email = Email() });

        await Assert.That(members.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(accept.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Invalid_roles_emails_and_acceptances_are_rejected()
    {
        await using var api = await AccessApi.StartAsync();
        var ownerId = User();
        var org = Org(await api.CreateOrganizationAsync(ownerId));
        using var owner = api.As(ownerId);
        var memberId = await api.AddMemberAsync(owner, org, "member");
        using var invitee = api.As(User());
        var role = Uri($"{org}/members/{memberId}/role");
        var invitations = Uri($"{org}/invitations");
        var accept = Uri(Accept);
        var email = Email();

        List<HttpStatusCode> statuses =
        [
            await StatusAsync(owner.PutAsJsonAsync(role, new { role = "superuser" })),
            await StatusAsync(owner.PutAsJsonAsync(role, new { role = 7 })),
            await StatusAsync(owner.PutAsJsonAsync(role, new { })),
            await StatusAsync(owner.PostAsJsonAsync(invitations, new { email, role = "superuser" })),
            await StatusAsync(owner.PostAsJsonAsync(invitations, new { email, role = 7 })),
            await StatusAsync(owner.PostAsJsonAsync(invitations, new { email = "not-an-email" })),
            await StatusAsync(owner.PostAsJsonAsync(invitations, new { email = "Bob <b@x.com>" })),
            await StatusAsync(owner.PostAsJsonAsync(invitations, new { })),
            await StatusAsync(invitee.PostAsJsonAsync(accept, new { })),
            await StatusAsync(invitee.PostAsJsonAsync(accept, new { email })),
            await StatusAsync(invitee.PostAsJsonAsync(accept, new { token = "token" })),
        ];
        var members = await owner.GetFromJsonAsync<JsonElement>(Uri($"{org}/members"));
        var pending = await owner.GetFromJsonAsync<JsonElement>(invitations);

        await Assert.That(statuses).All().Satisfy(status => status.IsEqualTo(HttpStatusCode.BadRequest));
        // A rejected request changes nothing.
        await Assert.That(Roles(members)).IsEquivalentTo([$"{ownerId}=owner", $"{memberId}=member"]);
        await Assert.That(pending.GetProperty("items").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task The_access_endpoints_are_documented_in_openapi()
    {
        await using var api = await AccessApi.StartAsync();

        using var response = await api.Server.Client.GetAsync(Uri("/openapi/v1.json"));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        var missing = DocumentedPaths.Where(path => !paths.TryGetProperty(path, out _)).ToList();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(missing).IsEmpty();
    }

    private static Uri Uri(string path) => new(path, UriKind.Relative);

    // The path of one organization; access routes hang below it.
    private static string Org(string organization) => $"{Organizations}/{organization}";

    private static string User() => Faker.Random.Guid().ToString();

    // The suffix keeps two addresses of one test from colliding by chance.
    private static string Email() => Faker.Internet.Email(uniqueSuffix: Faker.Random.AlphaNumeric(6));

    private static async Task<HttpStatusCode> StatusAsync(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        return response.StatusCode;
    }

    /// <summary>Invites <paramref name="email"/> as <paramref name="role"/> to <paramref name="org"/> and returns the acceptance token.</summary>
    private static async Task<string> InviteAsync(HttpClient inviter, string org, string email, string role)
    {
        using var response = await inviter.PostAsJsonAsync(Uri($"{org}/invitations"), new { email, role });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    private static IEnumerable<string> Strings(JsonElement page, string property) =>
        page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty(property).GetString()!);

    // "userId=role" of every member on a page.
    private static IEnumerable<string> Roles(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(member => $"{member.GetProperty("userId").GetString()}={member.GetProperty("role").GetString()}");

    // "actor|action|target|detail" of every audit event on a page; a missing or null value is empty.
    private static IEnumerable<string> Events(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(item =>
            string.Join('|', Text(item, "actor"), Text(item, "action"), Text(item, "target"), Text(item, "detail")));

    private static string? Text(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) ? value.GetString() : null;

    /// <summary>
    /// A server with the client listener and the dashboard credential, and clients that send it like
    /// ts-dashboard: the acting user on every request and, on organization routes, the organization from the path.
    /// </summary>
    private sealed class AccessApi : IAsyncDisposable
    {
        public const string Credential = "test-dashboard-credential-0123456789abcdef";

        private readonly ClientTls _tls;
        private readonly string _directory;

        private AccessApi(ClientTls tls, TerraBaseTestServer server, string directory)
        {
            _tls = tls;
            _directory = directory;
            Server = server;
        }

        public TerraBaseTestServer Server { get; }

        public static async Task<AccessApi> StartAsync()
        {
            var directory = Directory.CreateTempSubdirectory("terrabase-dashboard-").FullName;
            var credentialFile = Path.Combine(directory, "credential");
            await File.WriteAllTextAsync(credentialFile, Credential + "\n");
            var tls = new ClientTls();
            var server = await TerraBaseTestServer.StartAsync(settings =>
            {
                tls.Apply(settings);
                settings["dashboard-credential-file"] = credentialFile;
            });
            return new AccessApi(tls, server, directory);
        }

        /// <summary>Creates an organization whose first owner is <paramref name="owner"/>; returns its id.</summary>
        public async Task<string> CreateOrganizationAsync(string owner)
        {
            using var client = As(owner);
            using var response = await client.PostAsJsonAsync(Uri(Organizations), new { name = Faker.Company.CompanyName() });
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        }

        /// <summary>
        /// A client-listener client that sends <paramref name="bearer"/> and, when set, <paramref name="user"/> and
        /// the organization from the path; <paramref name="edit"/> then changes the final headers.
        /// </summary>
        public HttpClient Connect(string? bearer, string? user, Action<HttpRequestHeaders>? edit = null)
        {
            var client = new HttpClient(new DashboardHeaders(user, edit) { InnerHandler = _tls.CreateHandler() })
            {
                BaseAddress = Server.ClientAddress,
                Timeout = TimeSpan.FromSeconds(30),
            };
            if (bearer is not null)
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            }

            return client;
        }

        /// <summary>A dashboard client acting as <paramref name="user"/>.</summary>
        public HttpClient As(string user) => Connect(Credential, user);

        /// <summary>Has <paramref name="user"/> join <paramref name="org"/> through an invitation for <paramref name="email"/> from <paramref name="inviter"/>.</summary>
        public async Task JoinAsync(HttpClient inviter, string org, string user, string email, string role)
        {
            var token = await InviteAsync(inviter, org, email, role);
            using var client = As(user);
            using var response = await client.PostAsJsonAsync(Uri(Accept), new { token, email });
            response.EnsureSuccessStatusCode();
        }

        /// <summary>Has a new user join <paramref name="org"/> as <paramref name="role"/> through an invitation from <paramref name="inviter"/>; returns its id.</summary>
        public async Task<string> AddMemberAsync(HttpClient inviter, string org, string role)
        {
            var user = User();
            await JoinAsync(inviter, org, user, Email(), role);
            return user;
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            _tls.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class DashboardHeaders(string? user, Action<HttpRequestHeaders>? edit = null) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (user is not null)
            {
                request.Headers.Add(UserHeader, user);
            }

            var segments = request.RequestUri!.AbsolutePath.Split('/');
            if (segments.Length > 4 && segments[3] == "organizations")
            {
                request.Headers.Add(OrganizationHeader, segments[4]);
            }

            edit?.Invoke(request.Headers);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
