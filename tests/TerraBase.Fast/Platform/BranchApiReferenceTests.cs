using System.Text.Json;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

public sealed class BranchApiReferenceTests
{
    [Test]
    [Arguments("", "branchDatabase")]
    [Arguments("/if-id/{expectedId}", "branchDatabaseIfId")]
    public async Task Branch_openapi_documents_home_region_requirement_and_wrong_region_response(string suffix, string operationId)
    {
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var client = tls.CreateClient(server.ClientAddress!);
        var document = await client.GetFromJsonAsync<JsonElement>(new Uri("/openapi/v1.json", UriKind.Relative));
        var operation = document.GetProperty("paths")
            .GetProperty("/v1/projects/{project}/databases/{database}/branches" + suffix).GetProperty("post");

        await Assert.That(operation.GetProperty("operationId").GetString()).IsEqualTo(operationId);
        if (suffix.Length > 0)
        {
            await Assert.That(operation.GetProperty("responses").GetProperty("409").GetProperty("description").GetString()).Contains("database_identity_changed");
        }
        await Assert.That(operation.GetProperty("description").GetString()).Contains("home region");
        var response = operation.GetProperty("responses").GetProperty("421");
        await Assert.That(response.GetProperty("description").GetString()).Contains("wrong_region");
        var error = response.GetProperty("content").GetProperty("application/json")
            .GetProperty("schema").GetProperty("properties").GetProperty("error");
        await Assert.That(error.GetProperty("properties").GetProperty("code").GetProperty("type").GetString()).IsEqualTo("string");
        await Assert.That(error.GetProperty("required").EnumerateArray().Any(field => field.GetString() == "code")).IsTrue();
    }
}
