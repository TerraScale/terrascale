using System.Net;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseDatabaseRenameTests
{
    private const string DatabaseId = "01JA0000000000000000000004";
    private const string Renamed = """
        {"id":"01JA0000000000000000000004","projectId":"01JA0000000000000000000001","name":"production",
         "branchedFrom":null,"createdAt":"2026-10-12T00:00:00+00:00","region":"sjc","homeRegion":"gru"}
        """;

    [Test]
    [Arguments(DatabaseId)]
    public async Task Rename_sends_one_name_only_patch_and_reads_database_identity_and_home_region(string databaseId)
    {
        var requests = 0;
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            requests++;
            await Assert.That(request.Method).IsEqualTo(HttpMethod.Patch);
            await Assert.That(request.RequestUri!.AbsolutePath).IsEqualTo($"/v1/projects/{TerraBaseIntegrationFixture.ProjectId}/databases/{databaseId}");
            await Assert.That(request.Headers.Authorization!.ToString()).IsEqualTo("Bearer project-user-token");
            await Assert.That(request.Headers.Contains("X-TerraBase-User-Id")).IsFalse();
            await Assert.That(request.Headers.GetValues("X-TerraScale-Region-Pin").Single()).IsEqualTo("sjc");
            await Assert.That(request.Content!.Headers.ContentType!.MediaType).IsEqualTo("application/json");
            await Assert.That(await request.Content.ReadAsStringAsync(token)).IsEqualTo("{\"name\":\"production\"}");
            return TerraBaseIntegrationFixture.Json(Renamed);
        });
        using var http = new HttpClient(handler);

        var result = await TerraBaseIntegrationFixture.Client(http).Project(TerraBaseIntegrationFixture.ProjectId)
            .RenameDatabaseAsync(databaseId, "production");

        await Assert.That(requests).IsEqualTo(1);
        await Assert.That(result.Id).IsEqualTo(DatabaseId);
        await Assert.That(result.ProjectId).IsEqualTo(TerraBaseIntegrationFixture.ProjectId);
        await Assert.That(result.Name).IsEqualTo("production");
        await Assert.That(result.Region).IsEqualTo("sjc");
        await Assert.That(result.HomeRegion).IsEqualTo("gru");
    }

}
