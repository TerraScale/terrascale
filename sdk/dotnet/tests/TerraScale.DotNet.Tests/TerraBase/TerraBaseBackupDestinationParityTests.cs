using System.Net;
using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseBackupDestinationParityTests
{
    [Test]
    [Arguments("both")]
    [Arguments("customer")]
    public async Task Destination_is_project_scoped_and_credentials_only_travel_in_the_write_body(string mode)
    {
        var calls = new List<(string Method, string Path, string? Body)>();
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            calls.Add((request.Method.Method, request.RequestUri!.AbsolutePath, request.Content is null ? null : await request.Content.ReadAsStringAsync(token)));
            await Assert.That(request.Headers.Authorization!.Parameter).IsEqualTo("project-user-token");
            await Assert.That(request.RequestUri.Query).IsEqualTo("");
            return request.Method == HttpMethod.Delete ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : TerraBaseIntegrationFixture.Json($$$"""{"endpoint":"https://s3.example.test/","region":"test-region","bucket":"test-bucket","mode":"{{{mode}}}","updatedAt":"2026-10-22T00:00:00Z"}""");
        });
        using var http = new HttpClient(handler);
        var project = TerraBaseIntegrationFixture.Client(http).Project(TerraBaseIntegrationFixture.ProjectId);
        var request = new TerraBaseBackupDestinationRequest("https://s3.example.test", "test-region", "test-bucket", "fake-access-id", "fake-secret", mode);
        var saved = await project.SetBackupDestinationAsync(request);
        var read = await project.GetBackupDestinationAsync();
        await project.DeleteBackupDestinationAsync();
        await Assert.That(saved.Mode).IsEqualTo(mode);
        await Assert.That(read).IsEqualTo(saved);
        await Assert.That(calls.Select(c => c.Method)).IsEquivalentTo(["PUT", "GET", "DELETE"]);
        await Assert.That(calls.Select(c => c.Path).Distinct().Single()).IsEqualTo($"/v1/projects/{TerraBaseIntegrationFixture.ProjectId}/backup-destination");
        using var body = JsonDocument.Parse(calls[0].Body!);
        await Assert.That(body.RootElement.GetProperty("accessKeyId").GetString()).IsEqualTo("fake-access-id");
        await Assert.That(body.RootElement.GetProperty("secretKey").GetString()).IsEqualTo("fake-secret");
        await Assert.That(body.RootElement.GetProperty("mode").GetString()).IsEqualTo(mode);
        await Assert.That(calls[1].Body).IsNull();
        await Assert.That(calls[2].Body).IsNull();
        await Assert.That(request.ToString().Contains("fake-", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    [Arguments(400, "invalid_request")]
    [Arguments(404, "not_found")]
    [Arguments(503, "storage_unavailable")]
    public async Task Configuration_failures_are_explicit_and_not_retried(int status, string code)
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(TerraBaseIntegrationFixture.Json($$$"""{"error":{"code":"{{{code}}}","message":"refused"}}""", (HttpStatusCode)status));
        });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<TerraBaseException>(() => TerraBaseIntegrationFixture.Client(http)
            .Project(TerraBaseIntegrationFixture.ProjectId).SetBackupDestinationAsync(new("https://s3.example.test", "region", "bucket", "fake-id", "fake-secret", "both")));
        await Assert.That(error!.ErrorCode).IsEqualTo(code);
        await Assert.That(calls).IsEqualTo(1);
    }
}
