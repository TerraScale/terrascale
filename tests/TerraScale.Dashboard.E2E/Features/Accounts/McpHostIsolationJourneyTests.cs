using System.Net;
using System.Text.Json;

namespace TerraScale.Dashboard.E2E.Features.Accounts;

[ClassDataSource<DashboardRootFixture>]
public sealed class McpHostIsolationJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task SharedMetadataKeepsIndependentHostDependenciesUsersAndScopesAfterDisposal()
    {
        var starting = new[]
        {
            Task.Run(() => McpIsolationScenario.StartAsync(fixture.Root, "first-host-measurement")),
            Task.Run(() => McpIsolationScenario.StartAsync(fixture.Root, "second-host-measurement"))
        };
        McpIsolationScenario[] hosts;
        try { hosts = await Task.WhenAll(starting); }
        catch
        {
            foreach (var started in starting.Where(task => task.IsCompletedSuccessfully)) await started.Result.DisposeAsync();
            throw;
        }
        await using var first = hosts[0];
        await using var second = hosts[1];
        var identities = await Task.WhenAll(SignInAsync(first.Client), SignInAsync(second.Client));
        var firstSecret = identities[0].Secret;
        var secondSecret = identities[1].Secret;
        var contexts = await Task.WhenAll(CallAsync(first.Client, firstSecret, "get_context", new { }),
            CallAsync(second.Client, secondSecret, "get_context", new { }));
        var firstScope = contexts[0].GetProperty("selection");
        var secondScope = contexts[1].GetProperty("selection");
        var firstOrganization = firstScope.GetProperty("organizationId").GetString()!;
        var firstProject = firstScope.GetProperty("projectId").GetString()!;
        var secondOrganization = secondScope.GetProperty("organizationId").GetString()!;
        var secondProject = secondScope.GetProperty("projectId").GetString()!;
        await Assert.That(firstOrganization).IsNotEqualTo(secondOrganization);
        await Assert.That(firstProject).IsNotEqualTo(secondProject);
        for (var index = 0; index < hosts.Length; index++)
            await Assert.That(contexts[index].GetProperty("user").GetProperty("email").GetString()).IsEqualTo(identities[index].Email);

        var discovery = await Task.WhenAll(HttpJourney.McpAsync(first.Client, firstSecret, "tools/list", new { }),
            HttpJourney.McpAsync(second.Client, secondSecret, "tools/list", new { }));
        await Assert.That(discovery[0].GetProperty("result").GetProperty("tools").GetArrayLength()).IsEqualTo(96);
        await Assert.That(JsonElement.DeepEquals(discovery[0].GetProperty("result"), discovery[1].GetProperty("result"))).IsTrue();
        var overviews = await Task.WhenAll(CallAsync(first.Client, firstSecret, "get_overview", new { }),
            CallAsync(second.Client, secondSecret, "get_overview", new { }));
        await Assert.That(overviews[0].GetProperty("activity").GetProperty("note").GetString()).IsEqualTo("first-host-measurement");
        await Assert.That(overviews[1].GetProperty("activity").GetProperty("note").GetString()).IsEqualTo("second-host-measurement");
        await Assert.That(overviews[1].GetProperty("activity").GetProperty("range").GetString()).IsEqualTo("7d");

        await Task.WhenAll(DeniedAsync(first.Client, firstSecret, secondOrganization, secondProject),
            DeniedAsync(second.Client, secondSecret, firstOrganization, firstProject));
        using (var foreignToken = new HttpRequestMessage(HttpMethod.Post, "/mcp"))
        {
            foreignToken.Headers.Authorization = new("Bearer", firstSecret);
            using var denied = await second.Client.SendAsync(foreignToken);
            await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }
        await first.DisposeAsync();
        await CallAsync(second.Client, secondSecret, "update_profile", new { name = "Surviving MCP host" });
        var surviving = await CallAsync(second.Client, secondSecret, "get_context", new { });
        await Assert.That(surviving.GetProperty("user").GetProperty("name").GetString()).IsEqualTo("Surviving MCP host");
        await Assert.That(surviving.GetProperty("user").GetProperty("email").GetString()).IsEqualTo(identities[1].Email);
        var overview = await CallAsync(second.Client, secondSecret, "get_overview", new { organizationId = secondOrganization, projectId = secondProject });
        await Assert.That(overview.GetProperty("activity").GetProperty("note").GetString()).IsEqualTo("second-host-measurement");
        var rediscovery = await HttpJourney.McpAsync(second.Client, secondSecret, "tools/list", new { });
        await Assert.That(JsonElement.DeepEquals(discovery[1].GetProperty("result"), rediscovery.GetProperty("result"))).IsTrue();
        await DeniedAsync(second.Client, secondSecret, firstOrganization, firstProject);
    }

    private static async Task<(string Email, string Secret)> SignInAsync(HttpClient client)
    {
        var email = "mcp-isolation-" + Guid.NewGuid().ToString("N") + "@example.test";
        using var login = await HttpJourney.LoginAsync(client, email: email, signUp: true);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        return (email, await HttpJourney.CreatePatAsync(client));
    }

    private static async Task<JsonElement> CallAsync(HttpClient client, string secret, string name, object arguments)
    {
        var response = await HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments });
        var result = response.GetProperty("result");
        await Assert.That(result.TryGetProperty("isError", out var error) && error.GetBoolean()).IsFalse().Because(name);
        return result.GetProperty("structuredContent");
    }

    private static async Task DeniedAsync(HttpClient client, string secret, string organizationId, string projectId)
    {
        var response = await HttpJourney.McpAsync(client, secret, "tools/call", new { name = "get_organization", arguments = new { organizationId, projectId } });
        await Assert.That(response.GetProperty("result").GetProperty("isError").GetBoolean()).IsTrue();
        await Assert.That(response.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()).IsEqualTo("Organization is unavailable.");
    }
}
