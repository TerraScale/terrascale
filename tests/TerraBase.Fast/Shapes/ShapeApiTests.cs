using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Documents;
using TerraBase.Features.Shapes;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Shapes;

/// <summary>
/// <c>GET …/collections/{collection}/shape</c> (v1 shape protocol in PR #216, sections 2, 3, 8, 9, 11) on one real node.
/// Validation, scope, listener and budget checks run on the stock server. Lineage-dependent checks register fakes for
/// the internal shape services; snapshot, live-tail and resume fixtures belong only to this test binary.
/// </summary>
public sealed class ShapeApiTests
{
    private const string Handle = "h1-0123456789abcdef";

    // Base64url of the document id 0000000000E008000000000001, as a snapshot page position.
    private const string EncodedId = "MDAwMDAwMDAwMEUwMDgwMDAwMDAwMDAwMDE";
    private const string ShapeId = "0123456789abcdef0123456789abcdef";
    private const long Snapshot = 7;
    private static readonly string[] ExpectedParameters = ["project", "database", "collection", "where", "handle", "offset", "live", "cursor", "TB-Shape-Resume", "X-TerraScale-Region-Pin"];
    private static readonly string[] ExpectedResponses = ["200", "307", "304", "400", "404", "409", "414", "429", "503"];

    // Fixture bytes the fake page returns verbatim; the endpoint must not re-serialize them.
    [Test]
    public async Task A_snapshot_page_returns_the_fixture_bytes_verbatim_with_its_etag_and_metadata_headers()
    {
        await using var api = await StartWithFixturesAsync();
        var bootstrap = await GetShapeAsync(api.Client, Shape(api, "?offset=-1"));

        var page = await GetShapeAsync(api.Client, bootstrap.Location!);

        await Assert.That(page.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(page.RawBody.AsSpan().SequenceEqual(FixtureBody)).IsTrue();
        await Assert.That(page.ContentType).IsEqualTo("application/json");
        await Assert.That(page.Handle).IsEqualTo(Handle);
        await Assert.That(page.NextOffset).IsEqualTo(Snapshot.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That(page.UpToDate).IsEqualTo("false");
        var digest = Convert.ToHexString(SHA256.HashData(FixtureBody)[..8]).ToLowerInvariant();
        await Assert.That(page.ETag).IsEqualTo($"\"{ShapeId}:{Handle}:snap.{Snapshot}.0:{Snapshot}:{digest}\"");
        await CacheControlAssert.EqualAsync(page.CacheControl);
    }

    [Test]
    public async Task A_stale_handle_is_409_must_refetch_with_the_current_handle_in_body_and_header_and_no_store()
    {
        await using var api = await StartWithFixturesAsync();

        var reply = await GetShapeAsync(api.Client, Shape(api, "?handle=h1-0000000000000000&offset=0"));

        await Assert.That(reply.Status).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(reply.Code).IsEqualTo("must_refetch");
        await Assert.That(reply.Json.GetProperty("error").GetProperty("handle").GetString()).IsEqualTo(Handle);
        await Assert.That(reply.Handle).IsEqualTo(Handle);
        await CacheControlAssert.EqualAsync(reply.CacheControl);
    }

    [Test]
    public async Task The_production_backend_bootstraps_a_real_lineage_and_refuses_fixture_handles()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var bootstrap = await GetShapeAsync(api.Client, Shape(api, "?offset=-1"));
        await Assert.That(bootstrap.Status).IsEqualTo(HttpStatusCode.TemporaryRedirect);
        await Assert.That(bootstrap.Handle).IsNotNull();
        await Assert.That(bootstrap.Handle).IsNotEqualTo(Handle);
        foreach (var query in new[]
        {
            $"?handle={Handle}&offset=0", $"?handle={Handle}&offset=9223372036854775807",
            $"?handle={Handle}&offset=snap.0.0", $"?handle={Handle}&offset=snap.2.{EncodedId}",
            $"?handle={Handle}&offset=0&live=true",
        })
        {
            var reply = await GetShapeAsync(api.Client, Shape(api, query));
            await Assert.That(reply.Status).IsEqualTo(HttpStatusCode.Conflict);
            await Assert.That(reply.Code).IsEqualTo("must_refetch");
            await CacheControlAssert.EqualAsync(reply.CacheControl);
            await Assert.That(reply.Handle).IsEqualTo(bootstrap.Handle);
        }
    }

    [Test]
    public async Task A_proven_resume_redirects_to_the_local_commit_boundary_without_caching_the_redirect()
    {
        var resume = new FixtureResume();
        await using var api = await StartWithFixturesAsync(services => services.AddSingleton<IShapeResume>(resume));
        foreach (var useHeader in new[] { false, true })
        {
            var reply = useHeader
                ? await GetShapeAsync(api.Client, Shape(api, "?offset=-1"), ("TB-Shape-Resume", ResumeToken()))
                : await GetShapeAsync(api.Client, Shape(api, $"?offset=-1&cursor={ResumeToken()}"));
            await Assert.That(reply.Status).IsEqualTo(HttpStatusCode.TemporaryRedirect);
            await CacheControlAssert.EqualAsync(reply.CacheControl);
            await Assert.That(reply.Handle).IsEqualTo(Handle);
            await Assert.That(reply.Location!.OriginalString).IsEqualTo(Shape(api, $"?handle={Handle}&offset=0"));
            await Assert.That(resume.Scope.Span.SequenceEqual(Enumerable.Range(1, 16).Select(value => (byte)value).ToArray())).IsTrue();
            await Assert.That(resume.Cursor!.Issued.Value).IsEqualTo(1UL);
            await Assert.That(resume.Cursor.Issuer.Value).IsEqualTo(1UL);
        }
    }

    [Test]
    public async Task Every_origin_answer_including_401_and_403_is_private_no_store_and_never_shared_cacheable()
    {
        await using var api = await StartWithFixturesAsync(services =>
        {
            services.AddSingleton<IShapeLiveTail>(new FixtureLiveTail());
            services.AddSingleton<IShapeResume>(new FixtureResume());
        });
        using var anonymous = api.Management.DataClient();
        using var writeOnly = await api.ClientWithScopesAsync("documents:write");
        var page = Shape(api, $"?handle={Handle}&offset=snap.{Snapshot}.0");
        var etag = (await GetShapeAsync(api.Client, page)).ETag!;
        var answers = new (HttpStatusCode Status, ShapeReply Reply)[]
        {
            (HttpStatusCode.TemporaryRedirect, await GetShapeAsync(api.Client, Shape(api, "?offset=-1"))),
            (HttpStatusCode.TemporaryRedirect, await GetShapeAsync(api.Client, Shape(api, "?offset=-1"), ("TB-Shape-Resume", ResumeToken()))),
            (HttpStatusCode.OK, await GetShapeAsync(api.Client, page)),
            (HttpStatusCode.NotModified, await GetShapeAsync(api.Client, page, ("If-None-Match", etag))),
            (HttpStatusCode.OK, await GetShapeAsync(api.Client, Shape(api, $"?handle={Handle}&offset=0"))),
            (HttpStatusCode.OK, await GetShapeAsync(api.Client, Shape(api, $"?handle={Handle}&offset=0&live=true"))),
            (HttpStatusCode.Conflict, await GetShapeAsync(api.Client, Shape(api, "?handle=h1-0000000000000000&offset=0"))),
            (HttpStatusCode.BadRequest, await GetShapeAsync(api.Client, Shape(api, "?offset=bad"))),
            (HttpStatusCode.NotFound, await GetShapeAsync(api.Client, $"{api.Collections}/missing/shape?offset=-1")),
            (HttpStatusCode.Forbidden, await GetShapeAsync(writeOnly, page, ("If-None-Match", etag))),
            (HttpStatusCode.Unauthorized, await GetShapeAsync(anonymous, page, ("If-None-Match", etag))),
            (HttpStatusCode.Unauthorized, await GetShapeAsync(anonymous, Shape(api, "/keys?handle=" + Handle))),
            (HttpStatusCode.Forbidden, await GetShapeAsync(writeOnly, Shape(api, "/keys?handle=" + Handle))),
            (HttpStatusCode.Unauthorized, await GetShapeAsync(anonymous, Shape(api, "/manifest?handle=" + Handle + "&offset=0"))),
            (HttpStatusCode.Forbidden, await GetShapeAsync(writeOnly, Shape(api, "/manifest?handle=" + Handle + "&offset=0"))),
            (HttpStatusCode.BadRequest, await GetShapeAsync(api.Client, Shape(api, "/manifest?offset=bad"))),
        };

        foreach (var (status, reply) in answers)
        {
            await Assert.That(reply.Status).IsEqualTo(status);
            await CacheControlAssert.EqualAsync(reply.CacheControl);
        }
    }

    [Test]
    public async Task The_shape_needs_documents_read_and_is_not_served_on_the_internal_listener()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        using var writeOnly = await api.ClientWithScopesAsync("documents:write");

        var denied = await GetShapeAsync(writeOnly, Shape(api, "?offset=-1"));
        var internalListener = await GetShapeAsync(api.Server.Client, Shape(api, "?offset=-1"));

        await Assert.That(denied.Status).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(denied.Code).IsEqualTo("forbidden_scope");
        await Assert.That(internalListener.Status).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task The_openapi_document_describes_getShape_with_its_parameters_scope_and_responses()
    {
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var client = tls.CreateClient(server.ClientAddress!);

        using var json = JsonDocument.Parse(await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative)));
        var get = json.RootElement.GetProperty("paths")
            .GetProperty("/v1/projects/{project}/databases/{database}/collections/{collection}/shape")
            .GetProperty("get");

        await Assert.That(get.GetProperty("operationId").GetString()).IsEqualTo("getShape");
        await Assert.That(get.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString())).Contains("Live");
        // Bearer scopes live in x-required-scopes; the security requirement only names the scheme.
        await Assert.That(get.GetProperty("x-required-scopes").EnumerateArray().Select(scope => scope.GetString())).Contains("documents:read");
        await Assert.That(get.GetProperty("security").EnumerateArray().SelectMany(requirement => requirement.EnumerateObject()).Select(scheme => scheme.Name))
            .Contains("TsAuth");
        var parameters = get.GetProperty("parameters").EnumerateArray().Select(parameter => parameter.GetProperty("name").GetString()!).ToList();
        await Assert.That(parameters).IsEquivalentTo(ExpectedParameters);
        var responses = get.GetProperty("responses").EnumerateObject().Select(response => response.Name).ToList();
        await Assert.That(ExpectedResponses.Where(code => !responses.Contains(code))).IsEmpty();
    }

    /// <summary>A tenant database whose shape lineage, snapshot pages and closed chunks are fixtures (isolated backend fixtures).</summary>
    private static Task<DocumentApi> StartWithFixturesAsync(Action<IServiceCollection>? services = null) => DocumentApi.StartAsync(services: collection =>
    {
        collection.AddSingleton<IShapeLineages>(new FixtureLineages());
        collection.AddSingleton<IShapeSnapshots>(new FixtureSnapshots());
        collection.AddSingleton<IShapeChunks>(new FixtureChunks());
        services?.Invoke(collection);
    });

    private static string ResumeToken(string? machine = null, int origins = 0)
    {
        var cursor = new FeedCursor(Enumerable.Range(1, origins).ToDictionary(value => new ReplicaId((ulong)value), _ => new Hlc(1)), new Hlc(1))
        {
            Issuer = new ReplicaId(1),
            Machine = machine,
        };
        var inner = System.Buffers.Text.Base64Url.DecodeFromChars(ChangeCursor.Encode(cursor));
        var bytes = new byte[17 + inner.Length];
        bytes[0] = 1;
        for (var index = 1; index <= 16; index++)
        {
            bytes[index] = (byte)index;
        }

        inner.CopyTo(bytes.AsSpan(17));
        return System.Buffers.Text.Base64Url.EncodeToString(bytes);
    }

    private static string Shape(DocumentApi api, string query) => $"{api.Collections}/{DocumentApi.Collection}/shape{query}";

    private static async Task<ShapeReply> GetShapeAsync(HttpClient client, string uri, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, UriKind.Relative));
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await client.SendAsync(request);
        return await ShapeReply.ReadAsync(response);
    }

    private static Task<ShapeReply> GetShapeAsync(HttpClient client, Uri uri) => GetShapeAsync(client, uri.OriginalString);

    private sealed class FixtureLineages : IShapeLineages
    {
        public ValueTask<LineageResult> ResolveAsync(ShapeScope scope, CancellationToken cancellationToken) =>
            ValueTask.FromResult<LineageResult>(new ShapeLineage(ShapeId, Handle));

        ValueTask<LineageResult> IShapeLineages.ResolveHandleAsync(TenantContext database, CollectionInfo collection, string handle, CancellationToken cancellationToken) =>
            ValueTask.FromResult<LineageResult>(new ShapeLineage(ShapeId, Handle));
    }

    private sealed class FixtureSnapshots : IShapeSnapshots
    {
        public ValueTask<SnapshotResult> NewestAsync(ShapeScope scope, ShapeLineage lineage, CancellationToken cancellationToken) =>
            ValueTask.FromResult<SnapshotResult>(Snapshot);

        public ValueTask<ShapeRead> PageAsync(ShapeScope scope, ShapeLineage lineage, long snapshot, string? afterId, CancellationToken cancellationToken) =>
            snapshot == Snapshot
                ? ValueTask.FromResult<ShapeRead>(new ShapeBody(FixtureBody, Snapshot.ToString(System.Globalization.CultureInfo.InvariantCulture), false))
                : ValueTask.FromResult<ShapeRead>(new MustRefetch("snapshot is not published for this handle"));
    }

    private sealed class FixtureChunks : IShapeChunks
    {
        public ValueTask<ShapeRead> ReadAsync(ShapeScope scope, ShapeLineage lineage, long offset, CancellationToken cancellationToken) =>
            offset > Snapshot
                ? ValueTask.FromResult<ShapeRead>(new MustRefetch("offset is above the sealed head"))
                : ValueTask.FromResult<ShapeRead>(new ShapeBody(FixtureBody, offset.ToString(System.Globalization.CultureInfo.InvariantCulture), false));
    }

    private sealed class FixtureResume : IShapeResume
    {
        public ReadOnlyMemory<byte> Scope { get; private set; }

        public TerraBase.Engine.FeedCursor? Cursor { get; private set; }

        public ValueTask<ResumeResult> ResolveAsync(
            ShapeScope scope, ShapeLineage lineage, ReadOnlyMemory<byte> scope16, TerraBase.Engine.FeedCursor cursor, CancellationToken cancellationToken)
        {
            Scope = scope16;
            Cursor = cursor;
            return ValueTask.FromResult<ResumeResult>(0L);
        }
    }

    private sealed class FixtureLiveTail : IShapeLiveTail
    {
        public ValueTask<ShapeRead> WaitAsync(ShapeScope scope, ShapeLineage lineage, long offset, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ShapeRead>(new ShapeBody(JsonSerializer.SerializeToUtf8Bytes(
                new ShapeResponse(ShapeId, Handle, "0", "0", "0", "0", true, ResumeToken(), []), ShapeJsonContext.Default.ShapeResponse), "0", true));
    }

    private sealed record ShapeReply(
        HttpStatusCode Status, string? Handle, string? CacheControl, string? Vary, string? ETag, string? ContentType,
        string? NextOffset, string? UpToDate, Uri? Location, byte[] RawBody, JsonElement Json)
    {
        public string? Code => Json.ValueKind == JsonValueKind.Object && Json.TryGetProperty("error", out var error)
            ? error.GetProperty("code").GetString()
            : null;

        public static async Task<ShapeReply> ReadAsync(HttpResponseMessage response)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync();
            JsonElement json = default;
            if (bytes.Length > 0 && bytes[0] == '{')
            {
                using var document = JsonDocument.Parse(bytes);
                json = document.RootElement.Clone();
            }

            return new ShapeReply(
                response.StatusCode,
                Header(response, "TB-Shape-Handle"),
                Header(response, "Cache-Control"),
                Header(response, "Vary"),
                response.Headers.ETag?.ToString(),
                response.Content.Headers.ContentType?.MediaType,
                Header(response, "TB-Shape-Next-Offset"),
                Header(response, "TB-Shape-Up-To-Date"),
                response.Headers.Location,
                bytes,
                json);
        }

        // Raw wire values: HttpClient reorders parsed headers such as Cache-Control.
        private static string? Header(HttpResponseMessage response, string name) =>
            response.Headers.NonValidated.TryGetValues(name, out var values) ? values.SingleOrDefault() : null;
    }
}
