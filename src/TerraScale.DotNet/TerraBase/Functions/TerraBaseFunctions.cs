namespace TerraScale.DotNet.TerraBase;

/// <summary>A collection change trigger; null Events selects both upserts and deletes.</summary>
/// <param name="Collection">Watched collection. The function cannot write to this collection.</param>
/// <param name="Events">Optional upsert/delete filter. Delivery is at least once, with retries.</param>
public sealed record TerraBaseFunctionTrigger(string Collection, IReadOnlyList<string>? Events = null);

/// <summary>Replaces a function's source, change trigger and UTC schedule.</summary>
/// <param name="Source">JavaScript defining handler(event), subject to the producer's 256 KiB limit.</param>
/// <param name="Trigger">Change trigger; omission removes the previous trigger.</param>
/// <param name="Cron">Five-field UTC cron; omission removes the previous schedule. Missed runs are skipped.</param>
public sealed record TerraBaseDeployFunctionRequest(string Source, TerraBaseFunctionTrigger? Trigger = null, string? Cron = null);

/// <summary>A database function. Source is absent or null in list responses.</summary>
/// <param name="Name">Lowercase function name, at most 63 characters.</param>
/// <param name="CreatedAt">First deployment time.</param>
/// <param name="UpdatedAt">Last deployment time.</param>
/// <param name="Source">JavaScript source, included by get and deploy.</param>
/// <param name="Trigger">Change trigger, or null.</param>
/// <param name="Cron">UTC cron schedule, or null.</param>
public sealed record TerraBaseFunction(string Name, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string? Source = null, TerraBaseFunctionTrigger? Trigger = null, string? Cron = null);

public sealed partial class TerraBaseDatabase
{
    /// <summary>
    /// Lists functions in name order without source, as one bounded array (at most 32). Requires project
    /// credentials with both documents:read and documents:write; there is no pagination. Sent once.
    /// </summary>
    public async Task<IReadOnlyList<TerraBaseFunction>> ListFunctionsAsync(CancellationToken cancellationToken = default)
    {
        var functions = (await connection.SendAsync(HttpMethod.Get, path + "/functions", null,
            Json.TerraBaseFunctionArray, null, cancellationToken).ConfigureAwait(false)).Value;
        TerraBaseJsonContext.RequireNonNullItems(functions);
        return functions;
    }

    /// <summary>Reads a function with source. Requires both documents:read and documents:write. Sent once.</summary>
    public async Task<TerraBaseFunction> GetFunctionAsync(string functionName, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, FunctionPath(functionName), null,
            Json.TerraBaseFunction, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>
    /// Deploys or fully replaces a function, returning its source and timestamps. Both documents:read and
    /// documents:write are required. The producer validates JavaScript, trigger and cron; capacity conflicts
    /// include function_limit at 32 functions. Sent once; reconcile an uncertain outcome with GetFunctionAsync.
    /// </summary>
    public async Task<TerraBaseFunction> DeployFunctionAsync(string functionName, TerraBaseDeployFunctionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = TerraBaseConnection.Json(request, Json.TerraBaseDeployFunctionRequest);
        return (await connection.SendAsync(HttpMethod.Put, FunctionPath(functionName), body,
            Json.TerraBaseFunction, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    /// <summary>
    /// Deletes a function, trigger, schedule and logs (204). Requires both documents:read and documents:write.
    /// Sent once; an absent function answers 404. Cancellation does not establish whether deletion committed.
    /// </summary>
    public Task DeleteFunctionAsync(string functionName, CancellationToken cancellationToken = default) =>
        connection.SendAsync(HttpMethod.Delete, FunctionPath(functionName), null, null, cancellationToken);

    private string FunctionPath(string functionName) => path + "/functions/" + TerraBaseConnection.Segment(functionName, nameof(functionName));
}
