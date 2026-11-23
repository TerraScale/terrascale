namespace TerraScale.Dashboard.Platform.Clients;

public sealed class FunctionsClient(PlatformHttpClient platform, ServiceCaller caller, CancellationToken cancellationToken)
{
    public async Task<IReadOnlyList<TerraBaseFunctionInfo>> ListAsync(string projectId, string database) =>
        (await platform.ListFunctionsAsync(caller, projectId, database, cancellationToken)).Require();

    public async Task<TerraBaseFunctionInfo> GetAsync(string projectId, string database, string name) =>
        (await platform.GetFunctionAsync(caller, projectId, database, name, cancellationToken)).Require();

    public async Task<IReadOnlyList<TerraBaseFunctionInvocation>> LogsAsync(string projectId, string database, string name, int? limit) =>
        (await platform.FunctionLogsAsync(caller, projectId, database, name, limit, cancellationToken)).Require();

    public async Task<TerraBaseFunctionStats> StatsAsync(string projectId, string database, string name, string? window) =>
        (await platform.FunctionStatsAsync(caller, projectId, database, name, window, cancellationToken)).Require();

    public async Task<bool> DeleteAsync(string projectId, string database, string name) =>
        Deleted(await platform.DeleteFunctionAsync(caller, projectId, database, name, cancellationToken));

    public async Task<TerraBaseFunctionInfo> DeployAsync(string projectId, string database, string name, TerraBaseFunctionDeploy request) =>
        (await platform.DeployFunctionAsync(caller, projectId, database, name, request, cancellationToken)).Require();

    public async Task<IReadOnlyList<TerraBaseFunctionSecret>> ListSecretsAsync(string projectId, string database, string name) =>
        (await platform.ListSecretsAsync(caller, projectId, database, name, cancellationToken)).Require();

    public async Task<TerraBaseFunctionSecret> SetSecretAsync(string projectId, string database, string name, string secret, TerraBaseFunctionSecretValue request) =>
        (await platform.SetSecretAsync(caller, projectId, database, name, secret, request, cancellationToken)).Require();

    public async Task<bool> DeleteSecretAsync(string projectId, string database, string name, string secret) =>
        Deleted(await platform.DeleteSecretAsync(caller, projectId, database, name, secret, cancellationToken));

    public async Task<TerraBaseFunctionCall> InvokeAsync(string projectId, string database, string name, string body, IReadOnlyDictionary<string, string>? query = null) =>
        (await platform.InvokeFunctionAsync(caller, projectId, database, name, body, query, cancellationToken)).Require();

    private static bool Deleted(TerraBaseActionOutcome outcome) => outcome switch
    {
        TerraBaseCompleted => true,
        TerraBaseRefusal { Status: StatusCodes.Status404NotFound } => false,
        TerraBaseRefusal refusal => throw refusal.Exception(),
    };
}
