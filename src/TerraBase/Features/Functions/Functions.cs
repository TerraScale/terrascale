using TerraBase.Errors;
using TerraBase.Tenancy;
using TerraScale.TerraBase.Contracts.Functions;

namespace TerraBase.Features.Functions;

public sealed record DeployFunctionRequest(string? Source, string? Cron = null);

public sealed record FunctionBody(string Name, string? Source, string? Cron, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record FunctionLogBody(string Id, DateTimeOffset At, string Trigger, string Status, long DurationMs, IReadOnlyList<string> Lines, string? Error,
    int SqlCalls = 0, long SqlMs = 0, long RowsRead = 0, long RowsWritten = 0, long ResponseBytes = 0, int? HttpStatus = null);

public sealed record FunctionResponse(int Status, string ContentType, byte[] Body);

public sealed record SetFunctionSecretRequest(string? Value);

public sealed record FunctionSecretBody(string Name, DateTimeOffset UpdatedAt);

public readonly union FunctionResult(FunctionBody, FunctionBody[], FunctionLogBody[], FunctionSecretBody, FunctionSecretBody[], TerraBaseFunctionStats, FunctionResponse, Done, Validation, NotFound, Conflict, Unavailable);

public interface IFunctions
{
    ValueTask<FunctionResult> DeployAsync(TenantContext tenant, string name, DeployFunctionRequest request, CancellationToken cancellationToken);

    ValueTask<FunctionResult> ListAsync(TenantContext tenant, CancellationToken cancellationToken);

    ValueTask<FunctionResult> GetAsync(TenantContext tenant, string name, CancellationToken cancellationToken);

    ValueTask<FunctionResult> DeleteAsync(TenantContext tenant, string name, CancellationToken cancellationToken);

    ValueTask<FunctionResult> LogsAsync(TenantContext tenant, string name, int limit, CancellationToken cancellationToken);

    ValueTask<FunctionResult> StatsAsync(TenantContext tenant, string name, string window, CancellationToken cancellationToken);

    ValueTask<FunctionResult> ListSecretsAsync(TenantContext tenant, string name, CancellationToken cancellationToken);

    ValueTask<FunctionResult> SetSecretAsync(TenantContext tenant, string name, string secret, SetFunctionSecretRequest request, CancellationToken cancellationToken);

    ValueTask<FunctionResult> DeleteSecretAsync(TenantContext tenant, string name, string secret, CancellationToken cancellationToken);

    ValueTask<FunctionResult> InvokeAsync(TenantContext tenant, string name, string trigger, string eventJson, string? requestId,
        CancellationToken cancellationToken);
}

internal sealed record FunctionRecord(int Format, string Name, string Source, string Machine, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string? Cron = null);

internal sealed record FunctionSecretRecord(int Format, string Name, string Sealed, DateTimeOffset UpdatedAt);

internal sealed record FunctionCode(string Source, IReadOnlyList<FunctionSecretRecord> Secrets);

internal readonly union FunctionLoad(FunctionCode, NotFound, Unavailable);

internal sealed record FunctionSchedule(TenantContext Tenant, string Name, string Cron, string Machine);
