using TerraScale.Results;

namespace TerraScale.Dashboard.Platform.Clients;

public sealed class FunctionsClient(PlatformHttpClient platform, ServiceCaller caller, CancellationToken cancellationToken)
{
    public Task<IReadOnlyList<TerraBaseFunctionInfo>> ListAsync(string projectId, string database) =>
        TerraBaseResults.RequireAsync(platform.ListFunctionsAsync(caller, projectId, database, cancellationToken));

    public Task<TerraBaseFunctionInfo> GetAsync(string projectId, string database, string name) =>
        TerraBaseResults.RequireAsync(platform.GetFunctionAsync(caller, projectId, database, name, cancellationToken));

    public Task<IReadOnlyList<TerraBaseFunctionInvocation>> LogsAsync(string projectId, string database, string name, int? limit) =>
        TerraBaseResults.RequireAsync(platform.FunctionLogsAsync(caller, projectId, database, name, limit, cancellationToken));

    public Task<TerraBaseFunctionStats> StatsAsync(string projectId, string database, string name, string? window) =>
        TerraBaseResults.RequireAsync(platform.FunctionStatsAsync(caller, projectId, database, name, window, cancellationToken));

    public async Task<bool> DeleteAsync(string projectId, string database, string name) =>
        Deleted(await platform.DeleteFunctionAsync(caller, projectId, database, name, cancellationToken));

    public Task<TerraBaseFunctionInfo> DeployAsync(string projectId, string database, string name, TerraBaseFunctionDeploy request) =>
        TerraBaseResults.RequireAsync(platform.DeployFunctionAsync(caller, projectId, database, name, request, cancellationToken));

    public Task<IReadOnlyList<TerraBaseFunctionSecret>> ListSecretsAsync(string projectId, string database, string name) =>
        TerraBaseResults.RequireAsync(platform.ListSecretsAsync(caller, projectId, database, name, cancellationToken));

    public Task<TerraBaseFunctionSecret> SetSecretAsync(string projectId, string database, string name, string secret, TerraBaseFunctionSecretValue request) =>
        TerraBaseResults.RequireAsync(platform.SetSecretAsync(caller, projectId, database, name, secret, request, cancellationToken));

    public async Task<bool> DeleteSecretAsync(string projectId, string database, string name, string secret) =>
        Deleted(await platform.DeleteSecretAsync(caller, projectId, database, name, secret, cancellationToken));

    public Task<TerraBaseFunctionCall> InvokeAsync(string projectId, string database, string name, string body, IReadOnlyDictionary<string, string>? query = null) =>
        TerraBaseResults.RequireAsync(platform.InvokeFunctionAsync(caller, projectId, database, name, body, query, cancellationToken));

    private static bool Deleted(Result<Done, TerraBaseRefusal> result) => result switch
    {
        Done => true,
        TerraBaseRefusal { Status: StatusCodes.Status404NotFound } => false,
        TerraBaseRefusal refusal => throw refusal.Exception(),
    };
}
