using System.Net;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Features.Management.Usage;
using TerraBase.Hosting;
using TerraBase.Features.Platform;
using TerraBase.Primitives;

internal sealed class UsageJourneyClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2028, 3, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

internal static class CompleteMonths
{
    public static async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var clock = (UsageJourneyClock)services.GetRequiredService<TimeProvider>();
        var catalog = services.GetRequiredService<SystemCatalog>();
        var ids = Enumerable.Range(0, 27).Select(_ => EntityIds.New().ToString()).Order(StringComparer.Ordinal).ToList();
        var month = clock.Now.AddMonths(-2);
        if (await catalog.WriteAsync<Unavailable?>(async transaction =>
        {
            foreach (var id in ids)
            {
                _ = await CatalogTables.PutOrganizationAsync(transaction, new Organization(id, id, "usage-journey", month), cancellationToken);
                await SystemCatalog.PutAsync(transaction, $"organizations/{id}/usage-months", "2028-01~journey",
                    new UsageTotals(0, id == ids[0] ? 37 : 0) { StorageByteHours = id == ids[0] ? 96 : 0 },
                    ManagementJsonContext.Default.UsageTotals, cancellationToken);
            }

            return null;
        }, static error => error, cancellationToken) is { } refused)
        {
            throw new InvalidOperationException(refused.Message);
        }

        var options = services.GetRequiredService<TerraBaseOptions>();
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{options.InternalPort}"),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        services.GetRequiredService<InternalCredential>().Sign(http.DefaultRequestHeaders);

        async Task<JsonDocument> PageAsync(string query)
        {
            using var response = await http.GetAsync("/v1/usage/allowances" + query, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK || response.Headers.GetValues("Usage-Final").Single() != "false"
                || response.Headers.CacheControl?.NoStore != true)
            {
                throw new InvalidOperationException($"allowances {query}: {response.StatusCode}");
            }

            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        }

        async Task RefusedAsync(string value)
        {
            using var response = await http.GetAsync("/v1/usage/allowances?month=" + value, cancellationToken);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            Require(response.StatusCode == HttpStatusCode.BadRequest && body.RootElement.GetProperty("error").GetProperty("code").GetString() == "invalid_request",
                "incomplete or malformed month must use the existing 400 error envelope");
        }

        using var first = await PageAsync("?month=2028-01&after=" + PlatformDatabases.OrganizationId);
        var items = first.RootElement.GetProperty("items").EnumerateArray().ToList();
        Require(first.RootElement.GetProperty("periodStart").GetDateTimeOffset() == month && items.Count == 25, "two months back and page size");
        Require(items[0].GetProperty("organizationId").GetString() == ids[0] && items[0].GetProperty("operations").GetInt64() == 37
            && items[0].GetProperty("storageByteHours").GetInt64() == 96, "persisted historical totals");
        Require(items[1].GetProperty("operations").GetInt64() == 0 && items[1].GetProperty("sqlReads").GetInt64() == 0, "persisted zero usage");
        var after = first.RootElement.GetProperty("next").GetString();
        Require(after == ids[24], "first cursor follows organization id ordering");
        using var second = await PageAsync("?month=2028-01&after=" + after);
        Require(second.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("organizationId").GetString()).SequenceEqual(ids.Skip(25))
            && second.RootElement.GetProperty("next").ValueKind == JsonValueKind.Null, "remaining page and terminal cursor");
        using var missing = await PageAsync("?month=2027-12&after=" + PlatformDatabases.OrganizationId);
        Require(missing.RootElement.GetProperty("items").EnumerateArray().All(item => item.GetProperty("operations").ValueKind == JsonValueKind.Null
            && item.GetProperty("sqlReads").ValueKind == JsonValueKind.Null && item.GetProperty("storageByteHours").ValueKind == JsonValueKind.Null), "missing history is unavailable rather than zero");
        using var current = await PageAsync("?after=" + PlatformDatabases.OrganizationId);
        Require(current.RootElement.GetProperty("periodStart").GetDateTimeOffset() == clock.Now
            && current.RootElement.GetProperty("items")[0].GetProperty("operations").GetInt64() == 0, "omitted month retains current usage");
        await RefusedAsync("2028-03");
        await RefusedAsync("2028-04");
        await RefusedAsync("2028-1");
        await RefusedAsync("2028-00");
        await RefusedAsync("");
        clock.Now = clock.Now.AddTicks(-1);
        await RefusedAsync("2028-02");
        clock.Now = clock.Now.AddTicks(1);
        using var closed = await PageAsync("?month=2028-02");
        Require(closed.RootElement.GetProperty("periodStart").GetDateTimeOffset() == clock.Now.AddMonths(-1), "first instant of the next UTC month is complete");
        Console.WriteLine("usage complete-month journey: two months back=37, persisted zero=0, absent=null, paging=25+2, current/future=400, UTC boundary and Usage-Final=false passed");
    }

    private static void Require(bool accepted, string message)
    {
        if (!accepted)
        {
            throw new InvalidOperationException(message);
        }
    }
}
