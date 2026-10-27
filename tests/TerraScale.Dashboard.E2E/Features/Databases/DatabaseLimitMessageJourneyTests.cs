using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace TerraScale.Dashboard.E2E.Features.Databases;

[ClassDataSource<DashboardRootFixture>]
public sealed class DatabaseLimitMessageJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    [Arguments(413, "document exceeds 64 KiB", "The payload is too large. Reduce the size and try again.")]
    [Arguments(429, "stream slots exhausted", "Too many requests on this database. Wait a moment and try again.")]
    public async Task SdkLimitRefusalsKeepTheStatusAndActionableReason(int status, string reason, string message)
    {
        TestAuth auth = null!;
        var organization = "";
        var project = "";
        var refusals = 0;
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root,
            DatabaseSdkJourneyTests.CaptureAuth(value => auth = value), producerExtension: http =>
            {
                if (project.Length == 0 || http.Request.Method != "POST" || http.Request.Path != $"/v1/projects/{project}/databases")
                    return Task.FromResult<IResult?>(null);
                DatabaseSdkJourneyTests.DemandProjectToken(auth, http, organization, project, "databases:write");
                Interlocked.Increment(ref refusals);
                return Task.FromResult<IResult?>(Results.Json(new { error = new { code = "limit", message = reason } }, statusCode: status));
            });
        using var client = scenario.CreateClient();
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        using var identity = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me"));
        organization = identity.RootElement.GetProperty("organizationId").GetString()!;
        project = identity.RootElement.GetProperty("projectId").GetString()!;
        using var response = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
        {
            ["_csrf"] = await HttpJourney.CsrfAsync(client, "/databases"),
            ["name"] = "limit-refusal",
            ["region"] = "GRU",
            ["allRegions"] = "true"
        });
        await Assert.That(refusals).IsGreaterThan(0);
        await Assert.That((int)response.StatusCode).IsEqualTo(status);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/problem+json");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        await Assert.That(body.RootElement.GetProperty("status").GetInt32()).IsEqualTo(status);
        await Assert.That(body.RootElement.GetProperty("detail").GetString()).IsEqualTo(message + " " + reason);
        await Assert.That(body.RootElement.GetProperty("requestId").GetString()).IsEqualTo(response.Headers.GetValues("X-Request-ID").Single());
    }
}
