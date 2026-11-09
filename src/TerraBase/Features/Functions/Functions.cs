using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Functions;

public sealed record DeployFunctionRequest(string? Source, string? Cron = null);

public sealed record FunctionBody(string Name, string? Source, string? Cron, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record FunctionLogBody(string Id, DateTimeOffset At, string Trigger, string Status, long DurationMs, IReadOnlyList<string> Lines, string? Error);

public sealed record FunctionResponse(int Status, string ContentType, byte[] Body);

public readonly union FunctionResult(FunctionBody, FunctionBody[], FunctionLogBody[], FunctionResponse, Done, Validation, NotFound, Conflict, Unavailable);

public interface IFunctions
{
    ValueTask<FunctionResult> DeployAsync(TenantContext tenant, string name, DeployFunctionRequest request, CancellationToken cancellationToken);

    ValueTask<FunctionResult> ListAsync(TenantContext tenant, CancellationToken cancellationToken);

    ValueTask<FunctionResult> GetAsync(TenantContext tenant, string name, CancellationToken cancellationToken);

    ValueTask<FunctionResult> DeleteAsync(TenantContext tenant, string name, CancellationToken cancellationToken);

    ValueTask<FunctionResult> LogsAsync(TenantContext tenant, string name, int limit, CancellationToken cancellationToken);

    ValueTask<FunctionResult> InvokeAsync(TenantContext tenant, string name, string trigger, string eventJson, string? requestId,
        CancellationToken cancellationToken);
}

internal sealed record FunctionRecord(int Format, string Name, string Source, string Machine, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string? Cron = null);

internal sealed record FunctionSchedule(TenantContext Tenant, string Name, string Cron, string Machine);
