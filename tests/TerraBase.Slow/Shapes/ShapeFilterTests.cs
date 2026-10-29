using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Errors;
using TerraBase.Features.Memory;
using TerraBase.Features.Shapes;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Slow.Shapes;

public sealed class ShapeFilterTests
{
    private static readonly string[] Documents =
    [
        """{"n":1,"s":"a","b":true,"tags":["x",null,2],"o":{"k":"v"}}""",
        """{"n":1.0,"s":"ab","b":false,"tags":[],"o":{"k":1}}""",
        """{"n":9007199254740993,"s":"\ud83d\ude00","b":null,"tags":[["x"]],"o":[]}""",
        """{"n":9007199254740992,"s":"\uFFFD","tags":"x"}""",
        """{"n":-0.5,"s":"<b>","b":1,"o":{"k":null}}""",
        """{"n":"1","s":"","tags":[null]}""",
        """{"n":1e300,"s":"a\u0000b","b":true}""",
        """{"n":null,"s":"z","x~y/z":7}""",
        """{}""",
    ];

    private static readonly string[] Filters =
    [
        """{"eq":["/n",1]}""",
        """{"eq":["/n",9007199254740992]}""",
        """{"eq":["/n",9007199254740992.0]}""",
        """{"gt":["/n",9007199254740992]}""",
        """{"lt":["/n",1]}""",
        """{"gte":["/n",-1]}""",
        """{"ne":["/n",1]}""",
        """{"eq":["/n",null]}""",
        """{"eq":["/n","1"]}""",
        """{"gt":["/s",""]}""",
        """{"lt":["/s","\uFFFF"]}""",
        """{"lte":["/s","a"]}""",
        """{"eq":["/s","a\u0000b"]}""",
        """{"prefix":["/s","a"]}""",
        """{"prefix":["/s",""]}""",
        """{"eq":["/b",true]}""",
        """{"gt":["/b",false]}""",
        """{"exists":"/b"}""",
        """{"not":{"exists":"/o/k"}}""",
        """{"eq":["/o/k","v"]}""",
        """{"contains":["/tags",null]}""",
        """{"contains":["/tags","x"]}""",
        """{"contains":["/tags",2.0]}""",
        """{"in":["/n",[1,"1",null]]}""",
        """{"nin":["/s",["a","z"]]}""",
        """{"eq":["/x~0y~1z",7]}""",
        """{"gt":["$id","0"]}""",
        """{"and":[{"exists":"/n"},{"or":[{"eq":["/b",true]},{"lt":["/n",0]}]}]}""",
    ];

    [Test]
    public async Task A_filter_matches_a_materialized_document_exactly_as_the_query_endpoint_does()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var ids = new List<string>();
        foreach (var document in Documents)
        {
            var id = DocumentApi.NewId();
            (await api.SendRawAsync(HttpMethod.Put, api.Document(id), document, "application/json")).Status.ShouldBe(HttpStatusCode.Created);
            ids.Add(id);
        }

        var stored = await StoredAsync(api, ids);
        var many = $$"""{"in":["/n",[{{string.Join(',', Enumerable.Range(2, 70))}},1.0,"1"]]}""";
        foreach (var where in Filters.Append(many))
        {
            var filter = Parsed(where);
            var expected = await QueryIdsAsync(api, where);
            var actual = stored.Where(filter.Matches).Select(document => document.Id).Order(StringComparer.Ordinal).ToArray();
            await Assert.That(actual).IsEquivalentTo(expected).Because(where);
        }

        var all = ShapeFilter.All;
        await Assert.That(stored.All(all.Matches)).IsTrue();
        await Assert.That(Parsed("""{"exists":"$id"}""").Matches(stored[0] with { Deleted = true })).IsFalse()
            .Because("a document deleted by seal time is sent as a delete");
        await Assert.That(all.Matches(stored[0] with { UniqueConflict = true })).IsFalse()
            .Because("queries exclude documents that have a uniqueness conflict");
    }

    [Test]
    public async Task Equivalent_spellings_share_one_canonical_filter_that_queries_the_same_documents()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        foreach (var document in Documents)
        {
            (await api.SendRawAsync(HttpMethod.Put, api.Document(DocumentApi.NewId()), document, "application/json")).Status.ShouldBe(HttpStatusCode.Created);
        }

        string[][] equivalent =
        [
            [
                """{"and":[{"eq":["/n",1]},{"eq":["/s","a"]}]}""",
                """ { "and" : [ {"eq":["/s","a"]}, {"and":[{"eq":["/n",1.0]},{"eq":["/s","a"]}]} ] } """,
            ],
            [
                """{"in":["/n",[3,1,2,1]]}""",
                """{"or":[{"in":["/n",[2.0,3,1]]}]}""",
            ],
            [
                """{"ne":["/n",0]}""",
                """{"not":{"eq":["/n",-0]}}""",
                """{"not":{"in":["/n",[0.0,0]]}}""",
                """{"nin":["/n",[0]]}""",
            ],
            [
                """{"nin":["/s",["z","a"]]}""",
                """{"not":{"in":["/s",["a","z","a"]]}}""",
            ],
            [
                """{"exists":"/b"}""",
                """{"not":{"not":{"exists":"/b"}}}""",
                """{"or":[{"exists":"/b"},{"exists":"/b"}]}""",
            ],
        ];
        foreach (var group in equivalent)
        {
            var canonical = Parsed(group[0]).Canonical;
            var expected = await QueryIdsAsync(api, canonical);
            foreach (var where in group)
            {
                await Assert.That(Parsed(where).Canonical).IsEqualTo(canonical).Because(where);
                await Assert.That(await QueryIdsAsync(api, where)).IsEquivalentTo(expected).Because(where);
            }

            await Assert.That(Parsed(canonical).Canonical).IsEqualTo(canonical).Because("the canonical form is a fixed point");
        }

        await Assert.That(Parsed("""{ "eq" : [ "/s" , "<b>" ] }""").Canonical).IsEqualTo("""{"eq":["/s","<b>"]}""")
            .Because("compact JSON with relaxed escaping keeps < literal");
        await Assert.That(ShapeFilter.All.Canonical).IsEmpty();
        string[] distinct =
        [
            """{"eq":["/n",1]}""", """{"eq":["/n","1"]}""", """{"eq":["/n",true]}""", """{"eq":["/n",null]}""",
            """{"eq":["/n",1.5]}""", """{"eq":["/n",9007199254740993]}""", """{"eq":["/n",9007199254740992]}""",
            """{"or":[{"eq":["/n",1]},{"eq":["/s","a"]}]}""", """{"and":[{"eq":["/n",1]},{"eq":["/s","a"]}]}""",
        ];
        await Assert.That(distinct.Select(where => Parsed(where).Canonical).Distinct(StringComparer.Ordinal).Count()).IsEqualTo(distinct.Length);
    }

    [Test]
    public async Task Flattening_beyond_64_children_regroups_into_a_valid_order_independent_where()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        (await api.SendRawAsync(HttpMethod.Put, api.Document(DocumentApi.NewId()), """{"n":1}""", "application/json")).Status.ShouldBe(HttpStatusCode.Created);
        var faker = new Faker();
        var terms = Enumerable.Range(0, 120).Select(i => $$"""{"ne":["/n",{{i + 2}}]}""").ToList();
        string Nested(IEnumerable<string> children)
        {
            return $$"""{"and":[{{string.Join(',', children.Chunk(60).Select(chunk => $$"""{"and":[{{string.Join(',', chunk)}}]}"""))}}]}""";
        }

        var canonical = Parsed(Nested(terms)).Canonical;
        await Assert.That(Parsed(Nested(faker.Random.Shuffle(terms))).Canonical).IsEqualTo(canonical);
        await Assert.That(Parsed(canonical).Canonical).IsEqualTo(canonical);
        await Assert.That((await QueryIdsAsync(api, canonical)).Length).IsEqualTo(1)
            .Because("the regrouped canonical form is a valid query filter with the same meaning");
        string Group(string op, int start, int count)
        {
            return $$"""{"{{op}}":[{{string.Join(',', Enumerable.Range(start, count).Select(i => $$"""{"ne":["/n",{{i}}]}"""))}}]}""";
        }
        var boundary = $$"""{"and":[{{Group("and", 2, 64)}},{{Group("and", 66, 62)}},{{Group("or", 128, 64)}},{{Group("or", 192, 61)}}]}""";
        var normalizedBoundary = Parsed(boundary).Canonical;
        await Assert.That(Parsed(normalizedBoundary).Canonical).IsEqualTo(normalizedBoundary);
        await Assert.That(await QueryIdsAsync(api, normalizedBoundary)).IsEquivalentTo(await QueryIdsAsync(api, boundary));
    }

    [Test]
    public async Task Unsupported_filters_are_rejected_with_the_query_endpoints_400()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        string[] invalid =
        [
            """{"regex":["/s","a"]}""",
            """{"lt":["/n",null]}""",
            """{"eq":["/n",[1]]}""",
            """{"eq":["n",1]}""",
            """{"eq":[{"lower":"/s"},"a"]}""",
            """{"and":[]}""",
            """{"eq":["/n",1],"ne":["/n",2]}""",
            """{"in":["/n",[]]}""",
            """{"prefix":["/s",1]}""",
            """{"not":{"not":{"not":{"not":{"not":{"not":{"not":{"not":{"exists":"/n"}}}}}}}}}""",
        ];
        foreach (var where in invalid)
        {
            var error = Invalid(where);
            await Assert.That(error).IsNotNull().Because(where);
            var response = await QueryAsync(api, where);
            await Assert.That(response.Status).IsEqualTo(HttpStatusCode.BadRequest).Because(where);
            await Assert.That(error!.Code).IsEqualTo(response.Code).Because(where);
            await Assert.That(error.Message).IsEqualTo(response.Message).Because(where);
        }

        await Assert.That(Invalid("{\"eq\":")?.Code).IsEqualTo("invalid_query");
        await Assert.That(Invalid(new string(' ', ShapeFilter.MaxWhereBytes) + "{}")?.Code).IsEqualTo("invalid_query");
        await Assert.That(Invalid("")?.Code).IsEqualTo("invalid_request").Because("a present where is never empty");
        await Assert.That(ShapeFilter.Parse(null) is ShapeFilter all && ReferenceEquals(all, ShapeFilter.All)).IsTrue();
    }

    [Test]
    public async Task The_65th_filtered_shape_evicts_the_least_recently_used_of_its_own_database_only()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var first = await api.TenantOfAsync();
        (await api.SendAsync(HttpMethod.Put, api.DatabasePath("other"))).Status.ShouldBe(HttpStatusCode.Created);
        var second = await api.TenantOfAsync("other");
        var budget = new ProbeBudget(api.Server.Services.GetRequiredService<IMemoryBudget>());
        using var registry = new ShapeFilterRegistry(budget);
        var shapes = Enumerable.Range(0, ShapeFilterRegistry.MaxPerDatabase + 1).Select(i => Parsed($$"""{"eq":["/n",{{i}}]}""")).ToArray();
        foreach (var shape in shapes[..ShapeFilterRegistry.MaxPerDatabase])
        {
            await Assert.That(Evicted(registry.Admit(first, DocumentApi.Collection, shape))).IsEmpty();
            await Assert.That(Evicted(registry.Admit(second, DocumentApi.Collection, shape))).IsEmpty();
        }
        await Assert.That(registry.Check(first, DocumentApi.Collection, shapes[0])).IsNull();
        await Assert.That(Evicted(registry.Admit(first, DocumentApi.Collection, shapes[^1])))
            .IsEquivalentTo([new ShapeFilterKey(first.DatabaseId!, DocumentApi.Collection, shapes[1].Canonical)]);

        var conflict = registry.Check(first, DocumentApi.Collection, shapes[1]);
        await Assert.That(conflict?.Code).IsEqualTo("must_refetch");
        await Assert.That(registry.Check(first, DocumentApi.Collection, shapes[1])?.Code).IsEqualTo("must_refetch")
            .Because("a continuation never re-admits an evicted shape");
        foreach (var shape in shapes[..ShapeFilterRegistry.MaxPerDatabase])
        {
            await Assert.That(registry.Check(second, DocumentApi.Collection, shape)).IsNull().Because("databases are isolated");
        }

        await Assert.That(registry.Check(first, "other", shapes[0])?.Code).IsEqualTo("must_refetch").Because("a shape is one collection");
        await Assert.That(Evicted(registry.Admit(first, DocumentApi.Collection, ShapeFilter.All))).IsEmpty();
        await Assert.That(registry.Check(first, DocumentApi.Collection, ShapeFilter.All)).IsNull().Because("unfiltered shapes are not counted");
        await Assert.That(Evicted(registry.Admit(first, DocumentApi.Collection, shapes[1])))
            .IsEquivalentTo([new ShapeFilterKey(first.DatabaseId!, DocumentApi.Collection, shapes[2].Canonical)]);
        await Assert.That(registry.Check(first, DocumentApi.Collection, shapes[1])).IsNull();
        await Assert.That(budget.Outstanding).IsEqualTo(2 * ShapeFilterRegistry.MaxPerDatabase).Because("every admitted shape holds one reservation");
        budget.Refuse = true;
        var refused = registry.Admit(first, DocumentApi.Collection, Parsed("""{"eq":["/n","new"]}"""));
        await Assert.That(refused is QuotaExceeded { Code: "shape_busy" }).IsTrue();
        await Assert.That(registry.Check(first, DocumentApi.Collection, shapes[3])).IsNull();
        await Assert.That(Evicted(registry.Admit(first, DocumentApi.Collection, shapes[3]))).IsEmpty();

        registry.Dispose();
        await Assert.That(budget.Outstanding).IsEqualTo(0).Because("disposing the registry releases every reservation");
    }

    [Test]
    public async Task A_duplicate_member_matches_by_its_first_occurrence_as_the_query_endpoint_does()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var id = DocumentApi.NewId();
        (await api.PutAsync(id, new { d = 1 })).Status.ShouldBe(HttpStatusCode.Created);
        var store = (TursoDatabaseStore)(await api.Server.Services.GetRequiredService<TursoStorageEngine>()
            .OpenDatabaseAsync(await api.TenantOfAsync(), CancellationToken.None))!;
        _ = await store.WithWriterAsync(session =>
        {
            using var capture = session.Connection.CreateCommand();
            capture.CommandText = TursoSession.CaptureOff;
            capture.ExecuteNonQuery();
            try
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = $"UPDATE \"{DocumentApi.Collection}\" SET doc = $body WHERE id = $id;";
                command.Parameters.AddWithValue("$body", """{"d":1,"d":2}""");
                command.Parameters.AddWithValue("$id", id);
                return command.ExecuteNonQuery();
            }
            finally
            {
                capture.CommandText = TursoSession.CaptureOn;
                capture.ExecuteNonQuery();
            }
        }, "injecting a retained legacy document in a test", CancellationToken.None);

        var stored = (await StoredAsync(api, [id]))[0];
        await Assert.That(Parsed("""{"eq":["/d",1]}""").Matches(stored)).IsTrue();
        await Assert.That(Parsed("""{"eq":["/d",2]}""").Matches(stored)).IsFalse();
        foreach (var where in new[] { """{"eq":["/d",1]}""", """{"eq":["/d",2]}""" })
        {
            await Assert.That(Parsed(where).Matches(stored)).IsEqualTo((await QueryIdsAsync(api, where)).Contains(id)).Because(where);
        }
    }

    [Test]
    public async Task A_document_changed_out_of_the_filter_by_seal_time_no_longer_matches()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var id = DocumentApi.NewId();
        var kept = DocumentApi.NewId();
        var excluded = DocumentApi.NewId();
        var filter = Parsed($$"""{"and":[{"eq":["/status","open"]},{"ne":["$id","{{excluded}}"]}]}""");
        var tenant = await api.TenantOfAsync();
        var store = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant, CancellationToken.None)
            ?? throw new InvalidOperationException("the database is missing");
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var turso = (TursoDatabaseStore)(await engine.OpenDatabaseAsync(tenant, CancellationToken.None))!;
        var start = await turso.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = "SELECT seq FROM _tb_feed_sequence WHERE id = 1;";
            return (long)command.ExecuteScalar()!;
        }, "a test reading the shape's initial feed boundary", CancellationToken.None);

        (await api.PutAsync(id, new { status = "open" })).Status.ShouldBe(HttpStatusCode.Created);
        await Assert.That(filter.Matches((await StoredAsync(api, [id]))[0])).IsTrue();
        (await api.PutAsync(kept, new { status = "open" })).Status.ShouldBe(HttpStatusCode.Created);
        (await api.PutAsync(excluded, new { status = "open" })).Status.ShouldBe(HttpStatusCode.Created);

        (await api.PutAsync(id, new { status = "closed" })).Status.ShouldBe(HttpStatusCode.OK);
        await Assert.That(filter.Matches((await StoredAsync(api, [id]))[0])).IsFalse()
            .Because("the sealer materializes the current value, which it then sends as a delete");
        await Assert.That(await QueryIdsAsync(api, filter.Canonical)).IsEquivalentTo([kept]);

        ShapeSealDefinition shape;
        await using (var transaction = await store.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            var collection = await transaction.GetCollectionAsync(DocumentApi.Collection, CancellationToken.None)
                ?? throw new InvalidOperationException("the collection is missing");
            using var canonical = JsonDocument.Parse(filter.Canonical);
            var identity = ShapeHandle.Compute(store, collection, canonical.RootElement);
            shape = new ShapeSealDefinition(identity.ShapeId, identity.Handle, collection.Name, collection.Id,
                RandomNumberGenerator.GetBytes(16), store.Incarnation, filter.Matches);
        }

        var sealer = new ShapeSealer(api.Server.Services.GetRequiredService<IMemoryBudget>(),
            NullLogger<ShapeSealer>.Instance, new TursoShapeStorage(engine));
        var end = await sealer.SealUpToHeadAsync(store, shape, start, CancellationToken.None);
        using var chunk = await sealer.ReadChunkAsync(store, shape.Handle, start, CancellationToken.None)
            ?? throw new InvalidOperationException("the filtered chunk is missing");
        await Assert.That(chunk.EndOffset).IsEqualTo(end);
        using var body = JsonDocument.Parse(chunk.Body);
        var ops = body.RootElement.GetProperty("changes").EnumerateArray()
            .SelectMany(change => change.GetProperty("ops").EnumerateArray()).ToArray();
        await Assert.That(ops.Count(op => op.GetProperty("id").GetString() == id)).IsEqualTo(2);
        await Assert.That(ops.Where(op => op.GetProperty("id").GetString() != kept)
            .All(op => op.GetProperty("op").GetString() == "delete" && !op.TryGetProperty("document", out _))).IsTrue()
            .Because("the FR-12 predicate seam emits deletes for current nonmatches, including an id-only exclusion");
        var upsert = ops.Single(op => op.GetProperty("id").GetString() == kept);
        await Assert.That(upsert.GetProperty("op").GetString()).IsEqualTo("upsert");
        await Assert.That(upsert.GetProperty("document").GetProperty("status").GetString()).IsEqualTo("open");
        await Assert.That(ops.Count(op => op.GetProperty("id").GetString() == excluded)).IsEqualTo(1);
    }

    private static ShapeFilter Parsed(string where) => ShapeFilter.Parse(where) is ShapeFilter filter
        ? filter
        : throw new InvalidOperationException($"not a valid filter: {where}");

    private static Validation? Invalid(string where) => ShapeFilter.Parse(where) is Validation error ? error : null;

    private static IReadOnlyList<ShapeFilterKey> Evicted(ShapeAdmission admission) => admission is ShapeAdmitted admitted
        ? admitted.Evicted
        : throw new InvalidOperationException("the shape was not admitted");

    private static async Task<ApiResponse> QueryAsync(DocumentApi api, string where)
    {
        using var filter = JsonDocument.Parse(where);
        return await api.QueryAsync(new { filter = filter.RootElement.Clone(), limit = 1000 });
    }

    private static async Task<string[]> QueryIdsAsync(DocumentApi api, string where)
    {
        var response = await QueryAsync(api, where);
        if (response.Status != HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"query {where} answered {response.Status} {response.Code}: {response.Message}");
        }

        return [.. response.Ids.Order(StringComparer.Ordinal)];
    }

    private static async Task<List<StoredDocument>> StoredAsync(DocumentApi api, List<string> ids)
    {
        var tenant = await api.TenantOfAsync();
        var store = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant, CancellationToken.None)
            ?? throw new InvalidOperationException("the database is missing");
        await using var transaction = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        var stored = new List<StoredDocument>();
        foreach (var id in ids)
        {
            stored.Add(await transaction.GetAsync(DocumentApi.Collection, id, CancellationToken.None)
                ?? throw new InvalidOperationException("a written document is missing"));
        }

        return stored;
    }

    private sealed class ProbeBudget(IMemoryBudget inner) : IMemoryBudget
    {
        private int _outstanding;

        public bool Refuse { get; set; }

        public int Outstanding => Volatile.Read(ref _outstanding);

        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes) => TryReserve(lane, bytes, null);

        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes, TenantContext? tenant)
        {
            if (Refuse || inner.TryReserve(lane, bytes, tenant) is not { } reservation)
            {
                return null;
            }

            Interlocked.Increment(ref _outstanding);
            return new Counted(this, reservation);
        }

        private sealed class Counted(ProbeBudget owner, IMemoryReservation inner) : IMemoryReservation
        {
            private int _disposed;

            public long Bytes => inner.Bytes;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    Interlocked.Decrement(ref owner._outstanding);
                    inner.Dispose();
                }
            }
        }
    }
}
