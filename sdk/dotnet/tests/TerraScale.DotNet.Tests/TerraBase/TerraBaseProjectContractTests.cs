using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseProjectContractTests
{
    [Test]
    [Arguments("provenance.json", "8db4c792142dcd6644e1d14a2b2efd6bc04a107a")]
    [Arguments("pending-r8.json", "2b95f5ef0641d3a8b8695ec1989e02943befbef2")]
    public async Task Exported_sources_match_the_commit_stamped_producer_artifact(string manifestName, string commit)
    {
        using var manifest = JsonDocument.Parse(TerraBaseIntegrationFixture.ReadContract(manifestName));
        await Assert.That(manifest.RootElement.GetProperty("commit").GetString()).IsEqualTo(commit);
        var artifact = manifest.RootElement.GetProperty("artifact");
        await Assert.That(artifact.GetProperty("commit").GetString()).IsEqualTo(commit);
        var archive = TerraBaseIntegrationFixture.ReadContractBytes(artifact.GetProperty("path").GetString()!);
        await Assert.That(Convert.ToHexStringLower(SHA256.HashData(archive))).IsEqualTo(artifact.GetProperty("sha256").GetString());
        using var compressed = new MemoryStream(archive);
        using var decompressed = new GZipStream(compressed, CompressionMode.Decompress);
        using var reader = new TarReader(decompressed);
        var producerFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        string? producerCommit = null;
        while (await reader.GetNextEntryAsync() is { } entry)
        {
            if (entry is PaxGlobalExtendedAttributesTarEntry attributes)
                producerCommit = attributes.GlobalExtendedAttributes["comment"];
            if (entry.DataStream is not { } data) continue;
            using var bytes = new MemoryStream();
            await data.CopyToAsync(bytes);
            producerFiles.Add(entry.Name, bytes.ToArray());
        }
        await Assert.That(producerCommit).IsEqualTo(commit);
        await Assert.That(producerFiles.Count).IsEqualTo(manifest.RootElement.GetProperty("sources").GetArrayLength());
        foreach (var source in manifest.RootElement.GetProperty("sources").EnumerateArray())
        {
            var exported = TerraBaseIntegrationFixture.ReadContractBytes(source.GetProperty("export").GetString()!);
            var producer = producerFiles[source.GetProperty("path").GetString()!];
            await Assert.That(exported.SequenceEqual(producer)).IsTrue();
            await Assert.That(Convert.ToHexStringLower(SHA256.HashData(producer))).IsEqualTo(source.GetProperty("sha256").GetString());
        }
    }

    [Test]
    public async Task Canonical_lifecycle_uses_project_authority_database_names_and_placement()
    {
        var requests = new List<(string Method, string Path, string Query, string? Body)>();
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            await Assert.That(request.Headers.Authorization!.Scheme).IsEqualTo("Bearer");
            await Assert.That(request.Headers.Authorization.Parameter).IsEqualTo("project-user-token");
            await Assert.That(request.RequestUri!.Host).IsEqualTo("api.terrascale.tech");
            await Assert.That(request.Headers.GetValues("X-TerraScale-Region-Pin").Single()).IsEqualTo("sjc");
            await Assert.That(request.Headers.Contains("X-TerraBase-User-Id")).IsFalse();
            requests.Add((request.Method.Method, request.RequestUri.AbsolutePath, request.RequestUri.Query,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(token)));
            return request.Method == HttpMethod.Delete ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : TerraBaseIntegrationFixture.Json(TerraBaseIntegrationFixture.Read(request.RequestUri.AbsolutePath.EndsWith("/branches", StringComparison.Ordinal) ? "branch.json" : request.Method == HttpMethod.Get && request.RequestUri.Query.Length > 0 ? "databases.json" : "database.json"));
        });
        using var http = new HttpClient(handler);
        var client = TerraBaseIntegrationFixture.Client(http);
        var project = client.Project(TerraBaseIntegrationFixture.ProjectId);
        var created = await project.CreateDatabaseAsync("app", "gru", ["sjc", "fra"]);
        var page = await project.ListDatabasesAsync(after: created.Id, limit: 1);
        var found = await project.GetDatabaseAsync("app");
        await project.SetDatabaseRegionsAsync("app", []);
        await project.DeleteDatabaseAsync("app");
        var branch = await client.Database(TerraBaseIntegrationFixture.ProjectId, "app").BranchAsync("copy");

        await Assert.That(created.Region).IsEqualTo("sjc");
        await Assert.That(created.HomeRegion).IsEqualTo("gru");
        await Assert.That(created.ReplicaRegions).IsEquivalentTo(["sjc", "fra"]);
        await Assert.That(found.Id).IsEqualTo(created.Id);
        await Assert.That(page.Next).IsEqualTo(created.Id);
        await Assert.That(branch.BranchedFrom).IsEqualTo(created.Id);
        await Assert.That(branch.Name).IsEqualTo("copy");
        await Assert.That(requests.Select(r => r.Method)).IsEquivalentTo(["POST", "GET", "GET", "PATCH", "DELETE", "POST"]);
        await Assert.That(requests[2].Path).IsEqualTo($"/v1/projects/{TerraBaseIntegrationFixture.ProjectId}/databases/app");
        await Assert.That(requests[1].Query).IsEqualTo($"?after={created.Id}&limit=1");
        await Assert.That(requests[3].Body).IsEqualTo("{\"replicaRegions\":[]}");
        using var body = JsonDocument.Parse(requests[0].Body!);
        await Assert.That(body.RootElement.GetProperty("homeRegion").GetString()).IsEqualTo("gru");
        await Assert.That(body.RootElement.GetProperty("replicaRegions").GetArrayLength()).IsEqualTo(2);
        await Assert.That(requests[5].Body).IsEqualTo("{\"name\":\"copy\"}");
    }

    [Test]
    public async Task All_regions_placement_is_sent_on_create_and_patch_and_read_back()
    {
        const string allRegionsDatabase = """{"id":"01JA0000000000000000000004","projectId":"01JA0000000000000000000001","name":"app","branchedFrom":null,"createdAt":"2026-10-12T00:00:00+00:00","region":"sjc","homeRegion":"gru","replicaRegions":[],"allRegions":true}""";
        var requests = new List<(string Method, string? Body)>();
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            requests.Add((request.Method.Method, request.Content is null ? null : await request.Content.ReadAsStringAsync(token)));
            return TerraBaseIntegrationFixture.Json(allRegionsDatabase);
        });
        using var http = new HttpClient(handler);
        var project = TerraBaseIntegrationFixture.Client(http).Project(TerraBaseIntegrationFixture.ProjectId);
        var created = await project.CreateDatabaseAsync("app", "gru", allRegions: true);
        var placed = await project.SetDatabaseAllRegionsAsync("app");

        await Assert.That(created.AllRegions).IsTrue();
        await Assert.That(created.ReplicaRegions).IsEmpty();
        await Assert.That(placed.AllRegions).IsTrue();
        await Assert.That(requests.Select(r => r.Method)).IsEquivalentTo(["POST", "PATCH"]);
        await Assert.That(requests[0].Body).IsEqualTo("{\"name\":\"app\",\"homeRegion\":\"gru\",\"allRegions\":true}");
        await Assert.That(requests[1].Body).IsEqualTo("{\"allRegions\":true}");
    }

    [Test]
    public async Task Usage_preserves_int64_totals_periods_and_database_identity_without_manufacturing_billing()
    {
        var queries = new List<string>();
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            queries.Add(request.RequestUri!.Query);
            return Task.FromResult(TerraBaseIntegrationFixture.Json(TerraBaseIntegrationFixture.Read(
                request.RequestUri.AbsolutePath.Contains("/databases/", StringComparison.Ordinal) ? "database-usage.json" : "project-usage.json")));
        });
        using var http = new HttpClient(handler);
        var client = TerraBaseIntegrationFixture.Client(http);
        var from = DateTimeOffset.Parse("2026-10-12T00:00:00+03:00");
        var to = from.AddHours(1);
        var project = await client.Project(TerraBaseIntegrationFixture.ProjectId).GetUsageAsync("hour", from, to);
        var database = await client.Database(TerraBaseIntegrationFixture.ProjectId, "app").GetUsageAsync();

        await Assert.That(project.StorageBytes).IsEqualTo(9007199254740993L);
        await Assert.That(database.DatabaseId).IsEqualTo(project.Items[0].DatabaseId);
        await Assert.That(project.Items[0].Series).IsEmpty();
        await Assert.That(database.Series[0].Writes).IsEqualTo(3);
        await Assert.That(database.Reads).IsEqualTo(22);
        await Assert.That(database.Period.Reads).IsEqualTo(12);
        await Assert.That(queries[0]).IsEqualTo("?granularity=hour&from=2026-10-12T00%3A00%3A00.0000000%2B03%3A00&to=2026-10-12T01%3A00%3A00.0000000%2B03%3A00");
        await Assert.That(queries[1]).IsEqualTo("");
    }

    [Test]
    [Arguments(400, "invalid_request", typeof(TerraBaseValidationException))]
    [Arguments(404, "unknown_database", typeof(TerraBaseNotFoundException))]
    [Arguments(409, "database_replicated", typeof(TerraBaseConflictException))]
    [Arguments(421, "wrong_region", typeof(TerraBaseWrongRegionException))]
    [Arguments(503, "storage_unavailable", typeof(TerraBaseUnavailableException))]
    public async Task Lifecycle_failure_remains_explicit_and_is_never_retried(int status, string code, Type expected)
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            calls++;
            var response = TerraBaseIntegrationFixture.Json($$$"""{"error":{"code":"{{{code}}}","message":"refused"}}""", (HttpStatusCode)status);
            response.Headers.Add("X-TerraBase-Region", "sjc");
            response.Headers.Add("X-TerraBase-Database-Home", "gru");
            response.Headers.Add("X-TerraBase-Database-Regions", "gru,fra");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<TerraBaseException>(() => TerraBaseIntegrationFixture.Client(http).Project(TerraBaseIntegrationFixture.ProjectId).DeleteDatabaseAsync("app"));
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(error!.GetType()).IsEqualTo(expected);
        await Assert.That(error.Region).IsEqualTo("sjc");
        if (error is TerraBaseWrongRegionException wrong)
        {
            await Assert.That(wrong.HomeRegion).IsEqualTo("gru");
            await Assert.That(wrong.Regions).IsEquivalentTo(["gru", "fra"]);
        }
    }

    [Test]
    public async Task Read_only_SQL_and_secondary_indexes_use_canonical_contracts()
    {
        var calls = new List<(string Method, string Path, string Query, string? Body)>();
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            calls.Add((request.Method.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Query, request.Content is null ? null : await request.Content.ReadAsStringAsync(token)));
            var fixture = request.RequestUri.AbsolutePath.EndsWith("/sql", StringComparison.Ordinal) ? "sql.json"
                : request.Method == HttpMethod.Get ? "indexes.json" : "index.json";
            return request.Method == HttpMethod.Delete ? new HttpResponseMessage(HttpStatusCode.NoContent) : TerraBaseIntegrationFixture.Json(TerraBaseIntegrationFixture.Read(fixture));
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var result = await db.RunSqlAsync("SELECT * FROM items LIMIT 1");
        var indexes = await db.ListIndexesAsync("items");
        var index = await db.CreateIndexAsync("items", ["title"]);
        await db.DropIndexAsync("items_title");
        await Assert.That(result.Truncated).IsTrue();
        await Assert.That(result.Rows[0].Value.GetProperty("title").GetString()).IsEqualTo("São Paulo");
        await Assert.That(indexes.Items[0].Name).IsEqualTo(index.Name);
        await Assert.That(index.State).IsEqualTo("active");
        await Assert.That(calls[0].Body).IsEqualTo("{\"sql\":\"SELECT * FROM items LIMIT 1\"}");
        await Assert.That(calls[1].Query).IsEqualTo("?collection=items");
        await Assert.That(calls[2].Body).IsEqualTo("{\"collection\":\"items\",\"fields\":[\"title\"]}");
        await Assert.That(calls[3].Method).IsEqualTo("DELETE");
        await Assert.That(calls[3].Path).EndsWith("/indexes/items_title");
    }

    [Test]
    public async Task Collection_statistics_and_inferred_fields_use_their_own_contracts()
    {
        var paths = new List<string>();
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(TerraBaseIntegrationFixture.Json(TerraBaseIntegrationFixture.Read(
                request.RequestUri.AbsolutePath.EndsWith("/schema", StringComparison.Ordinal) ? "collection-stats.json" : "inferred-schema.json")));
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var stats = await db.ListCollectionStatsAsync();
        var schema = await db.GetInferredSchemaAsync("items");
        await Assert.That(stats.Items[0].Documents).IsEqualTo(2);
        await Assert.That(stats.Items[0].Bytes).IsEqualTo(1024);
        await Assert.That(schema.Fields[0].Path).IsEqualTo("title");
        await Assert.That(schema.Fields[0].Types["string"]).IsEqualTo(2);
        await Assert.That(paths[0]).EndsWith("/schema");
        await Assert.That(paths[1]).EndsWith("/schema/items");
    }

    [Test]
    public async Task Declared_JSON_schema_is_separate_from_uniqueness_declarations()
    {
        var paths = new List<string>();
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(TerraBaseIntegrationFixture.Json(request.Method == HttpMethod.Delete ? TerraBaseIntegrationFixture.Read("detached-schema.json")
                : request.RequestUri.AbsolutePath.Contains("/collections/", StringComparison.Ordinal) ? TerraBaseIntegrationFixture.Read("declared-schema.json") : "{\"constraints\":[]}"));
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var found = await db.GetDeclaredSchemaAsync("items");
        await db.PutDeclaredSchemaAsync("items", found.Schema!.Value);
        var detached = await db.DeleteDeclaredSchemaAsync("items");
        await db.DeclareSchemaAsync("items", new TerraBaseUniqueSchemaRequest());
        await Assert.That(found.Dialect).IsEqualTo("https://json-schema.org/draft/2020-12/schema");
        await Assert.That(detached.Schema).IsNull();
        await Assert.That(paths.Take(3).Distinct().Single()).EndsWith("/collections/items/schema");
        await Assert.That(paths[3]).EndsWith("/schema/items");
    }
}
