using System.Net;
using System.Text;
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
    // Fixture bytes the fake page returns verbatim; the endpoint must not re-serialize them.
    private static readonly byte[] FixtureBody = Encoding.UTF8.GetBytes(
        """{"shape_id":"0123456789abcdef0123456789abcdef","handle":"h1-0123456789abcdef","offset":"snap.7.0","next_offset":"7","covered_offset":"7","values_at":"7","up_to_date":false,"changes":[{"offset":"7","ops":[]}]}""");

    private const string Handle = "h1-0123456789abcdef";
    private const string ShapeId = "0123456789abcdef0123456789abcdef";
    private const long Snapshot = 7;

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
