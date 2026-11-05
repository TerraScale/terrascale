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
        await using var oidc = await OidcHarness.StartSharedManagementAsync(fixture);
        var (user, token) = await oidc.CreateUserTokenAsync(oidc.UniqueEmail("erin"));

        var pending = await oidc.OkAsync(HttpMethod.Post, Change, token, new { new_email = " " + oidc.UniqueEmail("erin.new").ToUpperInvariant() + " " }, HttpStatusCode.Accepted);
        await Assert.That(pending.GetProperty("new_email").GetString()).IsEqualTo(oidc.UniqueEmail("erin.new"));
        var (code, notice) = await ChangeMailsAsync(oidc, oidc.UniqueEmail("erin.new"), oidc.UniqueEmail("erin"));
        await Assert.That(notice.Text).Contains(oidc.UniqueEmail("erin.new")).And.Contains("/ui/terrascale/email-change/cancel?user=" + user.Id);

        var wrong = code == "000000" ? "111111" : "000000";
        await Assert.That(await oidc.FailAsync(HttpMethod.Post, Confirm, token, new { code = wrong })).IsEqualTo((403, "email_change_denied"));
        await oidc.OkAsync(HttpMethod.Post, Confirm, token, new { code }, HttpStatusCode.NoContent);

        // The old sign-in is over, the old address no longer names the account, and the code cannot be replayed.
        await Assert.That((await oidc.FailAsync(HttpMethod.Post, Confirm, token, new { code })).Status).IsEqualTo(401);
        var users = oidc.Server.App.Services.GetRequiredService<Users>();
        await Assert.That(await users.FindByEmailAsync(OidcIssuer.InstanceId, oidc.UniqueEmail("erin"))).IsNull();
        await Assert.That((await users.FindByEmailAsync(OidcIssuer.InstanceId, oidc.UniqueEmail("erin.new")))?.Id).IsEqualTo(user.Id);

        var fresh = await SignInAgainAsync(oidc, oidc.UniqueEmail("erin.new"));
        await oidc.OkAsync(HttpMethod.Get, "/v1/terrascale/account/mfa", fresh);
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
        (string Recipient, EmailMessage Message)[] mails = [await oidc.Email.NextAsync(newAddress), await oidc.Email.NextAsync(oldAddress)];
        await Assert.That(mails.Select(mail => mail.Recipient).Order().ToArray()).IsEquivalentTo(new[] { newAddress, oldAddress }.Order().ToArray());
        return (SixDigits().Match(mails.Single(mail => mail.Recipient == newAddress).Message.Text).Value, mails.Single(mail => mail.Recipient == oldAddress).Message);
    }

    [GeneratedRegex(@"\b[0-9]{6}\b")]
    private static partial Regex SixDigits();
}
