namespace TerraScale.DotNet.TerraBase;

public sealed record TerraBaseDeployFunctionRequest(string Source, string? Cron = null);

public sealed record TerraBaseFunction(string Name, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string? Source = null, string? Cron = null);

public sealed partial class TerraBaseDatabase
{
    public async Task<IReadOnlyList<TerraBaseFunction>> ListFunctionsAsync(CancellationToken cancellationToken = default)
    {
        var functions = (await connection.SendAsync(HttpMethod.Get, path + "/functions", null,
            Json.TerraBaseFunctionArray, null, cancellationToken).ConfigureAwait(false)).Value;
        TerraBaseJsonContext.RequireNonNullItems(functions);
        return functions;
    }

    public async Task<TerraBaseFunction> GetFunctionAsync(string functionName, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, FunctionPath(functionName), null,
            Json.TerraBaseFunction, null, cancellationToken).ConfigureAwait(false)).Value;

    public async Task<TerraBaseFunction> DeployFunctionAsync(string functionName, TerraBaseDeployFunctionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = TerraBaseConnection.Json(request, Json.TerraBaseDeployFunctionRequest);
        return (await connection.SendAsync(HttpMethod.Put, FunctionPath(functionName), body,
            Json.TerraBaseFunction, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    public Task DeleteFunctionAsync(string functionName, CancellationToken cancellationToken = default) =>
        connection.SendAsync(HttpMethod.Delete, FunctionPath(functionName), null, null, cancellationToken);

    private string FunctionPath(string functionName) => path + "/functions/" + TerraBaseConnection.Segment(functionName, nameof(functionName));
}
