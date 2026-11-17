using System.Globalization;
using System.Net;
using System.Text.Json;
using TerraBase.Errors;
using TerraBase.Features.Functions;
using TerraBase.Features.Management;
using TerraBase.Features.Management.Usage;
using TerraBase.Primitives;
using TerraBase.Tenancy;

internal static class FunctionInvocations
{
    private const string Counting = """async function handler(event) { await sql.execute("CREATE TABLE IF NOT EXISTS hits (id INTEGER PRIMARY KEY)"); await sql.execute("INSERT INTO hits DEFAULT VALUES"); return { ok: true }; }""";

    private const string Failing = """async function handler(event) { throw new Error("handler failed on purpose"); }""";

    private const string Idle = """async function handler(event) { return { ok: true }; }""";

    public static async Task<string> RunAsync(IServiceProvider services, HttpClient http, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var catalog = services.GetRequiredService<SystemCatalog>();
        var functions = services.GetRequiredService<IFunctions>();
        var meter = services.GetRequiredService<UsageMeter>();
        var month = now.ToString("yyyy-MM", CultureInfo.InvariantCulture);

        var metered = await TenantAsync(services, "invocations", cancellationToken);
        var before = await UsageAsync(http, metered.OrganizationId, cancellationToken);
        Require(before.GetProperty("operations").GetInt64() == 0 && before.GetProperty("invocations").GetInt64() == 0
            && before.GetProperty("writeUnits").GetInt64() == 0, "a new organization starts the month with no operations");
        await DeployAsync(functions, metered, "counting", Counting, cancellationToken);
        await DeployAsync(functions, metered, "failing", Failing, cancellationToken);
        const int Invoked = 3;
        for (var i = 0; i < Invoked; i++)
        {
            Require(await functions.InvokeAsync(metered, "counting", "http", "{}", null, cancellationToken) is FunctionResponse { Status: 200 },
                "the counting handler answers 200");
        }

        Require(await functions.InvokeAsync(metered, "failing", "http", "{}", null, cancellationToken) is FunctionResponse { Status: 500 },
            "a handler error answers 500");
        Require(await functions.InvokeAsync(metered, "missing", "http", "{}", null, cancellationToken) is NotFound, "a missing function is not found");
        await meter.FlushAsync(cancellationToken);
        var persisted = await catalog.ReadAsync<UsageTotals?>(async transaction =>
            (await SystemCatalog.ListAllAsync(transaction, $"organizations/{metered.OrganizationId}/usage-months", ManagementJsonContext.Default.UsageTotals, cancellationToken))
                .SingleOrDefault(), static _ => null, cancellationToken);
        if (persisted is not { Invocations: Invoked + 1, Writes: > 0 } saved)
        {
            throw new InvalidOperationException("the flushed organization month holds every started invocation");
        }

        var after = await UsageAsync(http, metered.OrganizationId, cancellationToken);
        var writes = after.GetProperty("writeUnits").GetInt64();
        var invocations = after.GetProperty("invocations").GetInt64();
        var operations = after.GetProperty("operations").GetInt64();
        Require(invocations == Invoked + 1 && writes == saved.Writes && operations == writes + invocations,
            "operations are the write units plus one unit per started invocation; errors count, a missing function does not");

        var capped = await TenantAsync(services, "capped", cancellationToken);
        if (await catalog.WriteAsync<Unavailable?>(async transaction =>
        {
            await SystemCatalog.PutAsync(transaction, $"organizations/{capped.OrganizationId}/usage-months", month + "~journey",
                new UsageTotals(0, 49_999), ManagementJsonContext.Default.UsageTotals, cancellationToken);
            return null;
        }, static error => error, cancellationToken) is { } refused)
        {
            throw new InvalidOperationException(refused.Message);
        }

        await DeployAsync(functions, capped, "idle", Idle, cancellationToken);
        Require(await functions.InvokeAsync(capped, "idle", "http", "{}", null, cancellationToken) is FunctionResponse { Status: 200 },
            "the last operation unit of the Free allowance admits one invocation");
        var refusal = await functions.InvokeAsync(capped, "idle", "http", "{}", null, cancellationToken);
        Require(refusal is QuotaExceeded { Code: "quota_exceeded" } exceeded && exceeded.Message.Contains("cap of 50,000 operation units", StringComparison.Ordinal),
            "a Free organization at its operation allowance is refused like a write");
        var full = await UsageAsync(http, capped.OrganizationId, cancellationToken);
        Require(full.GetProperty("operations").GetInt64() == 50_000 && full.GetProperty("invocations").GetInt64() == 1
            && full.GetProperty("writeUnits").GetInt64() == 49_999, "a refused invocation adds nothing");

        return $"before={before.GetRawText()} after={after.GetRawText()} capped={full.GetRawText()} refusal=429 quota_exceeded";
    }

    private static async Task<TenantContext> TenantAsync(IServiceProvider services, string name, CancellationToken cancellationToken)
    {
        var management = services.GetRequiredService<IManagementService>();
        var user = EntityIds.New().ToString();
        var organization = await management.CreateOrganizationAsync(user, name, cancellationToken) is Organization created
            ? created : throw new InvalidOperationException("organization");
        var project = await management.CreateProjectAsync(user, organization.Id, name, cancellationToken) is Project made
            ? made : throw new InvalidOperationException("project");
        var database = await management.CreateDatabaseAsync(user, organization.Id, project.Id, name, cancellationToken) is ManagedDatabase opened
            ? opened : throw new InvalidOperationException("database");
        Require(organization.Plan == PlanTier.Hobby, "new organizations start on Free");
        return new TenantContext(organization.Id, organization.Plan, project.Id, database.Id);
    }

    private static async Task DeployAsync(IFunctions functions, TenantContext tenant, string name, string source, CancellationToken cancellationToken)
    {
        Require(await functions.DeployAsync(tenant, name, new DeployFunctionRequest(source), cancellationToken) is FunctionBody, "deploy " + name);
    }

    private static async Task<JsonElement> UsageAsync(HttpClient http, string organizationId, CancellationToken cancellationToken)
    {
        string? after = null;
        do
        {
            using var response = await http.GetAsync("/v1/usage/allowances" + (after is null ? "" : "?after=" + after), cancellationToken);
            Require(response.StatusCode == HttpStatusCode.OK, "allowances answer");
            using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            Require(page.RootElement.GetProperty("operationsContract").GetString() == "operation-units-writes-invocations-global-v3", "operations contract");
            foreach (var item in page.RootElement.GetProperty("items").EnumerateArray())
            {
                if (item.GetProperty("organizationId").GetString() == organizationId)
                {
                    return item.Clone();
                }
            }

            after = page.RootElement.GetProperty("next").GetString();
        } while (after is not null);

        throw new InvalidOperationException("the organization is missing from the allowance pages");
    }

    private static void Require(bool accepted, string message)
    {
        if (!accepted)
        {
            throw new InvalidOperationException(message);
        }
    }
}
