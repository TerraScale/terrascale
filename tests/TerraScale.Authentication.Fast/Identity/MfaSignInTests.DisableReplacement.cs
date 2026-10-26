using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Identity;

public sealed partial class MfaSignInTests
{
    [Test]
    public async Task A_sign_out_everywhere_after_removal_commits_refuses_the_replacement_session()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        // Once armed, holds the first user read after the factor read: past the removal commit, before the replacement session.
        var armed = false;
        var sawFactor = false;
        var pause = new PausingTransactions(request =>
        {
            if (!armed || request.Method != HttpMethod.Get)
            {
                return false;
            }

            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains($"/collections/{AuthStore.MfaCollection}/documents/", StringComparison.Ordinal))
            {
                sawFactor = true;
                return false;
            }

            return sawFactor && path.Contains($"/collections/{AuthStore.UserCollection}/documents/", StringComparison.Ordinal);
        })
        { InnerHandler = TerraBaseFixture.NewHandler() };
        using var http = new HttpClient(pause);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock, services: services => services.AddSingleton(http));
        await using var replica = await oidc.StartReplicaAsync(clock: clock);
        var user = await oidc.CreateUserAsync("frank@example.test");
        var (_, codes, enrolled) = await EnrolAsync(oidc, await oidc.SignInAsync(user.Email), clock);

        // Task.Run in HoldNextAsync publishes this before the request starts.
        armed = true;
        var held = await pause.HoldNextAsync(() => SendAsync(oidc, HttpMethod.Post, "/ui/terrascale/security/totp/disable", enrolled, new() { ["code"] = codes[0] }));
        HttpResponseMessage response;
        try
        {
            // The removal committed; the account is signed out everywhere before the replacement is issued.
            await Assert.That((await oidc.Database.ListDocumentsAsync(AuthStore.MfaCollection)).Items.Single(item => item.Document.GetProperty("userId").GetString() == user.Id).Document.GetProperty("enabled").GetBoolean()).IsFalse();
            await Assert.That(await replica.App.Services.GetRequiredService<Sessions>().RevokeAllAsync(user.InstanceId, user.Id)).IsTrue();
        }
        finally
        {
            response = await pause.ReleaseAsync(held);
        }

        using (response)
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await response.Content.ReadAsStringAsync()).Contains("Two-factor authentication is off.");
            await Assert.That(OidcHarness.SessionCookie(response)).IsNull();
        }

        // No session exists at the authority the sign-out everywhere established.
        var epoch = (await oidc.Database.ListDocumentsAsync(AuthStore.UserCollection)).Items.Single(item => item.Document.GetProperty("id").GetString() == user.Id).Document.GetProperty("authEpoch").GetInt64();
        await Assert.That((await oidc.Database.ListDocumentsAsync(AuthStore.SessionCollection)).Items
            .Any(item => item.Document.GetProperty("userId").GetString() == user.Id && item.Document.GetProperty("authEpoch").GetInt64() >= epoch)).IsFalse();
    }
}
