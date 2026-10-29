using System.Globalization;
using System.Net;
using System.Text.Json;
using Bogus;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Documents;

public sealed class DocumentApiTests
{
    private static readonly Faker Faker = new();
    private static readonly string[] WriteScope = ["documents:write"];
    private static readonly string[] SingleTag = ["a"];
    [Test]
    public async Task Documents_round_trip_with_versions_and_offsets()
    {
        await using var api = await DocumentApi.StartAsync();
        var id = DocumentApi.NewId();
        var person = new { name = Faker.Name.FullName(), email = Faker.Internet.Email(), age = Faker.Random.Int(18, 90) };

        var created = await api.PutAsync(id, person);
        var replaced = await api.PutAsync(id, person with { age = person.age + 1 });
        var read = await api.GetAsync(id);
        var deleted = await api.SendAsync(HttpMethod.Delete, api.Document(id));
        var gone = await api.GetAsync(id);

        await Assert.That(created.Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(created.Version).Matches("^tbv1-[0-9a-f]{32}$");
        await Assert.That(created.ETag).IsEqualTo(created.Version);
        await Assert.That(created.Json.GetProperty("durability").GetString()).IsEqualTo("local_durable");
        await Assert.That(replaced.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(replaced.Version).IsNotEqualTo(created.Version);
        await Assert.That(Offset(replaced)).IsGreaterThan(Offset(created));
        await Assert.That(read.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(read.ETag).IsEqualTo(replaced.Version);
        await Assert.That(read.Json.GetProperty("id").GetString()).IsEqualTo(id);
        await Assert.That(read.Json.GetProperty("state").GetString()).IsEqualTo("value");
        await Assert.That(read.Json.GetProperty("document").GetProperty("email").GetString()).IsEqualTo(person.email);
        await Assert.That(read.Json.GetProperty("document").GetProperty("age").GetInt32()).IsEqualTo(person.age + 1);
        await Assert.That(deleted.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(deleted.Version).IsNotEqualTo(replaced.Version);
        await Assert.That(Offset(deleted)).IsGreaterThan(Offset(replaced));
        // A deleted document's 404 carries its tombstone's version.
        await Assert.That(gone.Status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(gone.Code).IsEqualTo("not_found");
        await Assert.That(gone.ETag).IsEqualTo(deleted.Version);
        // The settings reached Program: the database lives in this server's own data directory.
        // The database lives in its tenant's directory.
        await Assert.That(File.Exists(await api.DatabaseFileAsync())).IsTrue();
    }

    [Test]
    public async Task Post_creates_a_document_with_a_generated_uuidv7()
    {
        await using var api = await DocumentApi.StartAsync();

        var created = await api.SendAsync(HttpMethod.Post, api.Documents, new { title = Faker.Commerce.ProductName() });
        var id = created.Json.GetProperty("id").GetString()!;
        var read = await api.SendAsync(HttpMethod.Get, created.Location!.OriginalString);

        await Assert.That(created.Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(EntityIds.IsValid(id)).IsTrue();
        await Assert.That(id).IsEqualTo(id.ToUpperInvariant());
        await Assert.That(created.Location.OriginalString).IsEqualTo(api.Document(id));
        await Assert.That(read.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(read.Version).IsEqualTo(created.Version);
    }

    [Test]
    public async Task Preconditions_guard_writes_and_reads()
    {
        await using var api = await DocumentApi.StartAsync();
        var id = DocumentApi.NewId();
        var first = await api.PutAsync(id, new { n = 1 }, DocumentApi.IfNoneMatch("*"));

        var createAgain = await api.PutAsync(id, new { n = 2 }, DocumentApi.IfNoneMatch("*"));
        var second = await api.PutAsync(id, new { n = 2 }, DocumentApi.IfMatch(first.Version));
        var stale = await api.PutAsync(id, new { n = 3 }, DocumentApi.IfMatch(first.Version));
        var notModified = await api.GetAsync(id, DocumentApi.IfNoneMatch(second.Version));
        var modified = await api.GetAsync(id, DocumentApi.IfNoneMatch(first.Version));
        var deleted = await api.SendAsync(HttpMethod.Delete, api.Document(id), configure: DocumentApi.IfMatch(second.Version));

        await Assert.That(first.Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That((createAgain.Status, createAgain.Code)).IsEqualTo((HttpStatusCode.PreconditionFailed, "precondition_failed"));
        await Assert.That(second.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((stale.Status, stale.Code)).IsEqualTo((HttpStatusCode.PreconditionFailed, "precondition_failed"));
        await Assert.That(notModified.Status).IsEqualTo(HttpStatusCode.NotModified);
        await Assert.That(notModified.ETag).IsEqualTo(second.Version);
        await Assert.That(modified.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(deleted.Status).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Merge_patch_follows_rfc_7396()
    {
        await using var api = await DocumentApi.StartAsync();
        var id = DocumentApi.NewId();
        await api.PutAsync(id, new { name = "Ana", legacy = true, profile = new { city = "Lisbon", zip = "1000" }, tags = SingleTag });

        var patched = await api.SendRawAsync(
            HttpMethod.Patch, api.Document(id), """{"legacy":null,"profile":{"zip":null,"country":"PT"},"tags":["b"],"new":{"x":1}}""",
            "application/merge-patch+json");
        var read = await api.GetAsync(id);

        await Assert.That(patched.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(read.ETag).IsEqualTo(patched.Version);
        // Storage keeps top-level fields in name order (docs/terrabase/multi-region-replication.md section 4).
        await Assert.That(read.Json.GetProperty("document").GetRawText()).IsEqualTo(
            """{"name":"Ana","new":{"x":1},"profile":{"city":"Lisbon","country":"PT"},"tags":["b"]}""");
    }

    [Test]
    public async Task Patch_operations_set_unset_inc_and_append()
    {
        await using var api = await DocumentApi.StartAsync();
        var id = DocumentApi.NewId();
        await api.PutAsync(id, new { legacy = 1, name = "x", tags = SingleTag });
        const string ops = "application/vnd.terrabase.ops+json";

        var patched = await api.SendRawAsync(HttpMethod.Patch, api.Document(id), """
            {"ops":[{"op":"set","path":"/profile/name","value":"Ana"},
                    {"op":"unset","path":"/legacy"},
                    {"op":"inc","path":"/count","by":2},
                    {"op":"append","path":"/tags","value":"b"}]}
            """, ops);
        var read = await api.GetAsync(id);

        await Assert.That(patched.Status).IsEqualTo(HttpStatusCode.OK);
        var document = read.Json.GetProperty("document");
        await Assert.That(document.GetProperty("profile").GetProperty("name").GetString()).IsEqualTo("Ana");
        await Assert.That(document.TryGetProperty("legacy", out _)).IsFalse();
        await Assert.That(document.GetProperty("count").GetInt32()).IsEqualTo(2);
        await Assert.That(document.GetProperty("tags").GetRawText()).IsEqualTo("""["a","b"]""");
    }

    [Test]
    public async Task Listing_pages_through_a_collection_in_id_order()
    {
        await using var api = await DocumentApi.StartAsync();
        // An exact multiple of the page size: the last full page must already report no next page.
        var ids = Enumerable.Range(0, 5).Select(_ => DocumentApi.NewId()).ToList();
        foreach (var id in Faker.Random.Shuffle(ids))
        {
            await api.PutAsync(id, new { title = Faker.Commerce.ProductName() });
        }

        // Tombstones are not listed.
        await api.SendAsync(HttpMethod.Delete, api.Document(ids[2]));
        ids.RemoveAt(2);

        var seen = new List<string>();
        var pages = 0;
        string? cursor = null;
        do
        {
            pages++;
            var page = await api.SendAsync(HttpMethod.Get, $"{api.Documents}?limit=2" + (cursor is null ? "" : $"&cursor={cursor}"));
            seen.AddRange(page.Ids);
            cursor = page.Json.GetProperty("next_cursor").GetString();
        }
        while (cursor is not null);

        var defaults = await api.SendAsync(HttpMethod.Get, api.Documents);

        await Assert.That(pages).IsEqualTo(2);
        await Assert.That(seen).IsEquivalentTo(ids);
        await Assert.That(seen).IsOrderedBy(id => id, StringComparer.Ordinal);
        await Assert.That(defaults.Ids.Count).IsEqualTo(4);
        await Assert.That(defaults.Json.GetProperty("next_cursor").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task A_put_answers_the_etag_of_the_body_a_read_returns()
    {
        await using var api = await DocumentApi.StartAsync();
        var id = DocumentApi.NewId();
        var name = Faker.Name.FirstName();
        await api.PutAsync(id, new { stale = Faker.Lorem.Word(), name });

        // Fields out of name order and the document's own id: the stored body has its fields in name order, without the
        // id or the previous write's fields, and the PUT's ETag is the one a read of that body answers with.
        var replaced = await api.SendRawAsync(
            HttpMethod.Put, api.Document(id), $$$"""{"z":[2,1],"id":"{{{id}}}","a":{"y":1,"b":"{{{name}}}"}}""", "application/json");
        var read = await api.GetAsync(id);

        await Assert.That(replaced.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(read.ETag).IsEqualTo(replaced.ETag);
        await Assert.That(read.Json.GetProperty("document").GetRawText()).IsEqualTo($$$"""{"a":{"y":1,"b":"{{{name}}}"},"z":[2,1]}""");
    }

    [Test]
    public async Task Requests_need_a_key_with_the_right_scope()
    {
        await using var api = await DocumentApi.StartAsync();
        using var anonymous = await api.ClientWithScopesAsync("documents:read");
        anonymous.DefaultRequestHeaders.Authorization = null;
        using var unknown = await api.ClientWithScopesAsync("documents:read");
        unknown.DefaultRequestHeaders.Authorization = new("Bearer", "tb_not-a-real-key");
        using var reader = await api.ClientWithScopesAsync("documents:read");

        var noKey = await DocumentApi.SendAsync(anonymous, HttpMethod.Get, api.Documents);
        var badKey = await DocumentApi.SendAsync(unknown, HttpMethod.Get, api.Documents);
        var readOnlyWrite = await DocumentApi.SendAsync(reader, HttpMethod.Put, api.Document(DocumentApi.NewId()), JsonContent.Create(new { a = 1 }));
        var readOnlyRead = await DocumentApi.SendAsync(reader, HttpMethod.Get, api.Documents);

        await Assert.That((noKey.Status, noKey.Code)).IsEqualTo((HttpStatusCode.Unauthorized, "invalid_key"));
        await Assert.That((badKey.Status, badKey.Code)).IsEqualTo((HttpStatusCode.Unauthorized, "invalid_key"));
        await Assert.That((readOnlyWrite.Status, readOnlyWrite.Code)).IsEqualTo((HttpStatusCode.Forbidden, "forbidden_scope"));
        await Assert.That(readOnlyRead.Status).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Admin_endpoints_stay_internal_and_the_operator_reaches_client_routes_there()
    {
        await using var api = await DocumentApi.StartAsync();
        var client = api.Client;

        using var documentsOnAdmin = await api.Server.Client.PutAsync(new Uri(api.DatabasePath("x"), UriKind.Relative), null);
        using var keysOnClient = await client.PostAsJsonAsync(
            new Uri("/admin/api-keys", UriKind.Relative), new { name = "escalate", scopes = WriteScope, organizationId = api.Tenant.OrganizationId });
        // Health probes are the only administrative routes the client listener also serves.
        using var metricsOnClient = await client.GetAsync(new Uri("/metrics", UriKind.Relative));

        // The internal listener serves the client API too, as the platform operator when nothing is presented.
        await Assert.That(documentsOnAdmin.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(keysOnClient.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(metricsOnClient.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Every_document_route_is_in_the_openapi_document()
    {
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var client = tls.CreateClient(server.ClientAddress!);

        using var openApi = JsonDocument.Parse(await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative)));
        var operations = openApi.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject().Select(operation => operation.Value.GetProperty("operationId").GetString()))
            .ToHashSet();

        string[] expected =
        [
            "createDatabase", "createCollection", "listCollections", "getCollection", "dropCollection", "getDocument", "putDocument",
            "createDocument", "patchDocument", "deleteDocument", "listDocuments", "queryDocuments", "runTransaction",
        ];
        await Assert.That(expected.Where(operation => !operations.Contains(operation))).IsEmpty();
        var paths = openApi.RootElement.GetProperty("paths");
        var query = paths.GetProperty("/v1/projects/{project}/databases/{database}/collections/{collection}/query").GetProperty("post");
        var patch = paths.GetProperty("/v1/projects/{project}/databases/{database}/collections/{collection}/documents/by-key").GetProperty("patch");
        await Assert.That(query.GetProperty("requestBody").GetProperty("content").TryGetProperty("application/json", out _)).IsTrue();
        await Assert.That(patch.GetProperty("requestBody").GetProperty("content").EnumerateObject().Select(media => media.Name))
            .IsEquivalentTo(["application/merge-patch+json", "application/vnd.terrabase.ops+json"]);

        // The served contract must describe the size/media refusals exercised over HTTP by the document tests.
        var byId = paths.EnumerateObject().SelectMany(path => path.Value.EnumerateObject())
            .ToDictionary(operation => operation.Value.GetProperty("operationId").GetString()!, operation => operation.Value);
        var indexDescription = byId["listIndexes"].GetProperty("description").GetString();
        await Assert.That(indexDescription).Contains("state building");
        await Assert.That(indexDescription).Contains("then active");
        foreach (var operationId in new[]
        {
            "createCollection", "createDocument", "putDocument", "patchDocument", "queryDocuments", "runTransaction",
            "putDeclaredCollectionSchema", "previewCollectionSchema", "putCollectionTtl", "putCollectionRules",
        })
        {
            foreach (var status in new[] { "413", "415" })
            {
                var responses = byId[operationId].GetProperty("responses");
                await Assert.That(responses.TryGetProperty(status, out _)).IsTrue().Because($"{operationId} can return {status}");
                await Assert.That(responses.GetProperty(status).GetProperty("content").TryGetProperty("application/json", out _)).IsTrue();
            }
        }

        var read = byId["getDocument"];
        var readResponses = read.GetProperty("responses");
        await Assert.That(readResponses.TryGetProperty("412", out _)).IsTrue();
        await Assert.That(readResponses.GetProperty("412").GetProperty("content").TryGetProperty("application/json", out _)).IsTrue();
        await Assert.That(readResponses.TryGetProperty("304", out var notModified)).IsTrue();
        await Assert.That(notModified.TryGetProperty("content", out _)).IsFalse();
        var headers = read.GetProperty("parameters").EnumerateArray().Where(parameter => parameter.GetProperty("in").GetString() == "header")
            .ToDictionary(parameter => parameter.GetProperty("name").GetString()!, parameter => parameter);
        foreach (var name in new[] { "If-Match", "If-None-Match" })
        {
            await Assert.That(headers.ContainsKey(name)).IsTrue();
            await Assert.That(headers[name].TryGetProperty("required", out var required) && required.GetBoolean()).IsFalse();
            await Assert.That(headers[name].GetProperty("schema").GetProperty("type").GetString()).IsEqualTo("string");
        }
    }

    private static long Offset(ApiResponse response) =>
        long.Parse(response.Json.GetProperty("offset").GetString()!, NumberStyles.None, CultureInfo.InvariantCulture);

}
