using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bogus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Management;
using TerraBase.Features.Management.Queries;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class QueryShapeApiTests
{
    private static readonly string[] NumberProjection = ["/n"];
    private static readonly string[] NumberIndex = ["n"];
    private static readonly int[] UnmatchedNumbers = [100, 200];
    private static readonly int[] UnmatchedNumber = [999];

    [Test]
    public async Task Project_JWT_statistics_require_database_scope_while_legacy_aliases_keep_working()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await SeedAsync(api, database);
        await PostAsync(data, Query(database), new { filter = Eq("/n", 1) });
        var legacy = await ShapesAsync(api.Client, database);
        await Assert.That(legacy.Length).IsEqualTo(1);
        using var service = api.Service(api.User);
        var serviceShapes = await ShapesAsync(service, database);
        await Assert.That(serviceShapes[0].GetRawText()).IsEqualTo(legacy[0].GetRawText());

        using var project = ProjectClient(api, database.Project, "databases:read");
        var canonical = ManagementApi.Uri(database.Data + "/query-shapes");
        var granted = await project.GetFromJsonAsync<JsonElement>(canonical);
        await Assert.That(granted.GetProperty("items").GetArrayLength()).IsEqualTo(1);
        await Assert.That(granted.GetProperty("items")[0].GetRawText()).IsEqualTo(legacy[0].GetRawText());

        using var noScope = ProjectClient(api, database.Project, "documents:read");
        using var forbidden = await noScope.GetAsync(canonical);
        await Assert.That(forbidden.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        using var foreign = ProjectClient(api, EntityIds.New().ToString(), "databases:read");
        using var inaccessible = await foreign.GetAsync(canonical);
        await Assert.That(inaccessible.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Json_shapes_remove_literals_flatten_groups_and_count_cache_hits_without_replaying_scans()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await SeedAsync(api, database);
        var query = Query(database);
        var secret = new Faker().Internet.Email();
        var first = new { filter = new { and = new object[] { Eq("/n", 1), Eq("/email", secret) } }, limit = 3 };
        await PostAsync(data, query, first);
        var before = (await ShapesAsync(api.Client, database))[0];
        await PostAsync(data, query, first);
        var cached = (await ShapesAsync(api.Client, database))[0];
        await Assert.That(cached.GetProperty("queries").GetInt64()).IsEqualTo(2);
        await Assert.That(cached.GetProperty("rowsScanned").GetInt64()).IsEqualTo(before.GetProperty("rowsScanned").GetInt64());
        await Assert.That(before.GetProperty("rowsScanned").GetInt64()).IsEqualTo(6);

        await PostAsync(data, query, new
        {
            filter = new { and = new object[] { new { and = new object[] { Eq("/email", "different-private-value") } }, Eq("/n", 2) } },
            sort = new[] { new { path = "$id", order = "asc" } },
            limit = 1,
            project = NumberProjection,
        });
        var normalized = await ShapesAsync(api.Client, database);
        await Assert.That(normalized.Length).IsEqualTo(1);
        await Assert.That(normalized[0].GetProperty("queries").GetInt64()).IsEqualTo(3);
        await Assert.That(normalized[0].GetProperty("totalLatencyMs").GetDouble()).IsGreaterThan(0);
        await Assert.That(normalized[0].GetProperty("maxLatencyMs").GetDouble()).IsGreaterThan(0);
        await Assert.That(normalized[0].GetProperty("latencyBuckets").EnumerateArray().Sum(bucket => bucket.GetInt64())).IsEqualTo(3);
        await Assert.That(normalized[0].GetProperty("p95LatencyMs").GetDouble())
            .IsGreaterThan(0).And.IsLessThanOrEqualTo(normalized[0].GetProperty("maxLatencyMs").GetDouble());
        await Assert.That(normalized[0].GetProperty("shape").GetString()!).DoesNotContain(secret);
        await Assert.That(normalized[0].GetProperty("shape").GetString()!).DoesNotContain("different-private-value");

        await PostAsync(data, query, new { filter = new { and = new object[] { Eq("/n", "1"), Eq("/email", secret) } } });
        await PostAsync(data, query, new { filter = first.filter, sort = new[] { new { path = "$id", order = "desc" } } });
        await Assert.That((await ShapesAsync(api.Client, database)).Length).IsEqualTo(3);
    }

    [Test]
    public async Task Sql_shapes_converge_and_indexed_residual_scans_count_rejected_candidates()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await SeedAsync(api, database);
        var sql = database.Path + "/sql";
        await PostAsync(api.Client, sql, new { sql = "SELECT * FROM people WHERE n = 1 AND email = 'private-a'" });
        await PostAsync(api.Client, sql, new { sql = "SELECT n FROM people WHERE email = 'private-b' AND (n = 2) LIMIT 1" });
        var before = (await ShapesAsync(api.Client, database))[0];
        await Assert.That(before.GetProperty("queries").GetInt64()).IsEqualTo(2);
        await Assert.That(before.GetProperty("rowsScanned").GetInt64()).IsEqualTo(12);
        await PostAsync(api.Client, database.Path + "/indexes", new { collection = "people", fields = NumberIndex });
        await PostAsync(api.Client, sql, new { sql = "SELECT * FROM people WHERE n = 1 AND email = 'private-c'" });
        var indexed = (await ShapesAsync(api.Client, database))[0];
        await Assert.That(indexed.GetProperty("queries").GetInt64()).IsEqualTo(3);
        // Three n=1 candidates reached the residual email predicate; none returned a row.
        await Assert.That(indexed.GetProperty("rowsScanned").GetInt64()).IsEqualTo(15);

        await PostAsync(api.Client, database.Path + "/sql/check", new { sql = "SELECT * FROM people WHERE n = 1" });
        await Assert.That((await ShapesAsync(api.Client, database)).Length).IsEqualTo(1);
        await PostAsync(data, Query(database), new { filter = new { and = new object[] { Eq("/n", 1), Eq("/email", "private-c") } } });
        await Assert.That((await ShapesAsync(api.Client, database)).Length).IsEqualTo(2);
    }

    [Test]
    public async Task Shape_counts_survive_shutdown_and_remain_isolated_by_database_and_membership()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-query-shapes-").FullName;
        try
        {
            ManagedDatabaseRef database;
            string user;
            string id;
            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory, time: new FakeTimeProvider()))
            {
                user = api.User;
                database = await api.CreateManagedDatabaseAsync();
                using var data = await SeedAsync(api, database);
                await PostAsync(data, Query(database), new { filter = Eq("/n", 1) });
                await PostAsync(data, Query(database), new { filter = Eq("/n", 2) });
                id = (await ShapesAsync(api.Client, database))[0].GetProperty("id").GetString()!;
                var other = await api.CreateManagedDatabaseAsync();
                await Assert.That((await ShapesAsync(api.Client, other)).Length).IsEqualTo(0);
                using var stranger = api.As(EntityIds.New().ToString());
                using var denied = await stranger.GetAsync(ManagementApi.Uri(Stats(database)));
                await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            }

            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory, time: new FakeTimeProvider()))
            {
                using var owner = api.As(user);
                var restored = await ShapesAsync(owner, database);
                await Assert.That(restored.Length).IsEqualTo(1);
                await Assert.That(restored[0].GetProperty("id").GetString()).IsEqualTo(id);
                await Assert.That(restored[0].GetProperty("queries").GetInt64()).IsEqualTo(2);
                var recorder = api.Server.Services.GetRequiredService<QueryShapeRecorder>();
                await recorder.FlushAsync(CancellationToken.None);
                await recorder.FlushAsync(CancellationToken.None);
                await Assert.That((await ShapesAsync(owner, database))[0].GetProperty("queries").GetInt64()).IsEqualTo(2);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Cardinality_is_bounded_in_pending_and_persisted_statistics()
    {
        await using var api = await ManagementApi.StartAsync(time: new FakeTimeProvider());
        var database = await api.CreateManagedDatabaseAsync();
        await api.SetPlanAsync(api.User, database.Organization, "enterprise");
        using var data = await SeedAsync(api, database);
        for (var i = 0; i < 130; i++)
        {
            await PostAsync(data, Query(database), new { filter = Eq("/field" + i, i) });
        }

        await Assert.That((await ShapesAsync(api.Client, database)).Length).IsEqualTo(128);
        var recorder = api.Server.Services.GetRequiredService<QueryShapeRecorder>();
        await recorder.FlushAsync(CancellationToken.None);
        await PostAsync(data, Query(database), new { filter = Eq("/another_field", 0) });
        await recorder.FlushAsync(CancellationToken.None);
        await Assert.That((await ShapesAsync(api.Client, database)).Length).IsEqualTo(128);
        await PostAsync(data, Query(database), new { filter = Eq("/field0", -1) });
        var stats = await ShapesAsync(api.Client, database);
        await Assert.That(stats[0].GetProperty("queries").GetInt64()).IsEqualTo(2);
    }

    [Test]
    public async Task Scans_include_sort_admission_and_both_count_walks_and_in_shapes_ignore_arity()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await SeedAsync(api, database);
        await PostAsync(data, Query(database), new
        {
            filter = new { @in = new object[] { "/n", UnmatchedNumbers } },
            sort = new[] { new { path = "/n", order = "asc" }, new { path = "/n", order = "desc" } },
            count = true,
        });
        var counted = (await ShapesAsync(api.Client, database))[0];
        // Six candidates for sort admission, six for filtering, six for count, six for its overflow traversal.
        await Assert.That(counted.GetProperty("rowsScanned").GetInt64()).IsEqualTo(24);
        await PostAsync(data, Query(database), new
        {
            filter = new { @in = new object[] { "/n", UnmatchedNumber } },
            sort = new[] { new { path = "/n", order = "asc" } },
        });
        var stats = await ShapesAsync(api.Client, database);
        await Assert.That(stats.Length).IsEqualTo(1);
        await Assert.That(stats[0].GetProperty("queries").GetInt64()).IsEqualTo(2);
        await Assert.That(stats[0].GetProperty("rowsScanned").GetInt64()).IsEqualTo(36);
    }

    [Test]
    public async Task A_committed_flush_that_throws_is_not_recounted_and_keeps_concurrent_observations()
    {
        var hook = new UsageCommitHook((collection, _, _) => collection.EndsWith("/query_shapes", StringComparison.Ordinal));
        await using var api = await ManagementApi.StartAsync(time: new FakeTimeProvider(), services: hook.Install);
        var database = await api.CreateManagedDatabaseAsync();
        using var data = await SeedAsync(api, database);
        await PostAsync(data, Query(database), new { filter = Eq("/n", 1) });
        var recorder = api.Server.Services.GetRequiredService<QueryShapeRecorder>();
        using (var gate = hook.PauseAfterCommit(fail: true))
        {
            var flush = recorder.FlushAsync(CancellationToken.None).AsTask();
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await PostAsync(data, Query(database), new { filter = Eq("/n", 2) });
            gate.Dispose();
            await Assert.ThrowsAsync<IOException>(() => flush);
        }

        var combined = (await ShapesAsync(api.Client, database))[0];
        await Assert.That(combined.GetProperty("queries").GetInt64()).IsEqualTo(2);
        await Assert.That(combined.GetProperty("rowsScanned").GetInt64()).IsEqualTo(12);
        await recorder.FlushAsync(CancellationToken.None);
        await recorder.FlushAsync(CancellationToken.None);
        await Assert.That((await ShapesAsync(api.Client, database))[0].GetProperty("queries").GetInt64()).IsEqualTo(2);
    }

    [Test]
    [Arguments("unknown")]
    [Arguments("unknown_payload")]
    [Arguments("missing_stats")]
    [Arguments("array_stats")]
    [Arguments("missing_fields")]
    [Arguments("negative_counts")]
    [Arguments("invalid_latency")]
    [Arguments("wrong_shape_id")]
    [Arguments("oversized_shape")]
    [Arguments("wrong_key")]
    [Arguments("missing_id")]
    [Arguments("missing_collection")]
    [Arguments("missing_shape")]
    [Arguments("missing_queries")]
    [Arguments("missing_totalLatencyMs")]
    [Arguments("missing_maxLatencyMs")]
    [Arguments("missing_rowsScanned")]
    [Arguments("missing_lastSeenAt")]
    public async Task Unreadable_records_remain_intact_without_blocking_other_shapes_or_databases_from_flushing(string malformed)
    {
        await using var api = await ManagementApi.StartAsync(time: new FakeTimeProvider());
        var affected = await api.CreateManagedDatabaseAsync();
        var healthy = await api.CreateManagedDatabaseAsync();
        using var affectedData = await SeedAsync(api, affected);
        using var healthyData = await SeedAsync(api, healthy);
        await PostAsync(affectedData, Query(affected), new { filter = Eq("/n", 1) });
        var shape = (await ShapesAsync(api.Client, affected))[0];
        var collection = SystemCatalog.DatabaseState(affected.Id) + "query_shapes";
        var key = "people:" + shape.GetProperty("id").GetString();
        var catalog = api.Server.Services.GetRequiredService<SystemCatalog>();
        var recorder = api.Server.Services.GetRequiredService<QueryShapeRecorder>();
        await recorder.FlushAsync(CancellationToken.None);
        var original = await catalog.WriteAsync(async transaction =>
        {
            var saved = (await SystemCatalog.GetAsync(transaction, collection, key,
                TerraBaseJsonContext.Default.QueryShapeRecord, CancellationToken.None))!;
            var replacement = malformed switch
            {
                "unknown" => saved with { Format = "unsupported" },
                "negative_counts" => saved with { Stats = saved.Stats with { Queries = -1, RowsScanned = -1 } },
                "invalid_latency" => saved with { Stats = saved.Stats with { TotalLatencyMs = -1, MaxLatencyMs = -2 } },
                "wrong_shape_id" => saved with { Stats = saved.Stats with { Id = new string('0', 64) } },
                "oversized_shape" => saved with { Stats = saved.Stats with { Shape = new string('x', 9 * 1024) } },
                "wrong_key" => saved with { Stats = saved.Stats with { Collection = "elsewhere" } },
                _ => saved,
            };
            var json = malformed switch
            {
                "unknown_payload" => "{\"format\":\"unsupported\",\"stats\":[]}",
                "missing_stats" => "{\"format\":\"alpha\"}",
                "array_stats" => "{\"format\":\"alpha\",\"stats\":[]}",
                "missing_fields" => "{\"format\":\"alpha\",\"stats\":{}}",
                _ => JsonSerializer.Serialize(replacement, TerraBaseJsonContext.Default.QueryShapeRecord),
            };
            if (malformed.StartsWith("missing_", StringComparison.Ordinal) && malformed is not ("missing_stats" or "missing_fields"))
            {
                var record = JsonNode.Parse(json)!;
                record["stats"]!.AsObject().Remove(malformed["missing_".Length..]);
                json = record.ToJsonString();
            }
            await transaction.PutAsync(collection, key, Encoding.UTF8.GetBytes(json), CancellationToken.None);
            return json;
        }, static refused => throw new InvalidOperationException(refused.Message), CancellationToken.None);

        for (var i = 0; i < 2; i++)
        {
            await PostAsync(affectedData, Query(affected), new { filter = Eq("/n", i + 2) });
            await PostAsync(affectedData, Query(affected), new { filter = Eq("/email", "private" + i) });
            await PostAsync(healthyData, Query(healthy), new { filter = Eq("/n", i + 1) });
            await recorder.FlushAsync(CancellationToken.None);
            using var unavailable = await api.Client.GetAsync(ManagementApi.Uri(Stats(affected)));
            await Assert.That(unavailable.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            var (affectedRecords, healthyRecords) = await catalog.ReadAsync(async transaction =>
            {
                var affectedSaved = await SystemCatalog.ListAllAsync(transaction, collection,
                    TerraBaseJsonContext.Default.JsonElement, CancellationToken.None);
                var healthySaved = await SystemCatalog.ListAllAsync(transaction, SystemCatalog.DatabaseState(healthy.Id) + "query_shapes",
                    TerraBaseJsonContext.Default.QueryShapeRecord, CancellationToken.None);
                return (affectedSaved, healthySaved);
            }, static refused => throw new InvalidOperationException(refused.Message), CancellationToken.None);
            // The catalog keeps the unreadable record as it is, in canonical JSON (fields in name order).
            using var written = JsonDocument.Parse(original);
            await Assert.That(affectedRecords.Count(record => JsonElement.DeepEquals(record, written.RootElement))).IsEqualTo(1);
            await Assert.That(affectedRecords.Single(record => !JsonElement.DeepEquals(record, written.RootElement))
                .GetProperty("stats").GetProperty("queries").GetInt64()).IsEqualTo(i + 1);
            await Assert.That(healthyRecords.Single().Stats.Queries).IsEqualTo(i + 1);
            await Assert.That((await ShapesAsync(api.Client, healthy))[0].GetProperty("queries").GetInt64()).IsEqualTo(i + 1);
        }
    }

    [Test]
    public async Task Recorder_disposal_joins_a_committing_flush_and_late_shutdown_calls_are_safe()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-query-shape-disposal-").FullName;
        try
        {
            ManagedDatabaseRef database;
            string user;
            var hook = new UsageCommitHook((collection, _, _) => collection.EndsWith("/query_shapes", StringComparison.Ordinal));
            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory,
                services: hook.Install, time: new FakeTimeProvider()))
            {
                user = api.User;
                database = await api.CreateManagedDatabaseAsync();
                using var data = await SeedAsync(api, database);
                await PostAsync(data, Query(database), new { filter = Eq("/n", 1) });
                var recorder = api.Server.Services.GetRequiredService<QueryShapeRecorder>();
                using (var gate = hook.PauseAfterCommit(fail: false))
                {
                    var flush = recorder.FlushAsync(CancellationToken.None).AsTask();
                    await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    var disposal = recorder.DisposeAsync().AsTask();
                    try
                    {
                        await Assert.That(disposal.IsCompleted).IsFalse();
                    }
                    finally
                    {
                        gate.Dispose();
                    }

                    await flush.WaitAsync(TimeSpan.FromSeconds(10));
                    await disposal.WaitAsync(TimeSpan.FromSeconds(10));
                }

                await recorder.FlushAsync(CancellationToken.None);
                await recorder.DisposeAsync();
                using var stopped = await api.Client.GetAsync(ManagementApi.Uri(Stats(database)));
                await Assert.That(stopped.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            }

            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory, time: new FakeTimeProvider()))
            {
                using var owner = api.As(user);
                await Assert.That((await ShapesAsync(owner, database))[0].GetProperty("queries").GetInt64()).IsEqualTo(1);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Slow_statistics_responses_keep_memory_admitted_until_serialization_finishes()
    {
        var responseGate = new ResponseGate();
        await using var api = await ManagementApi.StartAsync(
            settings => settings["memory-budget"] = "8388608", services => services.AddSingleton<IStartupFilter>(responseGate),
            time: new FakeTimeProvider());
        var database = await api.CreateManagedDatabaseAsync();
        await api.SetPlanAsync(api.User, database.Organization, "enterprise");
        using var data = await SeedAsync(api, database);
        await PostAsync(data, Query(database), new { filter = Eq("/n", 1) });
        using var request = new HttpRequestMessage(HttpMethod.Get, ManagementApi.Uri(Stats(database)));
        request.Headers.Add("X-Stall-Query-Shape-Response", "true");
        var stalled = api.Client.SendAsync(request);
        try
        {
            await responseGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var refused = await api.Client.GetAsync(ManagementApi.Uri(Stats(database)));
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        }
        finally
        {
            responseGate.Released.TrySetResult();
        }

        using var completed = await stalled.WaitAsync(TimeSpan.FromSeconds(10));
        completed.EnsureSuccessStatusCode();
        await responseGate.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var next = await api.Client.GetAsync(ManagementApi.Uri(Stats(database)));
        next.EnsureSuccessStatusCode();
    }

    [Test]
    public async Task Concurrent_cache_hits_increment_frequency_once_each()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        await api.SetPlanAsync(api.User, database.Organization, "enterprise");
        using var data = await SeedAsync(api, database);
        var request = new { filter = Eq("/n", 1) };
        await PostAsync(data, Query(database), request);
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => PostAsync(data, Query(database), request)));
        var shape = (await ShapesAsync(api.Client, database))[0];
        await Assert.That(shape.GetProperty("queries").GetInt64()).IsEqualTo(17);
        await Assert.That(shape.GetProperty("rowsScanned").GetInt64()).IsEqualTo(6);
    }

    private static object Eq(string path, object value) => new { eq = new[] { path, value } };

    private static string Query(ManagedDatabaseRef database) =>
        $"{database.Data}/collections/people/query";

    private static string Stats(ManagedDatabaseRef database) => database.Path[3..] + "/query-shapes";

    private static HttpClient ProjectClient(ManagementApi api, string project, string scopes) =>
        api.WithToken(api.Issuer.Mint(EntityIds.New().ToString(), token =>
        {
            token.Claims["project_id"] = project;
            token.Claims["scope"] = scopes;
        }));

    private static async Task<JsonElement[]> ShapesAsync(HttpClient client, ManagedDatabaseRef database)
    {
        var body = await client.GetFromJsonAsync<JsonElement>(ManagementApi.Uri(Stats(database)));
        return [.. body.GetProperty("items").EnumerateArray()];
    }

    private static async Task PostAsync(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(ManagementApi.Uri(path), body);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<HttpClient> SeedAsync(ManagementApi api, ManagedDatabaseRef database)
    {
        var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        try
        {
            await ManagementApi.CreateCollectionAsync(data, database, "people");
            var faker = new Faker();
            for (var i = 0; i < 6; i++)
            {
                using var response = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", "p" + i)),
                    new { n = i < 3 ? 1 : i, email = faker.Internet.Email() });
                response.EnsureSuccessStatusCode();
            }

            return data;
        }
        catch
        {
            data.Dispose();
            throw;
        }
    }

    private sealed class ResponseGate : IStartupFilter
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, following) =>
            {
                if (context.Request.Headers.ContainsKey("X-Stall-Query-Shape-Response"))
                {
                    context.Response.OnStarting(async () =>
                    {
                        Entered.TrySetResult();
                        await Released.Task.WaitAsync(context.RequestAborted);
                    });
                }

                try
                {
                    await following(context);
                }
                finally
                {
                    if (context.Request.Headers.ContainsKey("X-Stall-Query-Shape-Response"))
                    {
                        Completed.TrySetResult();
                    }
                }
            });
            next(app);
        };
    }
}
