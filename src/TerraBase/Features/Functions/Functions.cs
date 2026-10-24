using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Functions;

/// <summary>A function to deploy or replace.</summary>
/// <param name="Source">JavaScript source defining <c>handler(event)</c>; at most 256 KiB.</param>
public sealed record DeployFunctionRequest(string? Source);

/// <summary>A deployed function.</summary>
/// <param name="Name">Lowercase function name.</param>
/// <param name="Source">JavaScript source; null in lists.</param>
/// <param name="CreatedAt">First deployment time.</param>
/// <param name="UpdatedAt">Last deployment time.</param>
public sealed record FunctionBody(string Name, string? Source, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>One invocation's log.</summary>
/// <param name="Id">Invocation ULID.</param>
/// <param name="At">Start time.</param>
/// <param name="Trigger">http, change or cron.</param>
/// <param name="Status">ok or error.</param>
/// <param name="DurationMs">Run time in milliseconds.</param>
/// <param name="Lines">Console lines, at most 50 of 1 KiB.</param>
/// <param name="Error">Error message, or null.</param>
public sealed record FunctionLogBody(string Id, DateTimeOffset At, string Trigger, string Status, long DurationMs, string[] Lines, string? Error);

/// <summary>A handler's HTTP answer.</summary>
/// <param name="Status">HTTP status.</param>
/// <param name="ContentType">Media type of <paramref name="Body"/>.</param>
/// <param name="Body">Response bytes.</param>
public sealed record FunctionResponse(int Status, string ContentType, byte[] Body);

/// <summary>A function operation or a public, sanitized failure.</summary>
public readonly union FunctionResult(FunctionBody, FunctionBody[], FunctionLogBody[], FunctionResponse, Done, Validation, NotFound, Conflict, Unavailable);

/// <summary>Database-scoped customer functions.</summary>
public interface IFunctions
{
    /// <summary>Deploys or replaces a function after checking its source parses.</summary>
    ValueTask<FunctionResult> DeployAsync(TenantContext tenant, string name, string? source, CancellationToken cancellationToken);

    /// <summary>Lists functions without their source.</summary>
    ValueTask<FunctionResult> ListAsync(TenantContext tenant, CancellationToken cancellationToken);

    /// <summary>Reads one function with its source.</summary>
    ValueTask<FunctionResult> GetAsync(TenantContext tenant, string name, CancellationToken cancellationToken);

    /// <summary>Deletes a function and its logs.</summary>
    ValueTask<FunctionResult> DeleteAsync(TenantContext tenant, string name, CancellationToken cancellationToken);

    /// <summary>The last 100 invocation logs, newest first.</summary>
    ValueTask<FunctionResult> LogsAsync(TenantContext tenant, string name, int limit, CancellationToken cancellationToken);

    /// <summary>Runs the function with <paramref name="eventJson"/>, applies its writes and records its log.</summary>
    ValueTask<FunctionResult> InvokeAsync(TenantContext tenant, string name, string trigger, string eventJson, CancellationToken cancellationToken);
}

internal sealed record FunctionRecord(int Format, string Name, string Source, string Machine, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
