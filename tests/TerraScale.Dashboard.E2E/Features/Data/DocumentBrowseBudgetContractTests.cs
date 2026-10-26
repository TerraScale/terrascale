using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TerraScale.Dashboard.Features.Data;
using TerraScale.Dashboard.Platform.Mcp;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.E2E.Features.Data;

public sealed class DocumentBrowseBudgetContractTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(3)]
    [Arguments(100)]
    public async Task McpCursorPagesRetainNativeContinuationAndReturnEveryDocument(int limit)
    {
        using var wire = new Regions();
        var documents = new Documents(205);
        var tools = Tools(documents, wire);
        var ids = new List<string>();
        var after = "";
        do
        {
            var result = await tools.Documents("database", "items", limit: limit, after: after);
            ids.AddRange(result.Documents.Select(row => row.Id));
            await Assert.That(result.NextCursor).IsEqualTo(documents.LastCursor);
            await Assert.That(result.Limit).IsEqualTo(limit == 0 ? 100 : limit);
            await Assert.That(result.HasMore).IsEqualTo(result.NextCursor is not null);
            await Assert.That(result.Page is null && result.PageCount is null && result.Total is null && result.TotalIsLowerBound is null).IsTrue();
            after = result.NextCursor ?? "";
        } while (after.Length > 0);
        await Assert.That(ids.SequenceEqual(Enumerable.Range(0, 205).Select(index => index.ToString("D4")))).IsTrue();
        await Assert.That(documents.ListCalls).IsEqualTo((205 + (limit == 0 ? 100 : limit) - 1) / (limit == 0 ? 100 : limit));
    }

    [Test]
    [Arguments(" NEEDLE ", 8)]
    [Arguments("absent", 10)]
    public async Task McpEmptyFilteredPagesStillReachSparseMatchesAndTheTerminalPage(string search, int emptyPages)
    {
        using var wire = new Regions();
        var documents = new Documents(1100);
        var tools = Tools(documents, wire);
        var ids = new List<string>();
        var after = "";
        var emptyWithContinuation = 0;
        do
        {
            var result = await tools.Documents("database", "items", search: search, after: after);
            ids.AddRange(result.Documents.Select(row => row.Id));
            if (result.Documents.Count == 0 && result.HasMore) emptyWithContinuation++;
            await Assert.That(result.HasMore).IsEqualTo(result.NextCursor is not null);
            after = result.NextCursor ?? "";
        } while (after.Length > 0);
        await Assert.That(ids.SequenceEqual(search == "absent" ? Array.Empty<string>() : ["0450", "0999", "1050"])).IsTrue();
        await Assert.That(emptyWithContinuation).IsEqualTo(emptyPages);
        await Assert.That(documents.ListCalls).IsEqualTo(11);
    }

    [Test]
    [Arguments(0, "")]
    [Arguments(1, "")]
    [Arguments(3, " NEEDLE ")]
    [Arguments(100, "absent")]
    public async Task SortedMcpPagesReachTheTailWithBoundedScansAndNoPageCap(int limit, string search)
    {
        using var wire = new Regions();
        var documents = new Documents(1100);
        var tools = Tools(documents, wire);
        var ids = new List<string>();
        var after = "";
        var page = 1;
        do
        {
            var before = documents.Queries.Count;
            var result = await tools.Documents("database", "items", search: search, sort: "a/b~c", descending: true, page: page++, limit: limit, after: after);
            ids.AddRange(result.Documents.Select(row => row.Id));
            await Assert.That(result.Limit).IsEqualTo(limit == 0 ? 25 : limit);
            await Assert.That(result.Documents.Count <= result.Limit).IsTrue();
            await Assert.That(result.HasMore).IsEqualTo(result.NextCursor is not null);
            await Assert.That(result.PageCount is null && result.Total is null && result.TotalIsLowerBound is null).IsTrue();
            await Assert.That(documents.Queries[before].Cursor).IsEqualTo(after.Length == 0 ? null : after);
            await Assert.That(documents.Queries.Count - before <= (search.Length == 0 ? 1 : 4)).IsTrue();
            await Assert.That(documents.Queries.Skip(before).Sum(query => query.Limit ?? 0) <= 100).IsTrue();
            after = result.NextCursor ?? "";
        } while (after.Length > 0);
        var expected = search.Length == 0 ? Enumerable.Range(0, 1100).Select(index => index.ToString("D4")) : search == "absent" ? Array.Empty<string>() : new[] { "0450", "0999", "1050" };
        await Assert.That(ids.SequenceEqual(expected)).IsTrue();
        await Assert.That(documents.Queries.All(query => query.Sort![0].Path == "/a~1b~0c" && query.Sort[0].Order == "desc")).IsTrue();
        await Assert.That(documents.ListCalls).IsEqualTo(0);
    }

    [Test]
    [Arguments(-1, "", "", 1)]
    [Arguments(101, "", "", 1)]
    [Arguments(0, "", "", 0)]
    [Arguments(0, "", "name", int.MaxValue)]
    [Arguments(0, "", "name", 2)]
    [Arguments(3, "", "", 2)]
    [Arguments(0, "opaque:100", "", 2)]
    [Arguments(0, " ", "", 1)]
    [Arguments(0, "\n", "name", 1)]
    public async Task McpRejectsInvalidPagingBeforeProducerReads(int limit, string after, string sort, int page)
    {
        using var wire = new Regions();
        var documents = new Documents(205);
        AccountAccessException? rejected = null;
        try { await Tools(documents, wire).Documents("database", "items", limit: limit, after: after, sort: sort, page: page); }
        catch (AccountAccessException error) { rejected = error; }
        await Assert.That(rejected?.StatusCode).IsEqualTo(422);
        await Assert.That(documents.ListCalls + documents.Queries.Count).IsEqualTo(0);
    }

    [Test]
    public async Task BrowserDefaultRetainsTwentyFiveRowsAndTheExactProducerCursor()
    {
        using var wire = new Regions();
        var documents = new Documents(1100);
        var (service, context) = Service(documents, wire);
        var browse = await service.BrowseAsync(context, "database", "items", "", "a/b~c", true, 41, CancellationToken.None, "1000");
        await Assert.That(browse.Documents.Select(row => row.Id).SequenceEqual(Enumerable.Range(1000, 25).Select(index => index.ToString("D4")))).IsTrue();
        await Assert.That(browse.Cursor).IsEqualTo("1000");
        await Assert.That(browse.NextCursor).IsEqualTo("1025");
        await Assert.That(documents.Queries.Single().Limit).IsEqualTo(25);
        await Assert.That(browse.Scanned).IsEqualTo(25);
    }

    private static DataMcpTools Tools(Documents documents, Regions wire)
    {
        var (service, context) = Service(documents, wire);
        var http = new DefaultHttpContext();
        http.Items[DashboardMcp.ContextKey] = context;
        return new(new HttpContextAccessor { HttpContext = http }, service, null!, null!);
    }

    private static (DataService, AccountContext) Service(Documents documents, Regions wire)
    {
        var backend = new DatabaseBackend(wire, null!);
        var project = new ProjectRecord { Id = "project", OrganizationId = "organization", Region = "gru" };
        project.Databases.Add("database", new() { Id = "database", ProjectId = project.Id, Name = "app", Region = "gru" });
        var organization = new OrganizationRecord { Id = "organization", Members = { ["user"] = new() { UserId = "user", Role = "owner" } } };
        var context = new AccountContext(new("user", "Synthetic", "synthetic@example.test", "token", null, DateTimeOffset.UtcNow.AddMinutes(5)), new() { UserId = "user" }, [], organization, project);
        return (new(new(null!, backend), documents, backend), context);
    }

    private sealed class Regions : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this, false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"regions\":[\"gru\"]}") });
    }
    private sealed class Documents(int count) : IDocumentBackend
    {
        public List<TerraBaseQuery> Queries { get; } = [];
        public int ListCalls { get; private set; }
        public string? LastCursor { get; private set; }
        public Task<TerraBaseDocumentPage> DocumentsAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, TerraBaseQuery query, CancellationToken cancellationToken)
        {
            Queries.Add(query);
            var offset = query.Cursor is null ? 0 : int.Parse(query.Cursor, System.Globalization.CultureInfo.InvariantCulture);
            var rows = Enumerable.Range(0, count).Skip(offset).Take(query.Limit!.Value).Select(index => new TerraBaseDocument(index.ToString("D4"), "v1", "value", JsonSerializer.SerializeToElement(new { text = index is 450 or 999 or 1050 ? "needle" : "other" }))).ToArray();
            return Task.FromResult(new TerraBaseDocumentPage(rows, offset + rows.Length < count ? (offset + rows.Length).ToString(System.Globalization.CultureInfo.InvariantCulture) : null));
        }
        public Task<IReadOnlyList<TerraBaseCollection>> CollectionsAsync(DatabaseScope s, DatabaseRegion h, string d, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateCollectionAsync(DatabaseScope s, DatabaseRegion h, string d, string c, CancellationToken ct) => throw new NotSupportedException();
        public Task DropCollectionAsync(DatabaseScope s, DatabaseRegion h, string d, string c, CancellationToken ct) => throw new NotSupportedException();
        public async Task<TerraBaseDocumentPage> ListAsync(DatabaseScope s, DatabaseRegion h, string d, string c, int limit, string? cursor, CancellationToken ct)
        {
            ListCalls++;
            var page = await DocumentsAsync(s, h, d, c, new() { Limit = limit, Cursor = cursor?["opaque:".Length..] }, ct);
            LastCursor = page.NextCursor is null ? null : "opaque:" + page.NextCursor;
            return new(page.Items, LastCursor);
        }
        public Task<TerraBaseDocument?> GetAsync(DatabaseScope s, DatabaseRegion h, string d, string c, string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<TerraBaseWriteResult> WriteAsync(DatabaseScope s, DatabaseRegion h, string d, string c, string op, string id, JsonElement body, string v, CancellationToken ct) => throw new NotSupportedException();
        public Task<TerraBaseCollectionSchema> SchemaAsync(DatabaseScope s, DatabaseRegion h, string d, string c, CancellationToken ct) => throw new NotSupportedException();
        public IAsyncEnumerable<TerraBaseExportLine> ExportAsync(DatabaseScope s, DatabaseRegion h, string d, CancellationToken ct) => throw new NotSupportedException();
    }
}
