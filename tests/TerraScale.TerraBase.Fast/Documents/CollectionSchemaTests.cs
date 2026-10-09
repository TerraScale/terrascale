using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Hosting;
using TerraBase.Testing;

namespace TerraBase.Fast.Documents;

public sealed class CollectionSchemaTests
{
    private const string Dialect = "https://json-schema.org/draft/2020-12/schema";
    private const string NameSchema = """{"type":"object","required":["name"],"properties":{"name":{"type":"string"},"count":{"type":"integer","minimum":0}}}""";

    [Test]
    public async Task Canceling_a_stock_keyword_evaluation_rolls_back_and_releases_the_writer()
    {
        using var cancellation = new CancellationTokenSource();
        var requests = new CanceledSchemaRequest(cancellation.Token);
        await using var api = await DocumentApi.StartAsync(services: services => services.AddSingleton<IStartupFilter>(requests));
        const string Schema = """{"properties":{"values":{"contains":{"allOf":[{"minimum":0},{"multipleOf":1}]}}}}""";
        await AttachedAsync(await api.SendRawAsync(HttpMethod.Put, SchemaPath(api), Schema, "application/json"), Schema);
        var id = DocumentApi.NewId();
        int[] originalValues = [1];
        var original = await api.PutAsync(id, new { values = originalValues });
        original.Status.ShouldBe(HttpStatusCode.Created);

        var meter = api.Server.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
        long minimumEvaluations = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (instrument.Name == "terrabase.schema.keyword_evaluations" && ReferenceEquals(instrument.Meter, meter))
            {
                observer.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "keyword" && Equals(tag.Value, "minimum") && Interlocked.Add(ref minimumEvaluations, value) == 3)
                {
                    cancellation.Cancel();
                }
            }
        });
        listener.Start();
        int[] replacementValues = [2, 3, 4, 5, 6, 7, 8, 9];
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(api.Document(id), UriKind.Relative))
        {
            Content = JsonContent.Create(new { values = replacementValues }),
        };
        request.Headers.Add(CanceledSchemaRequest.Header, "true");
        var responseDescription = "transport aborted";
        try
        {
            using var response = await api.Client.SendAsync(request, TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            responseDescription = $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}";
            await Assert.That(response.IsSuccessStatusCode).IsFalse();
        }
        catch (HttpRequestException)
        {
            // Kestrel may abort the transport when the request's cancellation escapes the pipeline.
        }

        await Assert.That(Interlocked.Read(ref minimumEvaluations)).IsEqualTo(3).Because(responseDescription);
        await Assert.That(await requests.Finished.Task.WaitAsync(TimeSpan.FromSeconds(30))).IsTrue().Because(responseDescription);
        listener.Dispose();

        var unchanged = await api.GetAsync(id);
        await Assert.That(unchanged.Version).IsEqualTo(original.Version);
        await Assert.That(unchanged.Json.GetProperty("document").GetProperty("values")[0].GetInt32()).IsEqualTo(1);
        var written = await api.PutAsync(id, new { values = replacementValues });
        written.Status.ShouldBe(HttpStatusCode.OK);
        var replaced = await api.GetAsync(id);
        await Assert.That(replaced.Json.GetProperty("document").GetProperty("values").GetArrayLength()).IsEqualTo(replacementValues.Length);
        await Assert.That(Offset(written)).IsEqualTo(Offset(original) + 1);
    }

    [Test]
    [Arguments("""{"then":{"required":["yes"]},"else":{"required":["no"]},"if":{"properties":{"mode":{"const":true}},"required":["mode"]}}""", """{"mode":true,"yes":true}""", """{"mode":true}""")]
    [Arguments("""{"then":{"required":["yes"]},"else":{"required":["no"]},"if":{"properties":{"mode":{"const":true}},"required":["mode"]}}""", """{"mode":false,"no":true}""", """{"mode":false}""")]
    [Arguments("""{"additionalProperties":false,"properties":{"kept":true}}""", """{"kept":1}""", """{"kept":1,"extra":2}""")]
    [Arguments("""{"unevaluatedProperties":false,"allOf":[{"properties":{"kept":true}}]}""", """{"kept":1}""", """{"kept":1,"extra":2}""")]
    [Arguments("""{"properties":{"values":{"unevaluatedItems":false,"prefixItems":[true]}}}""", """{"values":[1]}""", """{"values":[1,2]}""")]
    [Arguments("""{"properties":{"values":{"unevaluatedItems":false,"contains":{"minimum":0}}}}""", """{"values":[1,2]}""", """{"values":[1,-1]}""")]
    public async Task Keyword_dependencies_and_evaluated_annotations_ignore_member_order(string schema, string valid, string invalid)
    {
        await using var api = await DocumentApi.StartAsync();
        await AttachedAsync(await api.SendRawAsync(HttpMethod.Put, SchemaPath(api), schema, "application/json"), schema);
        var id = DocumentApi.NewId();
        var original = await api.SendRawAsync(HttpMethod.Put, api.Document(id), valid, "application/json");
        original.Status.ShouldBe(HttpStatusCode.Created);
        await ValidationFailureAsync(await api.SendRawAsync(HttpMethod.Put, api.Document(id), invalid, "application/json"));
        var unchanged = await api.GetAsync(id);
        await Assert.That(unchanged.Version).IsEqualTo(original.Version);
        using var expected = JsonDocument.Parse(valid);
        await Assert.That(JsonElement.DeepEquals(unchanged.Json.GetProperty("document"), expected.RootElement)).IsTrue();
    }

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
        await using var api = await DocumentApi.StartAsync();
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
        await Assert.That((await api.SendAsync(HttpMethod.Get, api.Documents)).Ids).IsEquivalentTo([id, putId]);
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
    public async Task Validation_details_escape_nested_json_pointers_without_exposing_values_and_false_rejects_writes()
    {
        await using var api = await DocumentApi.StartAsync();
        const string NestedSchema = """{"type":"object","properties":{"a/b":{"type":"object","properties":{"~secret":{"type":"array","items":{"type":"integer"}}}}}}""";
        await AttachedAsync(await api.SendRawAsync(HttpMethod.Put, SchemaPath(api), NestedSchema, "application/json"), NestedSchema);
        var secret = "private-" + new Faker().Random.AlphaNumeric(24);
        var rejected = await api.SendAsync(HttpMethod.Post, api.Documents, new Dictionary<string, object>
        {
            ["a/b"] = new Dictionary<string, object> { ["~secret"] = new[] { secret } },
        });
        await ValidationFailureAsync(rejected);
        var details = rejected.Json.GetProperty("error").GetProperty("details").EnumerateArray().ToArray();
        await Assert.That(details.Any(detail => detail.GetProperty("path").GetString() == "/a~1b/~0secret/0"
            && detail.GetProperty("keyword").GetString() == "type")).IsTrue();
        await Assert.That(rejected.Json.GetRawText().Contains(secret, StringComparison.Ordinal)).IsFalse();

        await AttachedAsync(await api.SendRawAsync(HttpMethod.Put, SchemaPath(api), "true", "application/json"), "true");
        await AttachedAsync(await api.SendAsync(HttpMethod.Get, SchemaPath(api)), "true");
        var id = DocumentApi.NewId();
        var original = await api.PutAsync(id, new { arbitrary = true });
        original.Status.ShouldBe(HttpStatusCode.Created);
        await AttachedAsync(await api.SendRawAsync(HttpMethod.Put, SchemaPath(api), "false", "application/json"), "false");
        await AttachedAsync(await api.SendAsync(HttpMethod.Get, SchemaPath(api)), "false");
        var falseResult = await api.PutAsync(id, new { arbitrary = false });
        await ValidationFailureAsync(falseResult);
        await Assert.That(falseResult.Json.GetProperty("error").GetProperty("details").EnumerateArray()
            .Any(detail => detail.GetProperty("path").GetString() == "")).IsTrue();
        await ValidationFailureAsync(await api.SendAsync(HttpMethod.Post, api.Documents, new { }));
        await Assert.That((await api.GetAsync(id)).Version).IsEqualTo(original.Version);
        (await api.SendAsync(HttpMethod.Delete, api.Document(id))).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task False_items_after_a_prefix_reports_the_first_disallowed_index_without_rejecting_the_prefix()
    {
        await using var api = await DocumentApi.StartAsync();
        const string Schema = """{"properties":{"a":{"type":"array","prefixItems":[true],"items":false}}}""";
        await AttachedAsync(await api.SendRawAsync(HttpMethod.Put, SchemaPath(api), Schema, "application/json"), Schema);
        var id = DocumentApi.NewId();
        var original = await api.SendRawAsync(HttpMethod.Put, api.Document(id), """{"a":[0]}""", "application/json");
        original.Status.ShouldBe(HttpStatusCode.Created);

        var rejected = await api.SendRawAsync(HttpMethod.Put, api.Document(id), """{"a":[0,1]}""", "application/json");
        await ValidationFailureAsync(rejected);
        var details = rejected.Json.GetProperty("error").GetProperty("details").EnumerateArray().ToArray();
        await Assert.That(details.Any(detail => detail.GetProperty("path").GetString() == "/a/1")).IsTrue();
        await Assert.That(details.Any(detail => detail.GetProperty("path").GetString() == "/a/0")).IsFalse();
        var unchanged = await api.GetAsync(id);
        await Assert.That(unchanged.Version).IsEqualTo(original.Version);
        await Assert.That(unchanged.Json.GetProperty("document").GetProperty("a").GetArrayLength()).IsEqualTo(1);
    }

    [Test]
    public async Task Many_invalid_array_items_bound_failure_count_and_total_utf8_pointer_bytes()
    {
        await using var api = await DocumentApi.StartAsync(settings => settings["memory-budget"] = "16777216");
        string[] propertyNames = ["a", new string('é', 600)];
        foreach (var propertyName in propertyNames)
        {
            var invalidItems = Enumerable.Repeat("wrong", propertyName == "a" ? 64 : 32).ToArray();
            var schema = $$"""{"properties":{"{{propertyName}}":{"type":"array","items":{"type":"integer"} } } }""";
            await AttachedAsync(await api.SendRawAsync(HttpMethod.Put, SchemaPath(api), schema, "application/json"), schema);
            var id = DocumentApi.NewId();
            var rejected = await api.PutAsync(id, new Dictionary<string, object> { [propertyName] = invalidItems });
            await ValidationFailureAsync(rejected);
            var details = rejected.Json.GetProperty("error").GetProperty("details").EnumerateArray().ToArray();
            await Assert.That(details.Length).IsLessThanOrEqualTo(16);
            await Assert.That(details.Sum(detail => Encoding.UTF8.GetByteCount(detail.GetProperty("path").GetString()!)))
                .IsLessThanOrEqualTo(8192);
            await Assert.That(details.Any(detail => detail.GetProperty("keyword").GetString() == "type"
                && detail.GetProperty("path").GetString()!.StartsWith($"/{propertyName}/", StringComparison.Ordinal))).IsTrue();
            if (propertyName == "a")
            {
                await Assert.That(details.Length).IsEqualTo(16);
            }
            else
            {
                // Multibyte pointers exhaust the byte budget before the sixteen-detail limit.
                await Assert.That(details.Length).IsLessThan(16);
            }

            await Assert.That((await api.GetAsync(id)).Status).IsEqualTo(HttpStatusCode.NotFound);
        }
    }

    [Test]
    public async Task Invalid_schemas_and_schemas_over_64_kib_preserve_the_previous_schema()
    {
        await using var api = await DocumentApi.StartAsync();
        var schema = SchemaPath(api);
        await AttachedAsync(await api.SendRawAsync(HttpMethod.Put, schema, NameSchema, "application/json"), NameSchema);
        foreach (var invalid in new[] { "{", "null", "[]", "\"string\"", """{"type":"not-a-json-type"}""", """{"required":"name"}""", """{"properties":{"name":42}}""" })
        {
            var rejected = await api.SendRawAsync(HttpMethod.Put, schema, invalid, "application/json");
            await Assert.That((rejected.Status, rejected.Code)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_schema"));
            await AttachedAsync(await api.SendAsync(HttpMethod.Get, schema), NameSchema);
        }

        // A multibyte comment makes the limit observable in UTF-8 bytes, rather than characters.
        const string EmptyComment = """{"$comment":""}""";
        var remaining = (64 * 1024) - Encoding.UTF8.GetByteCount(EmptyComment);
        var comment = new string('é', remaining / 2) + new string('x', remaining % 2);
        var atLimit = $$"""{"$comment":"{{comment}}"}""";
        await Assert.That(Encoding.UTF8.GetByteCount(atLimit)).IsEqualTo(64 * 1024);
        await AttachedAsync(await api.SendRawAsync(HttpMethod.Put, schema, atLimit, "application/json"), atLimit);
        var tooLarge = await api.SendRawAsync(HttpMethod.Put, schema, $$"""{"$comment":"{{comment}}é"}""", "application/json",
            request => request.Headers.ExpectContinue = true);
        await Assert.That((tooLarge.Status, tooLarge.Code)).IsEqualTo((HttpStatusCode.RequestEntityTooLarge, "payload_too_large"));
        await AttachedAsync(await api.SendAsync(HttpMethod.Get, schema), atLimit);
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
            using var adminRequest = new HttpRequestMessage(method, new Uri(schema, UriKind.Relative))
            {
                Content = method == HttpMethod.Put ? JsonContent.Create(true) : null,
            };
            adminRequest.Headers.Authorization = api.Client.DefaultRequestHeaders.Authorization;
            using var onAdmin = await api.Server.Client.SendAsync(adminRequest);
            await Assert.That(onAdmin.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
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
        $"/projects/{api.Tenant.ProjectId}/databases/{DocumentApi.Database}/collections/{DocumentApi.Collection}/schema";

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

    private sealed class CanceledSchemaRequest(CancellationToken cancellation) : IStartupFilter
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
