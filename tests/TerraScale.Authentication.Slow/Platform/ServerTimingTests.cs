using System.Net;
using System.Text.RegularExpressions;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Tests;
using static TerraScale.Authentication.Tests.PlatformCalls;

namespace TerraScale.Authentication.Slow.Platform;

// The dashboard profiles its ts-auth calls (QA round 4, dashui:26/36): bearer API calls and token requests report ts-auth's own time and
// its storage time; hosted pages report nothing, so storage call counts never hint whether an account exists.
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed partial class ServerTimingTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task Bearer_api_calls_report_app_and_storage_time_and_hosted_pages_report_nothing()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var (_, token) = await oidc.SignInUserAsync("alice@example.test");
        foreach (var path in new[] { Organizations, $"/{OidcIssuer.Slug}/oauth/userinfo" })
        {
            using var response = await oidc.CallAsync(HttpMethod.Get, path, token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var timing = string.Join(", ", response.Headers.GetValues("Server-Timing"));
            await Assert.That(Timing().IsMatch(timing)).IsTrue().Because(timing);
        }

        // Token requests report too: their counts show how many storage writes one grant waited for.
        var refresh = (await oidc.SignInAndRedeemAsync("bob@example.test")).Tokens.GetProperty("refresh_token").GetString()!;
        using (var refreshed = await oidc.RefreshAsync(refresh))
        {
            await Assert.That(refreshed.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var timing = string.Join(", ", refreshed.Headers.GetValues("Server-Timing"));
            await Assert.That(Timing().IsMatch(timing)).IsTrue().Because(timing);
        }

        using var page = await oidc.Client.GetAsync(new Uri($"/ui/{OidcIssuer.Slug}/sign-in", UriKind.Relative));
        await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(page.Headers.Contains("Server-Timing")).IsFalse();
    }

    // The harness supplies its own storage HttpClient, so the call count here can be zero; production wraps App's client.
    [GeneratedRegex("""\Aapp;dur=\d+\.\d, storage;dur=\d+\.\d;desc="\d+ calls, \d+ writes"\z""")]
    private static partial Regex Timing();
}
