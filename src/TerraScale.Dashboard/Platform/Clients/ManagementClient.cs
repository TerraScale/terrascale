using System.Text.Json;
using TerraScale.Results;

namespace TerraScale.Dashboard.Platform.Clients;

public sealed class ManagementClient(TerraBaseHttpClient terraBase, ServiceCaller caller, CancellationToken cancellationToken)
{
    public Task<TerraBaseDatabasePage> ListDatabasesAsync(string projectId, string? after, int? limit) =>
        TerraBaseResults.RequireAsync(terraBase.ListDatabasesAsync(caller, projectId, after, limit, cancellationToken));

    public Task<TerraBaseManagedDatabase> GetDatabaseAsync(string projectId, string database) =>
        TerraBaseResults.RequireAsync(terraBase.GetDatabaseAsync(caller, projectId, database, cancellationToken));

    public Task<TerraBaseDatabaseRegions> GetDatabaseRegionsAsync(string projectId, string database) =>
        TerraBaseResults.RequireAsync(terraBase.GetDatabaseRegionsAsync(caller, projectId, database, cancellationToken));

    public Task<TerraBaseManagedDatabase> CreateDatabaseAsync(string projectId, TerraBaseDatabasePlacement request) =>
        TerraBaseResults.RequireAsync(terraBase.CreateDatabaseAsync(caller, projectId, request, cancellationToken));

    public Task<TerraBaseManagedDatabase> SetDatabaseAsync(string projectId, string database, TerraBaseDatabasePlacement request) =>
        TerraBaseResults.RequireAsync(terraBase.SetDatabaseAsync(caller, projectId, database, request, cancellationToken));

    public async Task<bool> DeleteDatabaseAsync(string projectId, string database) =>
        await terraBase.DeleteDatabaseAsync(caller, projectId, database, cancellationToken) switch
        {
            Done => true,
            TerraBaseRefusal { Status: StatusCodes.Status404NotFound } => false,
            TerraBaseRefusal refusal => throw refusal.Exception(),
        };

    public Task<TerraBaseManagedDatabase> BranchAsync(string projectId, string database, string name, long? ttlSeconds = null) =>
        TerraBaseResults.RequireAsync(terraBase.BranchAsync(caller, projectId, database, name, ttlSeconds, cancellationToken));

    public Task<TerraBaseManagedDatabase> SetDatabaseTtlAsync(string projectId, string database, TerraBaseDatabaseTtl request) =>
        TerraBaseResults.RequireAsync(terraBase.SetDatabaseTtlAsync(caller, projectId, database, request.TtlSeconds, cancellationToken));

    public Task<TerraBaseManagedDatabase> ClearDatabaseTtlAsync(string projectId, string database) =>
        TerraBaseResults.RequireAsync(terraBase.ClearDatabaseTtlAsync(caller, projectId, database, cancellationToken));

    public Task<JsonElement> ExecuteSqlAsync(string projectId, string database, JsonElement request) =>
        TerraBaseResults.RequireAsync(terraBase.ExecuteSqlAsync(caller, projectId, database, request, cancellationToken));

    public Task<TerraBaseRules> GetRulesAsync(string projectId, string database) =>
        TerraBaseResults.RequireAsync(terraBase.GetRulesAsync(caller, projectId, database, cancellationToken));

    public Task<TerraBaseRulesDeploy> DeployRulesAsync(string projectId, string database, string? version, JsonElement rules) =>
        TerraBaseResults.RequireAsync(terraBase.DeployRulesAsync(caller, projectId, database, version, rules, cancellationToken));

    public Task<TerraBaseRulesVersionPage> ListRulesVersionsAsync(string projectId, string database, string? after, int? limit) =>
        TerraBaseResults.RequireAsync(terraBase.ListRulesVersionsAsync(caller, projectId, database, after, limit, cancellationToken));

    public Task<TerraBaseRules> GetRulesVersionAsync(string projectId, string database, string version) =>
        TerraBaseResults.RequireAsync(terraBase.GetRulesVersionAsync(caller, projectId, database, version, cancellationToken));

    public Task<TerraBaseRulesDeploy> RestoreRulesVersionAsync(string projectId, string database, string version, string current) =>
        TerraBaseResults.RequireAsync(terraBase.RestoreRulesVersionAsync(caller, projectId, database, version, current, cancellationToken));

    public Task<TerraBaseRulesTestResult> TestRulesAsync(string projectId, string database, JsonElement request) =>
        TerraBaseResults.RequireAsync(terraBase.TestRulesAsync(caller, projectId, database, request, cancellationToken));
}
