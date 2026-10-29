using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Hosting;
using TerraBase.Testing;

namespace TerraBase.Fast.Documents;

public sealed class CollectionSchemaTests
{
    private const string Dialect = "https://json-schema.org/draft/2020-12/schema";
    private const string NameSchema = """{"type":"object","required":["name"],"properties":{"name":{"type":"string"},"count":{"type":"integer","minimum":0}}}""";

    [Test]
    public async Task Collections_are_schemaless_until_a_schema_is_attached_replaced_or_detached()
    {
        await using var api = await DocumentApi.StartAsync();
        var schema = SchemaPath(api);
        await DetachedAsync(await api.SendAsync(HttpMethod.Get, schema));
        var id = DocumentApi.NewId();
        (await api.PutAsync(id, new { arbitrary = true })).Status.ShouldBe(HttpStatusCode.Created);

        await AttachedAsync(await api.SendRawAsync(HttpMethod.Put, schema, NameSchema, "application/json"), NameSchema);
        await AttachedAsync(await api.SendAsync(HttpMethod.Get, schema), NameSchema);
        await ValidationFailureAsync(await api.PutAsync(DocumentApi.NewId(), new { arbitrary = true }));

        const string Replacement = """{"type":"object","required":["count"],"properties":{"count":{"type":"integer"}}}""";
        await AttachedAsync(await api.SendRawAsync(HttpMethod.Put, schema, Replacement, "application/json"), Replacement);
        await AttachedAsync(await api.SendAsync(HttpMethod.Get, schema), Replacement);
        (await api.PutAsync(id, new { count = 2 })).Status.ShouldBe(HttpStatusCode.OK);
        await ValidationFailureAsync(await api.PutAsync(id, new { name = "missing-count" }));

        await DetachedAsync(await api.SendAsync(HttpMethod.Delete, schema));
        await DetachedAsync(await api.SendAsync(HttpMethod.Get, schema));
        int[] arbitrary = [1, 2];
        (await api.PutAsync(id, new { arbitrary })).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task Post_put_and_both_patch_formats_validate_the_complete_document_without_failed_writes()
    {
        await using var api = await DocumentApi.StartAsync(services: services =>
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, new FakeTimeProvider()));
        await AttachedAsync(await api.SendRawAsync(HttpMethod.Put, SchemaPath(api), NameSchema, "application/json"), NameSchema);
        var name = new Faker().Commerce.ProductName();
        var created = await api.SendAsync(HttpMethod.Post, api.Documents, new { name, count = 1 });
        created.Status.ShouldBe(HttpStatusCode.Created);
        var id = created.Json.GetProperty("id").GetString()!;
        var replaced = await api.PutAsync(id, new { name, count = 2 });
        replaced.Status.ShouldBe(HttpStatusCode.OK);
        var putId = DocumentApi.NewId();
        (await api.PutAsync(putId, new { name, count = 1 })).Status.ShouldBe(HttpStatusCode.Created);

        // Neither patch carries the required name: validation must see the merged result.
        var merged = await api.SendRawAsync(HttpMethod.Patch, api.Document(id), """{"count":3}""", "application/merge-patch+json");
        merged.Status.ShouldBe(HttpStatusCode.OK);
        var patched = await api.SendRawAsync(HttpMethod.Patch, api.Document(id),
            """{"ops":[{"op":"inc","path":"/count","by":1}]}""", "application/vnd.terrabase.ops+json");
        patched.Status.ShouldBe(HttpStatusCode.OK);
        var before = await api.GetAsync(id);
        await Assert.That(before.Json.GetProperty("document").GetProperty("name").GetString()).IsEqualTo(name);
        await Assert.That(before.Json.GetProperty("document").GetProperty("count").GetInt32()).IsEqualTo(4);

        var failedId = DocumentApi.NewId();
        ApiResponse[] failures =
        [
            await api.SendAsync(HttpMethod.Post, api.Documents, new { count = 1 }),
            await api.PutAsync(failedId, new { count = 1 }),
            await api.PutAsync(id, new { count = 1 }),
            await api.SendRawAsync(HttpMethod.Patch, api.Document(id), """{"name":null}""", "application/merge-patch+json"),
            await api.SendRawAsync(HttpMethod.Patch, api.Document(id),
                """{"ops":[{"op":"unset","path":"/name"}]}""", "application/vnd.terrabase.ops+json"),
            await api.SendRawAsync(HttpMethod.Patch, api.Document(id), """{"count":-1}""", "application/merge-patch+json"),
            await api.SendRawAsync(HttpMethod.Patch, api.Document(id),
                """{"ops":[{"op":"set","path":"/count","value":-1}]}""", "application/vnd.terrabase.ops+json"),
        ];
        foreach (var failure in failures)
        {
            await ValidationFailureAsync(failure);
        }

        var unchanged = await api.GetAsync(id);
        await Assert.That(unchanged.Version).IsEqualTo(before.Version);
        await Assert.That(JsonElement.DeepEquals(unchanged.Json.GetProperty("document"), before.Json.GetProperty("document"))).IsTrue();
        await Assert.That((await api.GetAsync(failedId)).Status).IsEqualTo(HttpStatusCode.NotFound);
        using var listed = await api.Client.GetAsync(new Uri(api.Documents, UriKind.Relative));
        var text = await listed.Content.ReadAsStringAsync();
        await Assert.That(listed.StatusCode).IsEqualTo(HttpStatusCode.OK)
            .Because($"GET {api.Documents}: HTTP {(int)listed.StatusCode}: {text[..Math.Min(text.Length, 4096)]}");
        using var page = JsonDocument.Parse(text);
        await Assert.That(page.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!))
            .IsEquivalentTo([id, putId]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Schema_failures_name_the_transaction_operation_and_roll_back_every_write_and_offset(bool batchPuts)
    {
        await using var api = await DocumentApi.StartAsync();
        await AttachedAsync(await api.SendRawAsync(HttpMethod.Put, SchemaPath(api), NameSchema, "application/json"), NameSchema);
        var id = DocumentApi.NewId();
        var name = new Faker().Commerce.ProductName();
        var original = await api.PutAsync(id, new { name, count = 1 });
        original.Status.ShouldBe(HttpStatusCode.Created);
        object[] invalidOperations =
        [
            new { op = "create", collection = "items", id = DocumentApi.NewId(), document = new { count = 2 } },
            new { op = "put", collection = "items", id, document = new { count = 2 } },
            new { op = "patch", collection = "items", id, merge = new { name = (string?)null } },
            new { op = "patch", collection = "items", id, ops = new[] { new { op = "unset", path = "/name" } } },
        ];
        foreach (var invalid in invalidOperations)
        {
            var fresh = DocumentApi.NewId();
            var result = await api.TransactAsync(
                new { op = batchPuts ? "put" : "create", collection = "items", id = fresh, document = new { name, count = 0 } }, invalid);
            await ValidationFailureAsync(result, 1);
            await Assert.That((await api.GetAsync(fresh)).Status).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That((await api.GetAsync(id)).Version).IsEqualTo(original.Version);
        }

        var unchanged = await api.GetAsync(id);
        await Assert.That(unchanged.Json.GetProperty("document").GetProperty("name").GetString()).IsEqualTo(name);
        await Assert.That(unchanged.Json.GetProperty("document").GetProperty("count").GetInt32()).IsEqualTo(1);

        var next = await api.PutAsync(DocumentApi.NewId(), new { name, count = 0 });
        next.Status.ShouldBe(HttpStatusCode.Created);
        await Assert.That(Offset(next)).IsEqualTo(Offset(original) + 1);
        var merged = await api.TransactAsync(new { op = "patch", collection = "items", id, merge = new { count = 2 } });
        merged.Status.ShouldBe(HttpStatusCode.OK);
        var patched = await api.TransactAsync(new
        {
            op = "patch",
            collection = "items",
            id,
            ops = new[] { new { op = "inc", path = "/count", by = 1 } },
        });
        patched.Status.ShouldBe(HttpStatusCode.OK);
        var read = await api.GetAsync(id);
        await Assert.That(read.Json.GetProperty("document").GetProperty("name").GetString()).IsEqualTo(name);
        await Assert.That(read.Json.GetProperty("document").GetProperty("count").GetInt32()).IsEqualTo(3);
    }

    [Test]
    public async Task Schema_routes_enforce_scopes_client_listener_and_existing_database_and_collection()
    {
        await using var api = await DocumentApi.StartAsync();
        var schema = SchemaPath(api);
        await AttachedAsync(await api.SendRawAsync(HttpMethod.Put, schema, NameSchema, "application/json"), NameSchema);
        using var reader = await api.ClientWithScopesAsync("documents:read");
        using var writer = await api.ClientWithScopesAsync("documents:write");
        using var anonymous = api.Management.DataClient();
        var read = await DocumentApi.SendAsync(reader, HttpMethod.Get, schema);
        await AttachedAsync(read, NameSchema);
        ApiResponse[] denied =
        [
            await DocumentApi.SendAsync(reader, HttpMethod.Put, schema, JsonContent.Create(false)),
            await DocumentApi.SendAsync(reader, HttpMethod.Delete, schema),
            await DocumentApi.SendAsync(writer, HttpMethod.Get, schema),
        ];
        foreach (var result in denied)
        {
            await Assert.That((result.Status, result.Code)).IsEqualTo((HttpStatusCode.Forbidden, "forbidden_scope"));
        }

        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Put, HttpMethod.Delete })
        {
            var unauthenticated = await DocumentApi.SendAsync(anonymous, method, schema, method == HttpMethod.Put ? JsonContent.Create(true) : null);
            await Assert.That(unauthenticated.Status).IsEqualTo(HttpStatusCode.Unauthorized);
            if (method == HttpMethod.Get)
            {
                // The internal listener serves the client API too; an API key reaches it as on the public listener.
                using var adminRequest = new HttpRequestMessage(method, new Uri(schema, UriKind.Relative));
                adminRequest.Headers.Authorization = api.Client.DefaultRequestHeaders.Authorization;
                using var onAdmin = await api.Server.Client.SendAsync(adminRequest);
                await Assert.That(onAdmin.IsSuccessStatusCode).IsTrue();
            }
            foreach (var missing in new[]
            {
                schema.Replace("/collections/items/", "/collections/missing/", StringComparison.Ordinal),
                schema.Replace("/databases/db/", "/databases/missing/", StringComparison.Ordinal),
            })
            {
                var result = await api.SendAsync(method, missing, method == HttpMethod.Put ? (object)true : null);
                await Assert.That(result.Status).IsEqualTo(HttpStatusCode.NotFound);
            }
        }

        await AttachedAsync(await api.SendAsync(HttpMethod.Get, schema), NameSchema);
        await AttachedAsync(await DocumentApi.SendAsync(writer, HttpMethod.Put, schema, JsonContent.Create(true)), "true");
        await DetachedAsync(await DocumentApi.SendAsync(writer, HttpMethod.Delete, schema));
    }

    [Test]
    public async Task Schemas_persist_across_restart_and_drop_recreate_removes_them()
    {
        var dataDirectory = Directory.CreateTempSubdirectory("terrabase-collection-schema-").FullName;
        try
        {
            string organization, schema, collection, collections, document;
            await using (var api = await DocumentApi.StartAsync(settings => settings["data-directory"] = dataDirectory))
            {
                organization = api.Tenant.OrganizationId;
                schema = SchemaPath(api);
                collections = api.Collections;
                collection = $"{collections}/items";
                document = api.Document(DocumentApi.NewId());
                await AttachedAsync(await api.SendRawAsync(HttpMethod.Put, schema, NameSchema, "application/json"), NameSchema);
            }

            await using var reopened = await ManagementApi.StartAsync(settings => settings["data-directory"] = dataDirectory);
            using var client = reopened.DataClient(await reopened.Server.IssueAsync(organization, "documents:read", "documents:write"));
            await AttachedAsync(await DocumentApi.SendAsync(client, HttpMethod.Get, schema), NameSchema);
            await ValidationFailureAsync(await DocumentApi.SendAsync(client, HttpMethod.Put, document, JsonContent.Create(new { count = 1 })));
            (await DocumentApi.SendAsync(client, HttpMethod.Put, document,
                JsonContent.Create(new { name = new Faker().Commerce.ProductName(), count = 1 }))).Status.ShouldBe(HttpStatusCode.Created);

            (await DocumentApi.SendAsync(client, HttpMethod.Delete, collection)).Status.ShouldBe(HttpStatusCode.OK);
            await Assert.That((await DocumentApi.SendAsync(client, HttpMethod.Get, schema)).Status).IsEqualTo(HttpStatusCode.NotFound);
            (await DocumentApi.SendAsync(client, HttpMethod.Post, collections, JsonContent.Create(new { name = "items" }))).Status.ShouldBe(HttpStatusCode.Created);
            await DetachedAsync(await DocumentApi.SendAsync(client, HttpMethod.Get, schema));
            await Assert.That((await DocumentApi.SendAsync(client, HttpMethod.Get, document)).Status).IsEqualTo(HttpStatusCode.NotFound);
            (await DocumentApi.SendAsync(client, HttpMethod.Put, document, JsonContent.Create(new { arbitrary = true }))).Status.ShouldBe(HttpStatusCode.Created);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private static string SchemaPath(DocumentApi api) =>
        $"/v1/projects/{api.Tenant.ProjectId}/databases/{DocumentApi.Database}/collections/{DocumentApi.Collection}/schema";

    private static async Task AttachedAsync(ApiResponse response, string schema)
    {
        await Assert.That(response.Status).IsEqualTo(HttpStatusCode.OK);
        using var expected = JsonDocument.Parse(schema);
        await Assert.That(JsonElement.DeepEquals(response.Json.GetProperty("schema"), expected.RootElement)).IsTrue();
        await Assert.That(response.Json.GetProperty("dialect").GetString()).IsEqualTo(Dialect);
    }

    private static async Task DetachedAsync(ApiResponse response)
    {
        await Assert.That(response.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Json.GetProperty("schema").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    private static async Task ValidationFailureAsync(ApiResponse response, int? op = null)
    {
        await Assert.That((response.Status, response.Code, response.Op)).IsEqualTo((HttpStatusCode.BadRequest, "schema_validation_failed", op));
        await Assert.That(string.IsNullOrWhiteSpace(response.Message)).IsFalse();
        var details = response.Json.GetProperty("error").GetProperty("details");
        await Assert.That(details.ValueKind).IsEqualTo(JsonValueKind.Array);
        await Assert.That(details.GetArrayLength()).IsGreaterThan(0);
        foreach (var detail in details.EnumerateArray())
        {
            await Assert.That(detail.EnumerateObject().Select(property => property.Name)).IsEquivalentTo(["path", "keyword"]);
            await Assert.That(detail.GetProperty("path").ValueKind).IsEqualTo(JsonValueKind.String);
            await Assert.That(string.IsNullOrWhiteSpace(detail.GetProperty("keyword").GetString())).IsFalse();
        }
    }

    private static long Offset(ApiResponse response) =>
        long.Parse(response.Json.GetProperty("offset").GetString()!, CultureInfo.InvariantCulture);

    internal sealed class CanceledSchemaRequest(CancellationToken cancellation) : IStartupFilter
    {
        public const string Header = "X-Cancel-Schema-Evaluation";

        public TaskCompletionSource<bool> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, following) =>
            {
                if (!context.Request.Headers.ContainsKey(Header))
                {
                    await following(context);
                    return;
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, cancellation);
                context.RequestAborted = linked.Token;
                try
                {
                    await following(context);
                }
                finally
                {
                    // Development exception handling may translate the cancellation into HTTP 499.
                    Finished.TrySetResult(linked.IsCancellationRequested);
                }
            });
            next(app);
        };
    }
}
