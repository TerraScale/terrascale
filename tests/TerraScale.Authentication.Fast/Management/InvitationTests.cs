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
