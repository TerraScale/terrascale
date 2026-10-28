using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Identity;

/// <summary>Step-up gated sign-in email changes: a code at the new address commits, a link at the old one cancels.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed partial class EmailChangeTests(TerraBaseFixture fixture)
{
    private const string Change = "/v1/terrascale/account/email-change";
    private const string Confirm = Change + "/confirm";

    [Test]
    public async Task A_confirmed_change_moves_sign_in_to_the_new_address_and_ends_every_session()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var (user, token) = await oidc.SignInUserAsync("erin@example.test");

        var pending = await oidc.OkAsync(HttpMethod.Post, Change, token, new { new_email = " Erin.New@Example.test " }, HttpStatusCode.Accepted);
        await Assert.That(pending.GetProperty("new_email").GetString()).IsEqualTo("erin.new@example.test");
        var (code, notice) = await ChangeMailsAsync(oidc, "erin.new@example.test", "erin@example.test");
        await Assert.That(notice.Text).Contains("erin.new@example.test").And.Contains("/ui/terrascale/email-change/cancel?user=" + user.Id);

        var wrong = code == "000000" ? "111111" : "000000";
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, Confirm, token, new { code = wrong })).IsEqualTo((403, "email_change_denied"));
        await oidc.OkAsync(HttpMethod.Post, Confirm, token, new { code }, HttpStatusCode.NoContent);

        // The old sign-in is over, the old address no longer names the account, and the code cannot be replayed.
        await Assert.That((await oidc.FailAsync(HttpMethod.Post, Confirm, token, new { code })).Status).IsEqualTo(401);
        var users = oidc.Server.App.Services.GetRequiredService<Users>();
        await Assert.That(await users.FindByEmailAsync(OidcIssuer.InstanceId, "erin@example.test")).IsNull();
        await Assert.That((await users.FindByEmailAsync(OidcIssuer.InstanceId, "erin.new@example.test"))?.Id).IsEqualTo(user.Id);

        clock.UtcNow += HostedAuth.MailCooldown;
        var fresh = await SignInAgainAsync(oidc, "erin.new@example.test");
        await oidc.OkAsync(HttpMethod.Get, "/v1/terrascale/account/mfa", fresh);
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, Confirm, fresh, new { code })).IsEqualTo((403, "email_change_denied"));
    }

    [Test]
    public async Task Proofless_duplicate_cross_account_and_expired_changes_are_denied()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var (_, token) = await oidc.SignInUserAsync("finn@example.test");
        clock.UtcNow += HostedAuth.MailCooldown;
        var (_, other) = await oidc.SignInUserAsync("gale@example.test");

        using (var bare = new HttpRequestMessage(HttpMethod.Post, Change) { Content = JsonContent.Create(new { new_email = "finn.2@example.test" }) })
        {
            bare.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var refused = await oidc.Client.SendAsync(bare);
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        }

        await Assert.That(await oidc.FailAsync(HttpMethod.Post, Change, token, new { new_email = "gale@example.test" })).IsEqualTo((409, "email_taken"));
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, Change, token, new { new_email = "finn@example.test" })).IsEqualTo((400, "invalid_request"));

        await oidc.OkAsync(HttpMethod.Post, Change, token, new { new_email = "finn.2@example.test" }, HttpStatusCode.Accepted);
        var code = await oidc.Email.NextCodeAsync("finn.2@example.test");
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, Confirm, other, new { code })).IsEqualTo((403, "email_change_denied"));

        // Another account claiming the address first wins the unique reservation.
        await oidc.CreateUserAsync("finn.2@example.test");
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, Confirm, token, new { code })).IsEqualTo((409, "email_taken"));

        await oidc.OkAsync(HttpMethod.Post, Change, token, new { new_email = "finn.3@example.test" }, HttpStatusCode.Accepted);
        code = await oidc.Email.NextCodeAsync("finn.3@example.test");
        clock.UtcNow += EmailChanges.Lifetime;
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, Confirm, token, new { code })).IsEqualTo((403, "email_change_denied"));
        await Assert.That((await oidc.Server.App.Services.GetRequiredService<Users>().FindByEmailAsync(OidcIssuer.InstanceId, "finn@example.test"))).IsNotNull();
    }

    [Test]
    public async Task The_old_address_cancel_link_voids_the_change_and_signs_out_everywhere()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var (_, token) = await oidc.SignInUserAsync("hana@example.test");
        await oidc.OkAsync(HttpMethod.Post, Change, token, new { new_email = "hana.new@example.test" }, HttpStatusCode.Accepted);
        var (code, notice) = await ChangeMailsAsync(oidc, "hana.new@example.test", "hana@example.test");
        var link = new Uri(CancelLink().Match(notice.Text).Value);

        using var page = await oidc.Client.GetAsync(link.PathAndQuery);
        await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var query = System.Web.HttpUtility.ParseQueryString(link.Query);
        var fields = new Dictionary<string, string> { ["user"] = query["user"]!, ["token"] = query["token"]! };
        using (var crossSite = await oidc.PostFormAsync(link.AbsolutePath, fields, origin: "https://evil.example"))
        {
            await Assert.That(crossSite.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        }

        using var cancelled = await oidc.PostFormAsync(link.AbsolutePath, fields);
        await Assert.That(cancelled.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await oidc.FailAsync(HttpMethod.Post, Confirm, token, new { code })).Status).IsEqualTo(401);

        clock.UtcNow += HostedAuth.MailCooldown;
        var fresh = await SignInAgainAsync(oidc, "hana@example.test");
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, Confirm, fresh, new { code })).IsEqualTo((403, "email_change_denied"));
    }

    private static async Task<string> SignInAgainAsync(OidcHarness oidc, string email)
    {
        var session = await oidc.SignInAsync(email);
        var (verifier, challenge) = OidcHarness.Pkce();
        using var redeemed = await oidc.RedeemCodeAsync(await oidc.CodeAsync(session, challenge), verifier);
        return (await OidcHarness.JsonAsync(redeemed)).GetProperty("access_token").GetString()!;
    }

    // The outbox delivers the new address's code and the old address's notice in either order.
    private static async Task<(string Code, EmailMessage Notice)> ChangeMailsAsync(OidcHarness oidc, string newAddress, string oldAddress)
    {
        (string Recipient, EmailMessage Message)[] mails = [await oidc.Email.NextAsync(), await oidc.Email.NextAsync()];
        await Assert.That(mails.Select(mail => mail.Recipient).Order().ToArray()).IsEquivalentTo(new[] { newAddress, oldAddress }.Order().ToArray());
        return (SixDigits().Match(mails.Single(mail => mail.Recipient == newAddress).Message.Text).Value, mails.Single(mail => mail.Recipient == oldAddress).Message);
    }

    [GeneratedRegex(@"\b[0-9]{6}\b")]
    private static partial Regex SixDigits();

    [GeneratedRegex(@"https?://\S+/ui/terrascale/email-change/cancel\?user=[0-9A-Z]{26}&token=[A-Za-z0-9_-]{43}")]
    private static partial Regex CancelLink();
}
