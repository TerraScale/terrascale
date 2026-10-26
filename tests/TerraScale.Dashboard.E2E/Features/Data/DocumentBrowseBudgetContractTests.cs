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
    [Arguments(31, 1, "", 1, 2, 26, true, true)]
    [Arguments(31, 2, "", 2, 2, 31, false, false)]
    [Arguments(31, 1, "other", 1, 2, 31, false, true)]
    [Arguments(25, 40, "", 1, 1, 25, false, false)]
    [Arguments(0, 1, "", 1, 1, 0, false, false)]
    [Arguments(1100, 40, "", 40, 40, 1000, true, true)]
    [Arguments(1100, 1, "absent", 1, 1, 0, true, true)]
    public async Task McpBrowserPagesExposeActualPageAndHonestTotals(int count, int requested, string search, int page, int pages, int total, bool lowerBound, bool hasMore)
    {
        using var wire = new Regions();
        var documents = new Documents(count);
        var result = await Tools(documents, wire).Documents("database", "items", search: search, sort: "a/b~c", page: requested);
        await Assert.That(result.Page).IsEqualTo(page);
        await Assert.That(result.PageCount).IsEqualTo(pages);
        await Assert.That(result.Total).IsEqualTo(total);
        await Assert.That(result.TotalIsLowerBound).IsEqualTo(lowerBound);
        await Assert.That(result.HasMore).IsEqualTo(hasMore);
        await Assert.That(result.NextCursor).IsNull();
        await Assert.That(result.Documents.Count).IsEqualTo(Math.Min(25, Math.Max(0, total - (page - 1) * 25)));
        await Assert.That(documents.ListCalls).IsEqualTo(0);
    }

    [Test]
    [Arguments(-1, "", "", false, 1)]
    [Arguments(101, "", "", false, 1)]
    [Arguments(0, "", "", false, 0)]
    [Arguments(0, "", "", false, 41)]
    [Arguments(3, "", "name", false, 1)]
    [Arguments(0, "opaque:100", "name", false, 1)]
    [Arguments(3, "", "", true, 1)]
    [Arguments(0, "opaque:100", "", true, 1)]
    [Arguments(3, "", "", false, 2)]
    [Arguments(0, "opaque:100", "", false, 2)]
    public async Task McpRejectsInvalidPagingAndIgnoredArgumentCombinations(int limit, string after, string sort, bool descending, int page)
    {
        using var wire = new Regions();
        var documents = new Documents(205);
        AccountAccessException? rejected = null;
        try { await Tools(documents, wire).Documents("database", "items", limit: limit, after: after, sort: sort, descending: descending, page: page); }
        catch (AccountAccessException error) { rejected = error; }
        await Assert.That(rejected?.StatusCode).IsEqualTo(422);
        await Assert.That(documents.ListCalls + documents.Queries.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(1, 1, 26, 0)]
    [Arguments(2, 2, 51, 25)]
    [Arguments(int.MinValue, 1, 26, 0)]
    [Arguments(int.MaxValue, 40, 1000, 975)]
    public async Task BrowseReadsOnlyThroughTheRequestedPageAndOneLookahead(int requested, int page, int budget, int first)
    {
        var documents = new Documents(1100);
        var browse = await BrowseAsync(documents, requested, "  ");
        await Assert.That(browse.Page).IsEqualTo(page);
        await Assert.That(browse.Documents.Count).IsEqualTo(25);
        await Assert.That(browse.Documents[0].Id).IsEqualTo(first.ToString("D4"));
        await Assert.That(browse.Documents[^1].Id).IsEqualTo((first + 24).ToString("D4"));
        await Assert.That(documents.Queries.Sum(query => query.Limit ?? 0)).IsEqualTo(budget);
        await Assert.That(documents.Queries.Count).IsEqualTo((budget + 99) / 100);
        await Assert.That(documents.Queries[0].Limit).IsEqualTo(Math.Min(100, budget));
        await Assert.That(documents.Queries.All(query => query.Sort![0].Path == "/a~1b~0c" && query.Sort[0].Order == "desc")).IsTrue();
        await Assert.That(browse.PageCount).IsEqualTo(Math.Min(40, page + 1));
        await Assert.That(browse.More).IsTrue();
        await Assert.That(Summary(browse)).Contains("at least");
    }

    [Test]
    [Arguments(70, false, 0)]
    [Arguments(1100, true, 2)]
    public async Task TextSearchIncludesLaterDocumentsAndDisclosesTheScanBoundary(int count, bool more, int matches)
    {
        var documents = new Documents(count);
        var browse = await BrowseAsync(documents, int.MaxValue, " NEEDLE ");
        await Assert.That(browse.Matched).IsEqualTo(matches);
        await Assert.That(browse.Documents.Select(row => row.Id).SequenceEqual(matches > 0 ? ["0450", "0999"] : Array.Empty<string>())).IsTrue();
        await Assert.That(documents.Queries.Sum(query => query.Limit ?? 0)).IsEqualTo(count >= 1000 ? 1000 : 100);
        await Assert.That(documents.Queries.Count).IsEqualTo((Math.Min(count, 1000) + 99) / 100);
        await Assert.That(browse.More).IsEqualTo(more);
        await Assert.That(browse.PageCount).IsEqualTo(1);
        await Assert.That(Summary(browse, "NEEDLE").Contains("partial search", StringComparison.Ordinal)).IsEqualTo(more);
    }

    [Test]
    [Arguments(0, 1)]
    [Arguments(25, 1)]
    [Arguments(50, 2)]
    public async Task ExhaustedCollectionsRetainExactTotalsAndClampToTheLastPage(int count, int page)
    {
        var browse = await BrowseAsync(new Documents(count), 40, "");
        await Assert.That(browse.Matched).IsEqualTo(count);
        await Assert.That(browse.Page).IsEqualTo(page);
        await Assert.That(browse.PageCount).IsEqualTo(page);
        await Assert.That(browse.More).IsFalse();
        await Assert.That(Summary(browse)).Contains($"of {count} documents · page {page} of {page}");
    }

    private static string Summary(DocumentBrowse browse, string search = "") => new DataPage(null!, null!, "documents", "items", [], browse, search).Summary;
    private static async Task<DocumentBrowse> BrowseAsync(Documents documents, int page, string search)
    {
        using var wire = new Regions();
        var (service, context) = Service(documents, wire);
        return await service.BrowseAsync(context, "database", "items", search, "a/b~c", true, page, CancellationToken.None);
    }

    private static DataMcpTools Tools(Documents documents, Regions wire)
    {
        var (service, context) = Service(documents, wire);
        var http = new DefaultHttpContext();
        http.Items[DashboardMcp.ContextKey] = context;
        return new(new HttpContextAccessor { HttpContext = http }, service, null!);
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
