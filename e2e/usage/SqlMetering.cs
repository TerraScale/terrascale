using System.Globalization;
using System.Net;
using System.Text.Json;
using TerraBase.Features.Catalog;
using TerraBase.Features.Management.Usage;

internal static class SqlMetering
{
    public static async Task<string> RunAsync(HttpClient http, CancellationToken cancellationToken)
    {
        var tenant = await UsageJourney.TenantAsync(http, "usage-metered", cancellationToken);
        var before = await UsageJourney.UsageAsync(http, tenant.ProjectUsage, cancellationToken);
        UsageJourney.Require(UsageMetrics.All.All(metric => before[metric] == 0), "a new project starts the month with no usage: " + before);

        await AcceptedAsync(http, tenant, "CREATE TABLE hits (id INTEGER PRIMARY KEY, note TEXT NOT NULL)", false, cancellationToken);
        await AcceptedAsync(http, tenant, "INSERT INTO hits (note) VALUES ('a'), ('b'), ('c')", false, cancellationToken);
        await AcceptedAsync(http, tenant, "SELECT note FROM hits ORDER BY id", false, cancellationToken);
        await AcceptedAsync(http, tenant, "INSERT INTO hits (note) VALUES ('strong')", true, cancellationToken);
        var refused = await UsageJourney.SqlAsync(http, tenant, "SELECT * FROM missing_table", false, cancellationToken);
        UsageJourney.Require(refused == HttpStatusCode.BadRequest, "a statement against a missing table answers 400: " + refused);

        var after = await UsageJourney.UsageAsync(http, tenant.ProjectUsage, cancellationToken);
        UsageJourney.Require(after[UsageMetrics.Requests] == 4 && after[UsageMetrics.Writes] == 3 && after[UsageMetrics.Reads] == 1,
            "every accepted request counts once as a read or a write and a refused statement counts nothing: " + after);
        UsageJourney.Require(after[UsageMetrics.RowsWritten] == 4 && after[UsageMetrics.RowsRead] == 3 && after[UsageMetrics.StrongCommits] == 2,
            "rows follow the accepted statements; the schema change escalates to strong and the requested strong insert commits strongly: " + after);
        var database = await UsageJourney.UsageAsync(http, tenant.DatabaseUsage, cancellationToken);
        UsageJourney.Require(database.Same(after), "the database scope matches its only project: " + database);

        using var allowances = await UsageJourney.GetAsync(http, "/v1/usage/allowances", cancellationToken);
        var hobby = allowances.RootElement.GetProperty("plans").EnumerateArray()
            .Single(static plan => plan.GetProperty("plan").GetString() == Plans.Hobby).GetProperty("databases").GetInt32();
        UsageJourney.Require(hobby == Plans.DatabaseLimit(Plans.Hobby), "the published hobby allowance is the enforced database limit: " + hobby);
        for (var index = 1; index < hobby; index++)
        {
            _ = await UsageJourney.CreateAsync(http, $"/v1/projects/{tenant.Project}/databases", "usage-allowance-" + index.ToString(CultureInfo.InvariantCulture), cancellationToken);
        }

        using var over = await UsageJourney.PostAsync(http, $"/v1/projects/{tenant.Project}/databases", "{\"name\":\"usage-allowance-over\"}", cancellationToken);
        using var refusal = JsonDocument.Parse(await over.Content.ReadAsStringAsync(cancellationToken));
        var code = refusal.RootElement.GetProperty("error").GetProperty("code").GetString();
        UsageJourney.Require(over.StatusCode == HttpStatusCode.TooManyRequests && code == "database_limit",
            $"a hobby organization at its database allowance is refused: {over.StatusCode} {code}");

        return $"before={before} after={after} refused=400 allowance hobby={hobby} refusal=429 {code}";
    }

    private static async Task AcceptedAsync(HttpClient http, UsageTenant tenant, string sql, bool strong, CancellationToken cancellationToken)
    {
        var status = await UsageJourney.SqlAsync(http, tenant, sql, strong, cancellationToken);
        UsageJourney.Require(status == HttpStatusCode.OK, $"{sql}: {status}");
    }
}
