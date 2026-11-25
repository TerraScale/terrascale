using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Features.Management.Usage;
using TerraBase.Hosting;
using TerraScale.Contracts.ControlPlane;

internal static class OrganizationUsage
{
    private const string Month = "2028-03";

    public static async Task<string> RunAsync(IServiceProvider services, HttpClient http, CancellationToken cancellationToken)
    {
        using var peer = PeerClient(services);
        using var anonymous = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = peer.BaseAddress,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        var organizationId = await UsageJourney.CreateAsync(http, "/v1/management/organizations", "usage-org", cancellationToken);
        using (var plan = new StringContent("{\"plan\":\"starter\"}", Encoding.UTF8, "application/json"))
        {
            using var planned = await peer.PutAsync($"/admin/organizations/{organizationId}/plan", plan, cancellationToken);
            UsageJourney.Require(planned.IsSuccessStatusCode, "an operator moves the organization to a plan with two databases: " + planned.StatusCode);
        }

        var first = await ProjectAsync(http, organizationId, "usage-org-first", cancellationToken);
        var second = await ProjectAsync(http, organizationId, "usage-org-second", cancellationToken);
        var path = $"/v1/usage/organizations/{first.Organization}?month={Month}";

        var empty = await ReadAsync(peer, path, cancellationToken);
        UsageJourney.Require(empty.Projects.Count == 2 && empty.Totals.All(static metric => metric.Value == 0),
            "a new organization lists its projects with zero usage: " + empty);

        await SqlAsync(http, first, "CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT NOT NULL)", cancellationToken);
        await SqlAsync(http, first, "INSERT INTO notes (body) VALUES ('a'), ('b')", cancellationToken);
        await SqlAsync(http, first, "SELECT body FROM notes ORDER BY id", cancellationToken);
        await SqlAsync(http, second, "CREATE TABLE tags (id INTEGER PRIMARY KEY, name TEXT NOT NULL)", cancellationToken);
        await SqlAsync(http, second, "INSERT INTO tags (name) VALUES ('x')", cancellationToken);

        var metered = await ReadAsync(peer, path, cancellationToken);
        var firstUsage = await UsageJourney.UsageAsync(http, first.ProjectUsage + "?month=" + Month, cancellationToken);
        var secondUsage = await UsageJourney.UsageAsync(http, second.ProjectUsage + "?month=" + Month, cancellationToken);
        UsageJourney.Require(metered.Complete && metered.Gaps.Count == 0 && metered.Month == Month && metered.AsOf == UsageJourneyClock.Boundary,
            "a single healthy node answers complete usage as of the read: " + metered);
        UsageJourney.Require(metered.Project(first.Project)[UsageMetrics.Requests] == 3 && metered.Project(first.Project)[UsageMetrics.Writes] == 2
            && metered.Project(first.Project)[UsageMetrics.Reads] == 1 && metered.Project(first.Project)[UsageMetrics.RowsWritten] == 2
            && metered.Project(first.Project)[UsageMetrics.RowsRead] == 2 && metered.Project(second.Project)[UsageMetrics.Requests] == 2
            && metered.Project(second.Project)[UsageMetrics.Writes] == 2 && metered.Project(second.Project)[UsageMetrics.RowsWritten] == 1,
            "each project reflects the SQL it ran: " + metered);
        UsageJourney.Require(UsageMetrics.All.All(metric => metered.Project(first.Project)[metric] == firstUsage[metric] && metered.Project(second.Project)[metric] == secondUsage[metric]
            && metered.Totals[metric] == firstUsage[metric] + secondUsage[metric]), "per-project rows match the project usage reads and totals add them: " + metered);

        var sampler = services.GetRequiredService<StorageSampler>();
        var sampleOutcome = await sampler.SampleAsync(UsageJourneyClock.Boundary.AddHours(1), cancellationToken);
        if (sampleOutcome switch { StorageSample taken => taken, BackendUnavailable => null } is not { Month: Month, Databases: > 0, ByteHours: > 0, Complete: true } sample)
        {
            throw new InvalidOperationException("the owning node samples stored databases for the hour that ended");
        }

        var stored = await ReadAsync(peer, path, cancellationToken);
        var firstDatabase = await UsageJourney.UsageAsync(http, first.DatabaseUsage + "?month=" + Month, cancellationToken);
        var secondDatabaseUsage = await UsageJourney.UsageAsync(http, second.DatabaseUsage + "?month=" + Month, cancellationToken);
        var firstBytes = stored.Project(first.Project)[UsageMetrics.StorageByteHours];
        var secondBytes = stored.Project(second.Project)[UsageMetrics.StorageByteHours];
        UsageJourney.Require(stored.Complete && firstBytes > 0 && secondBytes > 0 && firstBytes == firstDatabase[UsageMetrics.StorageByteHours]
            && secondBytes == secondDatabaseUsage[UsageMetrics.StorageByteHours] && stored.Totals[UsageMetrics.StorageByteHours] == firstBytes + secondBytes,
            "one hour of storage is counted once per database and adds up per project and organization: " + stored);
        UsageJourney.Require(sample.ByteHours >= firstBytes + secondBytes, "the node sample covers the organization's databases: " + sample);

        var repeatOutcome = await sampler.SampleAsync(UsageJourneyClock.Boundary.AddHours(1), cancellationToken);
        if (repeatOutcome switch { StorageSample taken => taken, BackendUnavailable => null } is not { Month: Month, Databases: 0, ByteHours: 0, Complete: true })
        {
            throw new InvalidOperationException("sampling the same UTC hour again records nothing");
        }

        var repeated = await ReadAsync(peer, path, cancellationToken);
        UsageJourney.Require(repeated.Project(first.Project)[UsageMetrics.StorageByteHours] == firstBytes && repeated.Project(second.Project)[UsageMetrics.StorageByteHours] == secondBytes
            && repeated.Totals[UsageMetrics.StorageByteHours] == firstBytes + secondBytes && repeated.Complete,
            "a second sample of the same UTC hour adds no storage: " + repeated);

        var unsampled = await ReadAsync(peer, $"/v1/usage/organizations/{first.Organization}?month=2028-02", cancellationToken);
        UsageJourney.Require(!unsampled.Complete && unsampled.Gaps.SequenceEqual([UsageReports.StorageIncomplete]),
            "a month whose hours were never sampled is not complete: " + unsampled);

        var clock = (UsageJourneyClock)services.GetRequiredService<TimeProvider>();
        clock.Pin(UsageJourneyClock.Boundary.AddHours(2).AddMinutes(20));
        var ghost = new DatabaseRecord(Ulid.NewUlid(), Ulid.Parse(first.Project, CultureInfo.InvariantCulture), "usage-org-unwritten",
            [services.GetRequiredService<TerraBaseOptions>().Region], false, ConsistencyMode.Fast, false, ControlDatabaseState.Active,
            services.GetRequiredService<DatabaseKeyring>().ActiveKeyId, null, null, UsageJourneyClock.Boundary, null, 1, 0);
        var inserted = await services.GetRequiredService<CatalogStore>().InsertDatabaseAsync(ghost, cancellationToken);
        UsageJourney.Require(inserted switch { DatabaseRecord => true, StateConflict or BackendUnavailable => false },
            "a database is registered in the catalog with no file on this node");
        if (await sampler.SampleAsync(UsageJourneyClock.Boundary.AddHours(3), cancellationToken) switch { StorageSample taken => taken, BackendUnavailable => null } is not { Complete: true })
        {
            throw new InvalidOperationException("a later hour is sampled while an earlier one was missed");
        }

        var unwritten = await UsageJourney.UsageAsync(http, $"/v1/projects/{first.Project}/databases/{ghost.Id}/usage?month={Month}", cancellationToken);
        UsageJourney.Require(unwritten[UsageMetrics.StorageByteHours] > 0,
            "a home database without a local file is materialized and billed for the hour instead of making storage incomplete: " + unwritten);
        var skipped = await ReadAsync(peer, path, cancellationToken);
        UsageJourney.Require(!skipped.Complete && skipped.Gaps.SequenceEqual([UsageReports.StorageIncomplete]),
            "a missed earlier hour is incomplete even when a later hour brings the count up to the elapsed hours: " + skipped);
        if (await sampler.SampleAsync(UsageJourneyClock.Boundary.AddHours(2), cancellationToken) switch { StorageSample taken => taken, BackendUnavailable => null } is not { Complete: true })
        {
            throw new InvalidOperationException("the missed hour is sampled late");
        }

        var filled = await ReadAsync(peer, path, cancellationToken);
        UsageJourney.Require(filled.Complete && filled.Gaps.Count == 0 && filled.Totals[UsageMetrics.StorageByteHours] > stored.Totals[UsageMetrics.StorageByteHours],
            "every elapsed hour sampled makes storage complete again: " + filled);
        clock.Pin(UsageJourneyClock.Boundary);

        var future = await ReadAsync(peer, $"/v1/usage/organizations/{first.Organization}?month=9999-12", cancellationToken);
        UsageJourney.Require(future.Complete && future.Totals.All(static metric => metric.Value == 0), "the last representable month has no usage and no elapsed hours: " + future);

        var current = await ReadAsync(peer, $"/v1/usage/organizations/{first.Organization}", cancellationToken);
        UsageJourney.Require(current.Month == Month, "an omitted month reads the current UTC month: " + current.Month);
        var unknown = await ReadAsync(peer, $"/v1/usage/organizations/{Ulid.NewUlid()}?month={Month}", cancellationToken);
        UsageJourney.Require(unknown.Complete && unknown.Projects.Count == 0, "an organization without projects has no usage: " + unknown);

        var refusals = new List<string>();
        foreach (var month in new[] { "2028-3", "2028-13", "March" })
        {
            refusals.Add(await RefusedAsync(peer, $"/v1/usage/organizations/{first.Organization}?month={month}", HttpStatusCode.BadRequest, "invalid_month", cancellationToken));
        }

        foreach (var organization in new[] { "not-an-organization", first.Organization.ToLowerInvariant() })
        {
            refusals.Add(await RefusedAsync(peer, $"/v1/usage/organizations/{organization}?month={Month}", HttpStatusCode.BadRequest, "invalid_organization", cancellationToken));
        }

        refusals.Add(await RefusedAsync(anonymous, path, HttpStatusCode.Unauthorized, "unauthorized", cancellationToken));
        refusals.Add(await RefusedAsync(http, path, HttpStatusCode.Forbidden, "forbidden_caller", cancellationToken));
        using var keyed = PeerClient(services);
        keyed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await ApiKeyAsync(peer, first, cancellationToken));
        refusals.Add(await RefusedAsync(keyed, path, HttpStatusCode.Forbidden, "forbidden_caller", cancellationToken));

        return string.Create(CultureInfo.InvariantCulture,
            $"projects=2 requests={metered.Totals[UsageMetrics.Requests]} writes={metered.Totals[UsageMetrics.Writes]} reads={metered.Totals[UsageMetrics.Reads]} complete={metered.Complete} storage counted once per hour, unsampled month and missed hour incomplete, refusals={string.Join(',', refusals)}");
    }

    private static async Task<UsageTenant> ProjectAsync(HttpClient http, string organization, string name, CancellationToken cancellationToken)
    {
        var project = await UsageJourney.CreateAsync(http, $"/v1/management/organizations/{organization}/projects", name, cancellationToken);
        return new UsageTenant(organization, project, await UsageJourney.CreateAsync(http, $"/v1/projects/{project}/databases", name, cancellationToken));
    }

    private static HttpClient PeerClient(IServiceProvider services)
    {
        var options = services.GetRequiredService<TerraBaseOptions>();
        var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{options.InternalPort}"),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        services.GetRequiredService<InternalCredential>().Sign(client.DefaultRequestHeaders);
        return client;
    }

    private static async Task SqlAsync(HttpClient http, UsageTenant tenant, string sql, CancellationToken cancellationToken)
    {
        var status = await UsageJourney.SqlAsync(http, tenant, sql, false, cancellationToken);
        UsageJourney.Require(status == HttpStatusCode.OK, $"{sql}: {status}");
    }

    private static async Task<OrganizationReading> ReadAsync(HttpClient http, string path, CancellationToken cancellationToken)
    {
        using var body = await UsageJourney.GetAsync(http, path, cancellationToken);
        var root = body.RootElement;
        var projects = root.GetProperty("projects").EnumerateArray()
            .ToDictionary(static project => Text(project.GetProperty("projectId")), Metrics, StringComparer.Ordinal);
        return new OrganizationReading(Text(root.GetProperty("month")), root.GetProperty("complete").GetBoolean(),
            DateTimeOffset.Parse(Text(root.GetProperty("asOf")), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            [.. root.GetProperty("gaps").EnumerateArray().Select(Text)], Metrics(root.GetProperty("totals")), projects);
    }

    private static Dictionary<string, long> Metrics(JsonElement usage) => new(StringComparer.Ordinal)
    {
        [UsageMetrics.Requests] = usage.GetProperty("requests").GetInt64(),
        [UsageMetrics.Reads] = usage.GetProperty("reads").GetInt64(),
        [UsageMetrics.Writes] = usage.GetProperty("writes").GetInt64(),
        [UsageMetrics.RowsRead] = usage.GetProperty("rowsRead").GetInt64(),
        [UsageMetrics.RowsWritten] = usage.GetProperty("rowsWritten").GetInt64(),
        [UsageMetrics.StrongCommits] = usage.GetProperty("strongCommits").GetInt64(),
        [UsageMetrics.StorageByteHours] = usage.GetProperty("storageByteHours").GetInt64(),
    };

    private static async Task<string> RefusedAsync(HttpClient http, string path, HttpStatusCode expected, string code, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(path, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        using var body = JsonDocument.Parse(text);
        UsageJourney.Require(response.StatusCode == expected && body.RootElement.GetProperty("error").GetProperty("code").GetString() == code,
            $"GET {path} answers {(int)expected} {code}: {(int)response.StatusCode} {text}");
        return ((int)expected).ToString(CultureInfo.InvariantCulture);
    }

    private static async Task<string> ApiKeyAsync(HttpClient peer, UsageTenant tenant, CancellationToken cancellationToken)
    {
        using var response = await UsageJourney.PostAsync(peer, "/admin/api-keys",
            $"{{\"name\":\"usage-org\",\"scopes\":[\"{Scopes.DatabasesRead}\"],\"organizationId\":\"{tenant.Organization}\",\"projectId\":\"{tenant.Project}\"}}", cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        UsageJourney.Require(response.StatusCode == HttpStatusCode.Created, "an operator issues a customer API key: " + text);
        using var created = JsonDocument.Parse(text);
        return Text(created.RootElement.GetProperty("secret"));
    }

    private static string Text(JsonElement value) => value.GetString() is { } text ? text : throw new InvalidOperationException("a usage field is not text");
}

internal sealed record OrganizationReading(
    string Month, bool Complete, DateTimeOffset AsOf, IReadOnlyList<string> Gaps, IReadOnlyDictionary<string, long> Totals, IReadOnlyDictionary<string, Dictionary<string, long>> Projects)
{
    public IReadOnlyDictionary<string, long> Project(string id) => Projects[id];

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{Month} complete={Complete} gaps={string.Join('|', Gaps)} totals={{{string.Join(',', Totals.Select(static metric => $"{metric.Key}={metric.Value}"))}}} projects={Projects.Count}");
}
