using System.Net;
using System.Text.Json;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class RequestLogFilterContractTests
{
    [Test]
    public async Task Generated_status_filter_matches_the_public_listener_validation()
    {
        await using var api = await ManagementApi.StartAsync();
        var document = await api.Client.GetFromJsonAsync<JsonElement>(new Uri("/openapi/v1.json", UriKind.Relative));
        var operation = document.GetProperty("paths")
            .GetProperty("/v1/projects/{project}/databases/{database}/logs").GetProperty("get");
        await Assert.That(operation.GetProperty("operationId").GetString()).IsEqualTo("listDatabaseLogs");
        var filter = operation.GetProperty("parameters").EnumerateArray().Single(parameter => parameter.GetProperty("name").GetString() == "status");
        await Assert.That(filter.GetProperty("in").GetString()).IsEqualTo("query");
        await Assert.That(filter.TryGetProperty("required", out var required) && required.GetBoolean()).IsFalse();
        var schema = filter.GetProperty("schema");
        await Assert.That(schema.GetProperty("type").GetString()).IsEqualTo("string");
        await Assert.That(schema.TryGetProperty("default", out _)).IsFalse();
        var allowed = schema.GetProperty("enum").EnumerateArray().Select(value => value.GetString()).ToArray();
        await Assert.That(string.Join(',', allowed)).IsEqualTo("2xx,3xx,4xx,5xx");

        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        using var project = api.WithToken(api.Issuer.Mint(EntityIds.New().ToString(), token =>
        {
            token.Claims["project_id"] = database.Project;
            token.Claims["scope"] = "databases:read";
        }));
        foreach (var value in allowed.Prepend(null))
        {
            using var response = await project.GetAsync(ManagementApi.Uri(database.Data + "/logs" + (value is null ? "" : "?status=" + value)));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(response.Headers.CacheControl?.NoStore).IsTrue();
        }
        foreach (var query in new[] { "?status=error", "?status=200", "?status=2xx&status=4xx" })
        {
            using var response = await project.GetAsync(ManagementApi.Uri(database.Data + "/logs" + query));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            var error = await response.Content.ReadFromJsonAsync<JsonElement>();
            await Assert.That(error.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("invalid_status");
        }
    }
}
