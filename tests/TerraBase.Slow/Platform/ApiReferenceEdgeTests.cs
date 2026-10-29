using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TerraBase.Testing;

namespace TerraBase.Slow.Platform;

public sealed class ApiReferenceEdgeTests
{

    [Test]
    public async Task Readiness_region_pin_errors_have_error_examples_while_unavailable_keeps_the_status_body()
    {
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(settings =>
        {
            tls.Apply(settings);
            settings["region"] = "gru-1";
        });
        using var client = tls.CreateClient(server.ClientAddress!);
        using var request = new HttpRequestMessage(HttpMethod.Get, Uri("/readyz"));
        request.Headers.Add("X-TerraScale-Region-Pin", "sfo-1");
        using var response = await client.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("application/json");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var code = body.RootElement.GetProperty("error").GetProperty("code").GetString();
        await Assert.That(code).IsEqualTo("region_pin_mismatch");

        using var reference = await client.GetAsync(Uri("/openapi/v1.json"));
        await Assert.That(reference.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await reference.Content.ReadAsStringAsync());
        var responses = document.RootElement.GetProperty("paths").GetProperty("/readyz").GetProperty("get").GetProperty("responses");
        var misdirected = responses.GetProperty("421");
        await Assert.That(misdirected.GetProperty("description").GetString()).Contains(code!);
        var error = misdirected.GetProperty("content").GetProperty("application/json");
        var schema = error.GetProperty("schema");
        await Assert.That(schema.GetProperty("required").EnumerateArray().Select(item => item.GetString())).Contains("error");
        var details = schema.GetProperty("properties").GetProperty("error");
        await Assert.That(details.GetProperty("required").EnumerateArray().Select(item => item.GetString())).Contains("code");
        await Assert.That(details.GetProperty("properties").GetProperty("code").GetProperty("type").GetString()).IsEqualTo("string");
        await Assert.That(error.GetProperty("example").GetProperty("error").GetProperty("code").GetString()).IsEqualTo(code);
        await Assert.That(error.GetProperty("example").TryGetProperty("status", out _)).IsFalse();
        var unavailable = responses.GetProperty("503").GetProperty("content").GetProperty("application/json");
        await Assert.That(unavailable.GetProperty("schema").GetRawText()).Contains("StatusBody");
        await Assert.That(unavailable.GetProperty("example").GetProperty("status").GetString()).IsEqualTo("unavailable");
        await Assert.That(unavailable.GetProperty("example").TryGetProperty("error", out _)).IsFalse();
    }

    [Test]
    public async Task Patch_conflict_examples_and_descriptions_match_project_JWT_responses()
    {
        await using var api = await DocumentApi.StartAsync();
        var id = DocumentApi.NewId();
        await Assert.That((await api.PutAsync(id, new { n = 1 })).Status).IsEqualTo(HttpStatusCode.Created);
        using var owner = api.Management.As(api.Tenant.User);
        using var client = api.Management.WithToken(api.Management.Issuer.Mint(api.Tenant.User, token =>
        {
            token.Claims["project_id"] = api.Tenant.ProjectId;
            token.Claims["scope"] = "documents:write";
        }));
        var ops = new object[] { new { op = "append", path = "/n", value = 1 } };
        var patch = await DocumentApi.SendAsync(client, HttpMethod.Patch, api.Document(id),
            JsonContent.Create(new { ops }, mediaType: new MediaTypeHeaderValue("application/vnd.terrabase.ops+json")));
        var transaction = await DocumentApi.SendAsync(client, HttpMethod.Post, api.Transactions,
            JsonContent.Create(new { ops = new object[] { new { op = "patch", collection = "items", id, ops } } }));
        using var reference = await client.GetAsync(Uri("/openapi/v1.json"));
        await Assert.That(reference.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await reference.Content.ReadAsStringAsync());
        var operations = json.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject()
                .Where(operation => operation.Value.TryGetProperty("responses", out _))
                .Select(operation => operation.Value));

        foreach (var (operationId, result) in new[] { ("patchDocument", patch), ("runTransaction", transaction) })
        {
            await Assert.That((result.Status, result.Code)).IsEqualTo((HttpStatusCode.Conflict, "patch_conflict"));
            var documented = operations.Single(operation => operation.GetProperty("operationId").GetString() == operationId)
                .GetProperty("responses").GetProperty("409");
            await Assert.That(documented.GetProperty("content").GetProperty("application/json").GetProperty("example")
                .GetProperty("error").GetProperty("code").GetString()).IsEqualTo(result.Code).Because(operationId);
            await Assert.That(documented.GetProperty("description").GetString()).Contains(result.Code!).Because(operationId);
        }
    }

    private static Uri Uri(string path) => new(path, UriKind.Relative);
}
