using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Callers;
using TerraBase.Features.Management.Usage;
using TerraBase.Hosting;

internal sealed class UsageJourneyClock : TimeProvider
{
    public static readonly DateTimeOffset Boundary = new(2028, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly Lock _gate = new();

    private DateTimeOffset? _pinned;

    public void Pin(DateTimeOffset at)
    {
        lock (_gate)
        {
            _pinned = at;
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _pinned ?? TimeProvider.System.GetUtcNow();
        }
    }
}

internal sealed record UsageTenant(string Organization, string Project, string Database)
{
    public string ProjectUsage => $"/v1/projects/{Project}/usage";

    public string DatabaseUsage => $"/v1/projects/{Project}/databases/{Database}/usage";
}

internal sealed record UsageReading(string Month, IReadOnlyDictionary<string, long> Metrics)
{
    public long this[string metric] => Metrics[metric];

    public bool Same(UsageReading other) =>
        Month == other.Month && UsageMetrics.All.All(metric => Metrics[metric] == other.Metrics[metric]);

    public override string ToString() =>
        string.Concat(Month, "{", string.Join(',', UsageMetrics.All.Select(metric => string.Concat(metric, "=", Metrics[metric].ToString(CultureInfo.InvariantCulture)))), "}");
}

internal static class CompleteMonths
{
    private const string History = "2028-01";

    private const string Closed = "2028-02";

    private const string Current = "2028-03";

    public static async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var clock = (UsageJourneyClock)services.GetRequiredService<TimeProvider>();
        clock.Pin(UsageJourneyClock.Boundary);
        using var http = UsageJourney.Client(services);
        var tenant = await UsageJourney.TenantAsync(http, "usage-months", cancellationToken);
        var scope = UsageMeter.ProjectScope(Ulid.Parse(tenant.Project));
        await SeedAsync(services.GetRequiredService<SystemDatabase>(), scope, cancellationToken);

        var history = await UsageJourney.UsageAsync(http, tenant.ProjectUsage + "?month=" + History, cancellationToken);
        UsageJourney.Require(history.Month == History && history[UsageMetrics.Requests] == 37 && history[UsageMetrics.Writes] == 37
            && history[UsageMetrics.RowsWritten] == 96 && history[UsageMetrics.Reads] == 0, "persisted history two months back: " + history);
        var absent = await UsageJourney.UsageAsync(http, tenant.ProjectUsage + "?month=2027-12", cancellationToken);
        UsageJourney.Require(absent.Month == "2027-12" && UsageMetrics.All.All(metric => absent[metric] == 0), "a month without history reports zero for every metric: " + absent);

        clock.Pin(UsageJourneyClock.Boundary.AddTicks(-1));
        UsageJourney.Require(await UsageJourney.SqlAsync(http, tenant, "CREATE TABLE ledger (id INTEGER PRIMARY KEY, note TEXT NOT NULL)", false, cancellationToken) == HttpStatusCode.OK,
            "the last instant of February accepts a write");
        clock.Pin(UsageJourneyClock.Boundary);
        UsageJourney.Require(await UsageJourney.SqlAsync(http, tenant, "SELECT count(*) FROM ledger", false, cancellationToken) == HttpStatusCode.OK,
            "the first instant of March accepts a read");
        var closed = await UsageJourney.UsageAsync(http, tenant.ProjectUsage + "?month=" + Closed, cancellationToken);
        UsageJourney.Require(closed[UsageMetrics.Requests] == 1 && closed[UsageMetrics.Writes] == 1 && closed[UsageMetrics.Reads] == 0,
            "a write one tick before the UTC month boundary belongs to the closing month: " + closed);
        var current = await UsageJourney.UsageAsync(http, tenant.ProjectUsage, cancellationToken);
        UsageJourney.Require(current.Month == Current && current[UsageMetrics.Requests] == 1 && current[UsageMetrics.Reads] == 1 && current[UsageMetrics.Writes] == 0
            && current[UsageMetrics.RowsRead] == 1, "an omitted month reads the current UTC month: " + current);
        var database = await UsageJourney.UsageAsync(http, tenant.DatabaseUsage + "?month=" + Closed, cancellationToken);
        UsageJourney.Require(database.Same(closed), "the database scope matches its only project in the closing month: " + database);

        foreach (var month in new[] { "2028-1", "2028-00", "2028-13", "28-01", "March" })
        {
            await UsageJourney.RefusedAsync(http, tenant.ProjectUsage + "?month=" + month, cancellationToken);
        }

        var months = await FlushedAsync(http, tenant, cancellationToken);
        UsageJourney.Require(months.Select(static reading => reading.Month).SequenceEqual([History, Closed, Current]),
            "flushed counters persist under the month they were recorded in: " + string.Join(' ', months));
        UsageJourney.Require(months[0].Same(history) && months[1].Same(closed) && months[2].Same(current), "analytics agree with the monthly reads: " + string.Join(' ', months));

        Console.WriteLine($"usage complete-month journey: history {History} writes=37, absent month=0, write at boundary-1tick in {Closed}, read at boundary in {Current}, invalid month=400, flushed months {History},{Closed},{Current} passed");
        Console.WriteLine("usage sql metering journey: " + await SqlMetering.RunAsync(http, cancellationToken));
        Console.WriteLine("usage organization journey: " + await OrganizationUsage.RunAsync(services, http, cancellationToken));
    }

    private static async Task<IReadOnlyList<UsageReading>> FlushedAsync(HttpClient http, UsageTenant tenant, CancellationToken cancellationToken)
    {
        List<UsageReading> months = [];
        for (var attempt = 0; attempt < 60; attempt++)
        {
            using var analytics = await UsageJourney.GetAsync(http, $"/v1/projects/{tenant.Project}/analytics", cancellationToken);
            months = [.. analytics.RootElement.GetProperty("months").EnumerateArray().Select(UsageJourney.Reading)];
            if (months.Any(static reading => reading.Month == Closed))
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        return months;
    }

    private static async Task SeedAsync(SystemDatabase system, string scope, CancellationToken cancellationToken)
    {
        SqlStatement[] statements =
        [
            .. new (string Metric, long Value)[] { (UsageMetrics.Requests, 37), (UsageMetrics.Writes, 37), (UsageMetrics.RowsWritten, 96) }.Select(seed => SqlBuild.Statement(
                "INSERT INTO usage_counters (scope, month, metric, value) VALUES (?, ?, ?, ?)",
                SqlBuild.Text(scope), SqlBuild.Text(History), SqlBuild.Text(seed.Metric), SqlBuild.Integer(seed.Value))),
        ];
        var written = await system.WriteAsync(statements, ConsistencyMode.Fast, cancellationToken);
        var refused = written switch
        {
            SqlWriteResult => null,
            StateConflict conflict => conflict.Code,
            BackendUnavailable unavailable => unavailable.Code,
        };
        UsageJourney.Require(refused is null, "seeding usage history failed: " + refused);
    }
}

internal static class UsageJourney
{
    public static HttpClient Client(IServiceProvider services)
    {
        var options = services.GetRequiredService<TerraBaseOptions>();
        var http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{options.InternalPort}"),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        services.GetRequiredService<InternalCredential>().Sign(http.DefaultRequestHeaders);
        http.DefaultRequestHeaders.Add(CallerAuthentication.UserHeader, Ulid.NewUlid().ToString());
        return http;
    }

    public static async Task<UsageTenant> TenantAsync(HttpClient http, string name, CancellationToken cancellationToken)
    {
        var organization = await CreateAsync(http, "/v1/management/organizations", name, cancellationToken);
        var project = await CreateAsync(http, $"/v1/management/organizations/{organization}/projects", name, cancellationToken);
        var database = await CreateAsync(http, $"/v1/projects/{project}/databases", name, cancellationToken);
        return new UsageTenant(organization, project, database);
    }

    public static async Task<string> CreateAsync(HttpClient http, string path, string name, CancellationToken cancellationToken)
    {
        using var response = await PostAsync(http, path, string.Concat("{\"name\":\"", JsonEncodedText.Encode(name).ToString(), "\"}"), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Require(response.StatusCode == HttpStatusCode.Created, $"POST {path}: {response.StatusCode} {body}");
        using var created = JsonDocument.Parse(body);
        return created.RootElement.GetProperty("id").GetString() is { } id ? id : throw new InvalidOperationException("created resource without id at " + path);
    }

    public static async Task<HttpResponseMessage> PostAsync(HttpClient http, string path, string json, CancellationToken cancellationToken)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await http.PostAsync(path, content, cancellationToken);
    }

    public static async Task<HttpStatusCode> SqlAsync(HttpClient http, UsageTenant tenant, string sql, bool strong, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/projects/{tenant.Project}/databases/{tenant.Database}/sql")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent(string.Concat("{\"statements\":[{\"sql\":\"", JsonEncodedText.Encode(sql).ToString(), "\"}]}"), Encoding.UTF8, "application/json"),
        };
        if (strong)
        {
            request.Headers.Add(ConsistencyHeaders.Consistency, ConsistencyHeaders.StrongValue);
        }

        using var response = await http.SendAsync(request, cancellationToken);
        return response.StatusCode;
    }

    public static async Task<JsonDocument> GetAsync(HttpClient http, string path, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(path, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Require(response.StatusCode == HttpStatusCode.OK, $"GET {path}: {response.StatusCode} {body}");
        return JsonDocument.Parse(body);
    }

    public static async Task<UsageReading> UsageAsync(HttpClient http, string path, CancellationToken cancellationToken)
    {
        using var body = await GetAsync(http, path, cancellationToken);
        return Reading(body.RootElement);
    }

    public static UsageReading Reading(JsonElement usage)
    {
        var metrics = usage.GetProperty("metrics");
        return new UsageReading(usage.GetProperty("month").GetString() is { } month ? month : throw new InvalidOperationException("usage without month"),
            UsageMetrics.All.ToDictionary(static metric => metric, metric => metrics.GetProperty(metric).GetInt64(), StringComparer.Ordinal));
    }

    public static async Task RefusedAsync(HttpClient http, string path, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(path, cancellationToken);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        Require(response.StatusCode == HttpStatusCode.BadRequest && body.RootElement.GetProperty("error").GetProperty("code").GetString() == "invalid_month",
            "a malformed month answers 400 invalid_month: " + path);
    }

    public static void Require(bool accepted, string message)
    {
        if (!accepted)
        {
            throw new InvalidOperationException(message);
        }
    }
}
