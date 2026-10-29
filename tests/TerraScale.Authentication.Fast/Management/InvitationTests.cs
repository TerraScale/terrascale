using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Store;
using TerraScale.DotNet.TerraBase;
using TerraScale.Authentication.Tests;
using static TerraScale.Authentication.Tests.PlatformCalls;

namespace TerraScale.Authentication.Fast.Management;

/// <summary>Invitations, ownership transfer and audit events of the platform (TS-598), over real HTTP on the real TerraBase.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed partial class InvitationTests(TerraBaseFixture fixture)
{
    // The dashboard's accept page, on the origin of the harness's first dashboard redirect URI.
    private static readonly string AcceptPage = $"{new Uri(OidcHarness.RedirectUri).GetLeftPart(UriPartial.Authority)}/invitations/";

    [Test]
    public async Task An_invitation_is_emailed_works_once_for_its_invitee_and_is_audited()
    {
        await using var oidc = await StartAsync();
        var (alice, aliceToken) = await oidc.SignInUserAsync("alice@example.test");
        var (bob, bobToken) = await oidc.SignInUserAsync("bob@example.test");
        var (_, carolToken) = await oidc.SignInUserAsync("carol@example.test");
        var id = await oidc.CreateOrganizationAsync(aliceToken);
        var invitations = $"{Organizations}/{id}/invitations";

        var created = await oidc.OkAsync(HttpMethod.Post, invitations, aliceToken, new { email = " Bob@Example.TEST ", role = "member" }, HttpStatusCode.Created);
        var token = created.GetProperty("token").GetString()!;
        await Assert.That(Token().IsMatch(token)).IsTrue();
        var invitation = created.GetProperty("invitation");
        await Assert.That(invitation.GetProperty("email").GetString()).IsEqualTo("bob@example.test");
        await Assert.That(invitation.GetProperty("status").GetString()).IsEqualTo("pending");
        await Assert.That(invitation.GetProperty("invited_by").GetString()).IsEqualTo(alice.Id);

        // The invitee is emailed the dashboard's accept link with the token.
        var (recipient, mail) = await NextInvitationAsync(oidc);
        await Assert.That(recipient).IsEqualTo("bob@example.test");
        await Assert.That(mail.Text).Contains($"{AcceptPage}{token}");
        await Assert.That(mail.Text).Contains("Acme");

        var lookup = await oidc.OkAsync(HttpMethod.Post, "/v1/terrascale/invitations/lookup", carolToken, new { token });
        await Assert.That(lookup.GetProperty("organization").GetProperty("name").GetString()).IsEqualTo("Acme");
        await Assert.That(lookup.GetProperty("project").ValueKind).IsEqualTo(JsonValueKind.Null);

        // Only the invitee accepts, and only once.
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, "/v1/terrascale/invitations/accept", carolToken, new { token })).IsEqualTo((403, "email_mismatch"));
        var accepted = await oidc.OkAsync(HttpMethod.Post, "/v1/terrascale/invitations/accept", bobToken, new { token });
        await Assert.That(accepted.GetProperty("organization_id").GetString()).IsEqualTo(id);
        await Assert.That(accepted.GetProperty("role").GetString()).IsEqualTo("member");
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{id}", bobToken)).GetProperty("role").GetString()).IsEqualTo("member");
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, "/v1/terrascale/invitations/accept", bobToken, new { token })).IsEqualTo((409, "not_pending"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, "/v1/terrascale/invitations/lookup", bobToken, new { token })).IsEqualTo((404, "not_found"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, "/v1/terrascale/invitations/accept", bobToken, new { token = "tsi_" + new string('0', 52) })).IsEqualTo((404, "not_found"));

        // Who may invite, and what cannot be invited twice.
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, invitations, aliceToken, new { email = "bob@example.test", role = "admin" })).IsEqualTo((409, "already_member"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, invitations, bobToken, new { email = "dave@example.test", role = "viewer" })).IsEqualTo((403, "forbidden_role"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, invitations, aliceToken, new { email = "not-an-address", role = "viewer" })).IsEqualTo((400, "invalid_request"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, invitations, aliceToken, new { email = "dave@example.test", role = "superuser" })).IsEqualTo((400, "invalid_request"));
        var dave = (await oidc.OkAsync(HttpMethod.Post, invitations, aliceToken, new { email = "dave@example.test", role = "viewer" }, HttpStatusCode.Created)).GetProperty("invitation").GetProperty("id").GetString()!;
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, invitations, aliceToken, new { email = "DAVE@example.test", role = "admin" })).IsEqualTo((409, "invitation_pending"));

        // Revoked, it stops working and the address can be invited again.
        await oidc.OkAsync(HttpMethod.Delete, $"{invitations}/{dave}", aliceToken, status: HttpStatusCode.NoContent);
        await Assert.That(await oidc.FailAsync(HttpMethod.Delete, $"{invitations}/{dave}", aliceToken)).IsEqualTo((409, "not_pending"));
        await oidc.OkAsync(HttpMethod.Post, invitations, aliceToken, new { email = "dave@example.test", role = "viewer" }, HttpStatusCode.Created);
        var statuses = (await oidc.OkAsync(HttpMethod.Get, invitations, aliceToken)).GetProperty("items").EnumerateArray().Select(item => item.GetProperty("status").GetString()).ToArray();
        await Assert.That(string.Join(',', statuses)).IsEqualTo("accepted,revoked,pending");
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, invitations, bobToken)).IsEqualTo((403, "forbidden_role"));

        // Every change is audited, newest first, for admins only.
        var events = (await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{id}/audit-events", aliceToken)).GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("action").GetString()!).ToArray();
        await Assert.That(events).IsEquivalentTo(["invitation.created", "invitation.revoked", "invitation.created", "invitation.accepted", "member.joined", "invitation.created", "organization.created"]);
        await Assert.That(events[0]).IsEqualTo("invitation.created");
        await Assert.That(events[^1]).IsEqualTo("organization.created");
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{Organizations}/{id}/audit-events", bobToken)).IsEqualTo((403, "forbidden_role"));
    }

    [Test]
    public async Task A_project_invitation_makes_a_collaborator_and_project_admins_manage_their_own()
    {
        await using var oidc = await StartAsync();
        var (_, aliceToken) = await oidc.SignInUserAsync("alice@example.test");
        var (bob, bobToken) = await oidc.SignInUserAsync("bob@example.test");
        var (_, carolToken) = await oidc.SignInUserAsync("carol@example.test");
        var id = await oidc.CreateOrganizationAsync(aliceToken);
        var production = await oidc.CreateProjectAsync(aliceToken, id, "Production");
        var staging = await oidc.CreateProjectAsync(aliceToken, id, "Staging");
        await oidc.AddMemberAsync(id, bob.Id, "viewer");
        await oidc.OkAsync(HttpMethod.Put, $"{Organizations}/{id}/projects/{production}/members/{bob.Id}", aliceToken, new { role = "admin" });
        var invitations = $"{Organizations}/{id}/invitations";

        // A project admin invites to its project only, and lists only its project's invitations.
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, invitations, bobToken, new { email = "carol@example.test", role = "member", project_id = staging })).IsEqualTo((403, "forbidden_role"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, invitations, bobToken, new { email = "carol@example.test", role = "owner", project_id = production })).IsEqualTo((400, "invalid_request"));
        var created = await oidc.OkAsync(HttpMethod.Post, invitations, bobToken, new { email = "carol@example.test", role = "member", project_id = production }, HttpStatusCode.Created);
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, invitations, bobToken)).IsEqualTo((403, "forbidden_role"));
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, $"{invitations}?project_id={production}", bobToken)).GetProperty("items").GetArrayLength()).IsEqualTo(1);

        var token = created.GetProperty("token").GetString()!;
        var lookup = await oidc.OkAsync(HttpMethod.Post, "/v1/terrascale/invitations/lookup", carolToken, new { token });
        await Assert.That(lookup.GetProperty("project").GetProperty("name").GetString()).IsEqualTo("Production");
        var accepted = await oidc.OkAsync(HttpMethod.Post, "/v1/terrascale/invitations/accept", carolToken, new { token });
        await Assert.That(accepted.GetProperty("project_id").GetString()).IsEqualTo(production);

        // The invitee joins as a collaborator: the organization without a role, and only that project.
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{id}", carolToken)).GetProperty("role").ValueKind).IsEqualTo(JsonValueKind.Null);
        var projects = (await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{id}/projects", carolToken)).GetProperty("items").EnumerateArray()
            .Select(project => (project.GetProperty("id").GetString(), project.GetProperty("role").GetString())).ToArray();
        await Assert.That(projects).IsEquivalentTo(new (string?, string?)[] { (production, "member") });

        // Declining works the same way.
        var declined = (await oidc.OkAsync(HttpMethod.Post, invitations, aliceToken, new { email = "carol@example.test", role = "viewer", project_id = staging }, HttpStatusCode.Created)).GetProperty("token").GetString()!;
        await oidc.OkAsync(HttpMethod.Post, "/v1/terrascale/invitations/decline", carolToken, new { token = declined }, HttpStatusCode.NoContent);
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{Organizations}/{id}/projects/{staging}", carolToken)).IsEqualTo((404, "not_found"));

        // Deleting a project deletes all its invitations, whatever their status, and a collaborator leaves with its last project.
        var (_, daveToken) = await oidc.SignInUserAsync("dave@example.test");
        var pending = (await oidc.OkAsync(HttpMethod.Post, invitations, aliceToken, new { email = "dave@example.test", role = "viewer", project_id = staging }, HttpStatusCode.Created)).GetProperty("token").GetString()!;
        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{id}/projects/{staging}", aliceToken, status: HttpStatusCode.NoContent);
        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{id}/projects/{production}", aliceToken, status: HttpStatusCode.NoContent);
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, "/v1/terrascale/invitations/lookup", daveToken, new { token = pending })).IsEqualTo((404, "not_found"));
        var statuses = (await oidc.OkAsync(HttpMethod.Get, invitations, aliceToken)).GetProperty("items").EnumerateArray().Select(item => item.GetProperty("status").GetString()).ToArray();
        // Both projects are gone, so no invitation (accepted, declined or pending) keeps an invitee's email.
        await Assert.That(statuses).IsEmpty();
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, $"{Organizations}/{id}", carolToken)).IsEqualTo((404, "not_found"));
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{id}", bobToken)).GetProperty("role").GetString()).IsEqualTo("viewer");

        // Access gained since the invitation was sent is not lowered or duplicated: accepting answers already_member.
        var (erin, erinToken) = await oidc.SignInUserAsync("erin@example.test");
        var demo = await oidc.CreateProjectAsync(aliceToken, id, "Demo");
        var late = (await oidc.OkAsync(HttpMethod.Post, invitations, aliceToken, new { email = "erin@example.test", role = "member", project_id = demo }, HttpStatusCode.Created)).GetProperty("token").GetString()!;
        await oidc.AddMemberAsync(id, erin.Id, "admin");
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, "/v1/terrascale/invitations/accept", erinToken, new { token = late })).IsEqualTo((409, "already_member"));
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{id}/projects/{demo}/members", aliceToken)).GetProperty("items").EnumerateArray()
            .Any(member => member.GetProperty("user_id").GetString() == erin.Id)).IsFalse();
    }

    [Test]
    public async Task Ownership_moves_only_when_the_nominee_confirms_and_lapses_with_its_starter()
    {
        await using var oidc = await StartAsync();
        var (alice, aliceToken) = await oidc.SignInUserAsync("alice@example.test");
        var (bob, bobToken) = await oidc.SignInUserAsync("bob@example.test");
        var (carol, carolToken) = await oidc.SignInUserAsync("carol@example.test");
        var id = await oidc.CreateOrganizationAsync(aliceToken);
        await oidc.AddMemberAsync(id, bob.Id, "admin");
        await oidc.AddMemberAsync(id, carol.Id, "viewer");
        var transfer = $"{Organizations}/{id}/ownership-transfer";

        await Assert.That(await oidc.FailAsync(HttpMethod.Post, transfer, bobToken, new { user_id = carol.Id })).IsEqualTo((403, "forbidden_role"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, transfer, aliceToken, new { user_id = alice.Id })).IsEqualTo((400, "invalid_request"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, transfer, carolToken)).IsEqualTo((404, "not_found"));
        var started = await oidc.OkAsync(HttpMethod.Post, transfer, aliceToken, new { user_id = bob.Id }, HttpStatusCode.Created);
        await Assert.That(started.GetProperty("to_user_id").GetString()).IsEqualTo(bob.Id);
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, transfer, carolToken)).GetProperty("from_user_id").GetString()).IsEqualTo(alice.Id);

        // Only the nominee confirms; then it owns and the starter administers.
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, $"{transfer}/confirm", carolToken)).IsEqualTo((403, "forbidden_role"));
        var confirmed = await oidc.OkAsync(HttpMethod.Post, $"{transfer}/confirm", bobToken);
        await Assert.That(confirmed.GetProperty("role").GetString()).IsEqualTo("owner");
        await Assert.That((await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{id}", aliceToken)).GetProperty("role").GetString()).IsEqualTo("admin");
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, transfer, bobToken)).IsEqualTo((404, "not_found"));

        // An offer lapses once its starter is no longer an owner; the nominee may also cancel one.
        await oidc.OkAsync(HttpMethod.Put, $"{Organizations}/{id}/members/{alice.Id}/role", bobToken, new { role = "owner" });
        await oidc.OkAsync(HttpMethod.Post, transfer, aliceToken, new { user_id = carol.Id }, HttpStatusCode.Created);
        await oidc.OkAsync(HttpMethod.Put, $"{Organizations}/{id}/members/{alice.Id}/role", bobToken, new { role = "admin" });
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, $"{transfer}/confirm", carolToken)).IsEqualTo((404, "not_found"));

        // It stays lapsed when the starter is an owner again, and when the nominee is removed and added back.
        await oidc.OkAsync(HttpMethod.Put, $"{Organizations}/{id}/members/{alice.Id}/role", bobToken, new { role = "owner" });
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, $"{transfer}/confirm", carolToken)).IsEqualTo((404, "not_found"));
        await oidc.OkAsync(HttpMethod.Post, transfer, aliceToken, new { user_id = carol.Id }, HttpStatusCode.Created);
        await oidc.OkAsync(HttpMethod.Delete, $"{Organizations}/{id}/members/{carol.Id}", aliceToken, status: HttpStatusCode.NoContent);
        await oidc.AddMemberAsync(id, carol.Id, "viewer");
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, $"{transfer}/confirm", carolToken)).IsEqualTo((404, "not_found"));
        await oidc.OkAsync(HttpMethod.Post, transfer, bobToken, new { user_id = carol.Id }, HttpStatusCode.Created);
        await oidc.OkAsync(HttpMethod.Delete, transfer, carolToken, status: HttpStatusCode.NoContent);
        await Assert.That(await oidc.FailAsync(HttpMethod.Get, transfer, carolToken)).IsEqualTo((404, "not_found"));
        var actions = (await oidc.OkAsync(HttpMethod.Get, $"{Organizations}/{id}/audit-events", bobToken)).GetProperty("items").EnumerateArray().Select(item => item.GetProperty("action").GetString()).ToArray();
        await Assert.That(actions).Contains("ownership.transfer_confirmed");
        await Assert.That(actions).Contains("ownership.transfer_cancelled");
    }

    private Task<OidcHarness> StartAsync(TimeProvider? clock = null) =>
        OidcHarness.StartAsync(fixture, clock: clock);

    private static async Task<(string Recipient, TerraScale.Authentication.Features.Email.EmailMessage Message)> NextInvitationAsync(OidcHarness oidc)
    {
        while (true)
        {
            var (recipient, message) = await oidc.Email.NextAsync();
            if (message.Text.Contains(AcceptPage, StringComparison.Ordinal))
            {
                return (recipient, message);
            }
        }
    }

    [GeneratedRegex("^tsi_[0-9a-hjkmnp-tv-z]{52}$")]
    private static partial Regex Token();
}
