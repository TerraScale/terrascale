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

namespace TerraScale.Authentication.Slow.Identity;

/// <summary>Step-up gated sign-in email changes: a code at the new address commits, a link at the old one cancels.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed partial class EmailChangeTests(TerraBaseFixture fixture)
{
    private const string Change = "/v1/terrascale/account/email-change";
    private const string Confirm = Change + "/confirm";


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



    // The outbox delivers the new address's code and the old address's notice in either order.


}
