using Microsoft.Extensions.Logging.Abstractions;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Fast.Platform;
using TerraScale.Auth.Hosting;
using TerraScale.Auth.Tests;

namespace TerraScale.Auth.Fast.Store;

/// <summary>The request path of <see cref="TerraBaseMembers"/>, without its background probe.</summary>
public sealed class TerraBaseMembersTests
{
    [Test]
    [Arguments("https://member.invalid:9465")]
    [Arguments("https://127.0.0.1:9")]
    public async Task A_member_the_last_request_could_not_reach_is_skipped_at_once(string unreachable)
    {
        using var members = Members($"{unreachable};https://127.0.0.1:10", failover: true);
        using var http = new HttpClient(members.CreateHandler());

        var preferred = await members.ResolveAsync();
        await Assert.That(preferred).IsEqualTo(new Uri(unreachable));
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(new Uri(preferred, "readyz")));
        await Assert.That(await members.ResolveAsync()).IsEqualTo(new Uri("https://127.0.0.1:10"));
    }

    [Test]
    public async Task A_single_member_stays_pinned_while_it_is_down()
    {
        using var members = Members("https://127.0.0.1:9", failover: false);
        using var http = new HttpClient(members.CreateHandler());

        var member = await members.ResolveAsync();
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(new Uri(member, "readyz")));
        await Assert.That(await members.ResolveAsync()).IsEqualTo(member);
    }

    private static TerraBaseMembers Members(string endpoints, bool failover)
    {
        var settings = AuthTestServer.DefaultSettings();
        settings["terrascale-api-endpoints"] = endpoints;
        settings["terrascale-api-ca"] = ConfigurationTests.Root();
        settings["terrascale-api-failover"] = failover ? "true" : "false";
        return new TerraBaseMembers(AuthOptions.Load(settings), NullLogger<TerraBaseMembers>.Instance);
    }
}
