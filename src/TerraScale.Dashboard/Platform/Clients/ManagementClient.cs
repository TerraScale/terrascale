using System.Text.Json;

namespace TerraScale.Dashboard.Platform.Clients;

public sealed class ManagementClient(TerraBaseHttpClient terraBase, ServiceCaller caller, CancellationToken cancellationToken)
{
    public async Task<TerraBaseDatabasePage> ListDatabasesAsync(string projectId, string? after, int? limit) =>
        (await terraBase.ListDatabasesAsync(caller, projectId, after, limit, cancellationToken)).Require();

    public async Task<TerraBaseManagedDatabase> GetDatabaseAsync(string projectId, string database) =>
        (await terraBase.GetDatabaseAsync(caller, projectId, database, cancellationToken)).Require();

    public async Task<TerraBaseDatabaseRegions> GetDatabaseRegionsAsync(string projectId, string database) =>
        (await terraBase.GetDatabaseRegionsAsync(caller, projectId, database, cancellationToken)).Require();

    public async Task<TerraBaseManagedDatabase> CreateDatabaseAsync(string projectId, TerraBaseDatabasePlacement request) =>
        (await terraBase.CreateDatabaseAsync(caller, projectId, request, cancellationToken)).Require();

    public async Task<TerraBaseManagedDatabase> SetDatabaseAsync(string projectId, string database, TerraBaseDatabasePlacement request) =>
        (await terraBase.SetDatabaseAsync(caller, projectId, database, request, cancellationToken)).Require();

    public async Task<bool> DeleteDatabaseAsync(string projectId, string database)
    {
        var outcome = await terraBase.DeleteDatabaseAsync(caller, projectId, database, cancellationToken);
        return outcome switch
        {
            TerraBaseCompleted => true,
            TerraBaseRefusal { Status: StatusCodes.Status404NotFound } => false,
            TerraBaseRefusal refusal => throw refusal.Exception(),
        };
    }

    public async Task<TerraBaseManagedDatabase> BranchAsync(string projectId, string database, string name, long? ttlSeconds = null) =>
        (await terraBase.BranchAsync(caller, projectId, database, name, ttlSeconds, cancellationToken)).Require();

    public async Task<TerraBaseManagedDatabase> SetDatabaseTtlAsync(string projectId, string database, TerraBaseDatabaseTtl request) =>
        (await terraBase.SetDatabaseTtlAsync(caller, projectId, database, request.TtlSeconds, cancellationToken)).Require();

    public async Task<TerraBaseManagedDatabase> ClearDatabaseTtlAsync(string projectId, string database) =>
        (await terraBase.ClearDatabaseTtlAsync(caller, projectId, database, cancellationToken)).Require();

    public async Task<JsonElement> ExecuteSqlAsync(string projectId, string database, JsonElement request) =>
        (await terraBase.ExecuteSqlAsync(caller, projectId, database, request, cancellationToken)).Require();

    public async Task<TerraBaseRules> GetRulesAsync(string projectId, string database) =>
        (await terraBase.GetRulesAsync(caller, projectId, database, cancellationToken)).Require();

    public async Task<TerraBaseRulesDeploy> DeployRulesAsync(string projectId, string database, string? version, JsonElement rules) =>
        (await terraBase.DeployRulesAsync(caller, projectId, database, version, rules, cancellationToken)).Require();

    public async Task<TerraBaseRulesVersionPage> ListRulesVersionsAsync(string projectId, string database, string? after, int? limit) =>
        (await terraBase.ListRulesVersionsAsync(caller, projectId, database, after, limit, cancellationToken)).Require();

    public async Task<TerraBaseRules> GetRulesVersionAsync(string projectId, string database, string version) =>
        (await terraBase.GetRulesVersionAsync(caller, projectId, database, version, cancellationToken)).Require();

    public async Task<TerraBaseRulesDeploy> RestoreRulesVersionAsync(string projectId, string database, string version, string current) =>
        (await terraBase.RestoreRulesVersionAsync(caller, projectId, database, version, current, cancellationToken)).Require();

    public async Task<TerraBaseRulesTestResult> TestRulesAsync(string projectId, string database, JsonElement request) =>
        (await terraBase.TestRulesAsync(caller, projectId, database, request, cancellationToken)).Require();
}
