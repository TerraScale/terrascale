using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Documents;
using TerraBase.Features.Management;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Memory;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Functions;

/// <summary>Registers database-scoped customer functions.</summary>
public static class FunctionsExtensions
{
    /// <summary>Registers the always-on function store and runtime.</summary>
    public static IServiceCollection AddTerraBaseFunctions(this IServiceCollection services) =>
        services.AddSingleton<FunctionService>().AddSingleton<IFunctions>(provider => provider.GetRequiredService<FunctionService>())
            .AddSingleton<FunctionScheduler>().AddHostedService(provider => provider.GetRequiredService<FunctionScheduler>());
}

internal sealed class FunctionService(SystemCatalog catalog, IDocumentService documents, IUsageMeter usage, IMemoryBudget memory,
    TerraBaseOptions options, TimeProvider time) : IFunctions
{
    public const string Schedules = "function_schedules";
    private const int MaxFunctions = 32;
    private const int KeptLogs = 100;
    private static readonly Unavailable Busy = new("function capacity is temporarily unavailable");

    public async ValueTask<FunctionResult> DeployAsync(TenantContext tenant, string name, DeployFunctionRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Source is not { Length: <= FunctionRunner.MaxSourceChars } source)
        {
            return new Validation("source is required and at most 256 KiB");
        }

        if (FunctionRunner.Check(source) is { } error)
        {
            return new Validation("source is not valid JavaScript: " + error);
        }

        if (request.Cron is { } cron && !FunctionScheduler.IsValid(cron))
        {
            return new Validation("cron must be a five-field cron expression");
        }

        var now = time.GetUtcNow();
        var result = await catalog.WriteDurableAsync<FunctionResult>(async transaction =>
        {
            if (await SystemCatalog.GetAsync(transaction, $"projects/{tenant.ProjectId}/databases", tenant.DatabaseId!,
                TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken).ConfigureAwait(false) is null)
            {
                return new NotFound();
            }

            var existing = await GetAsync(transaction, tenant, name, cancellationToken).ConfigureAwait(false);
            if (existing is null && (await transaction.ListAsync(Records(tenant), null, MaxFunctions, 8 * 1024 * 1024, cancellationToken)
                .ConfigureAwait(false)).Documents.Count >= MaxFunctions)
            {
                return new Conflict($"at most {MaxFunctions} functions may be deployed per database", "function_limit");
            }

            var record = new FunctionRecord(1, name, source, options.MachineId, existing?.CreatedAt ?? now, now, request.Cron);
            await SystemCatalog.PutAsync(transaction, Records(tenant), name, record, TerraBaseJsonContext.Default.FunctionRecord, cancellationToken)
                .ConfigureAwait(false);
            if (request.Cron is { } schedule)
            {
                await SystemCatalog.PutAsync(transaction, Schedules, ScheduleId(tenant, name), new FunctionSchedule(tenant, name, schedule, options.MachineId),
                    TerraBaseJsonContext.Default.FunctionSchedule, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await transaction.DeleteAsync(Schedules, ScheduleId(tenant, name), cancellationToken).ConfigureAwait(false);
            }

            return Body(record, withSource: true);
        }, static error => error, cancellationToken).ConfigureAwait(false);

        return result;
    }

    public async ValueTask<FunctionResult> ListAsync(TenantContext tenant, CancellationToken cancellationToken) =>
        await catalog.ReadAsync<FunctionResult>(async transaction =>
            (await SystemCatalog.ListAllAsync(transaction, Records(tenant), TerraBaseJsonContext.Default.FunctionRecord, cancellationToken)
                .ConfigureAwait(false)).Select(record => Body(record, withSource: false)).ToArray(),
            static error => error, cancellationToken).ConfigureAwait(false);

    public async ValueTask<FunctionResult> GetAsync(TenantContext tenant, string name, CancellationToken cancellationToken) =>
        await catalog.ReadAsync<FunctionResult>(async transaction =>
            await GetAsync(transaction, tenant, name, cancellationToken).ConfigureAwait(false) is { } record
                ? Body(record, withSource: true) : new NotFound(),
            static error => error, cancellationToken).ConfigureAwait(false);

    public async ValueTask<FunctionResult> DeleteAsync(TenantContext tenant, string name, CancellationToken cancellationToken)
    {
        var result = await catalog.WriteDurableAsync<FunctionResult>(async transaction =>
        {
            if (await GetAsync(transaction, tenant, name, cancellationToken).ConfigureAwait(false) is null)
            {
                return new NotFound();
            }

            await transaction.DeleteAsync(Records(tenant), name, cancellationToken).ConfigureAwait(false);
            await transaction.DeleteAsync(Schedules, ScheduleId(tenant, name), cancellationToken).ConfigureAwait(false);
            await transaction.DropCollectionAsync(Logs(tenant, name), cancellationToken).ConfigureAwait(false);
            return new Done();
        }, static error => error, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<FunctionResult> LogsAsync(TenantContext tenant, string name, int limit, CancellationToken cancellationToken) =>
        await catalog.ReadAsync<FunctionResult>(async transaction =>
        {
            if (await GetAsync(transaction, tenant, name, cancellationToken).ConfigureAwait(false) is null)
            {
                return new NotFound();
            }

            var logs = await SystemCatalog.ListAllAsync(transaction, Logs(tenant, name), TerraBaseJsonContext.Default.FunctionLogBody, cancellationToken)
                .ConfigureAwait(false);
            return logs.AsEnumerable().Reverse().Take(limit).ToArray();
        }, static error => error, cancellationToken).ConfigureAwait(false);

    public async ValueTask<FunctionResult> InvokeAsync(TenantContext tenant, string name, string trigger, string eventJson, CancellationToken cancellationToken) =>
        await RunAsync(tenant, name, trigger, eventJson, cancellationToken).ConfigureAwait(false);

    internal async ValueTask<FunctionResult> RunAsync(TenantContext tenant, string name, string trigger, string eventJson,
        CancellationToken cancellationToken)
    {
        if (await GetAsync(tenant, name, cancellationToken).ConfigureAwait(false) is not FunctionBody { Source: { } source })
        {
            return new NotFound();
        }
        using var reserved = memory.TryReserve(MemoryLane.Foreground, FunctionRunner.MemoryBytes + 2 * eventJson.Length, tenant);
        if (reserved is null)
        {
            return Busy;
        }

        var started = time.GetUtcNow();
        var clock = Stopwatch.StartNew();
        var run = await Task.Run(() => FunctionRunner.Run(source, eventJson, cancellationToken), cancellationToken).ConfigureAwait(false);
        var error = run.Error;
        if (error is null && run.Writes.Count > 0
            && await documents.WriteAsync(tenant, run.Writes, indexErrors: true, cancellationToken).ConfigureAwait(false) is DocumentRejected rejected)
        {
            error = $"write {rejected.Op} was refused: " + Message(rejected.Error);
        }

        usage.RecordOperation(tenant, "invoke", 1);
        var log = new FunctionLogBody(EntityIds.New().ToString(), started, trigger, error is null ? "ok" : "error", clock.ElapsedMilliseconds,
            [.. run.Lines], error);
        await RecordAsync(tenant, name, log, cancellationToken).ConfigureAwait(false);
        return error is null ? Response(run.Result!) : new FunctionResponse(500, "application/json",
            JsonSerializer.SerializeToUtf8Bytes(new ErrorBody(new ErrorDetail("function_error", error)), TerraBaseJsonContext.Default.ErrorBody));
    }

    private async ValueTask RecordAsync(TenantContext tenant, string name, FunctionLogBody log, CancellationToken cancellationToken) =>
        _ = await catalog.WriteAsync(async transaction =>
        {
            if (await GetAsync(transaction, tenant, name, cancellationToken).ConfigureAwait(false) is null)
            {
                return false;
            }

            await SystemCatalog.PutAsync(transaction, Logs(tenant, name), log.Id, log, TerraBaseJsonContext.Default.FunctionLogBody, cancellationToken)
                .ConfigureAwait(false);
            var kept = await transaction.ListAsync(Logs(tenant, name), null, KeptLogs + 1, 8 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            if (kept.Documents.Count > KeptLogs)
            {
                await transaction.DeleteAsync(Logs(tenant, name), kept.Documents[0].Id, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }, static _ => false, cancellationToken).ConfigureAwait(false);

    private static FunctionResponse Response(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("status", out var status)
            && status.TryGetInt32(out var code) && code is >= 200 and <= 599)
        {
            return !root.TryGetProperty("body", out var body) || body.ValueKind == JsonValueKind.Null
                ? new FunctionResponse(code, "application/json", [])
                : body.ValueKind == JsonValueKind.String
                    ? new FunctionResponse(code, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(body.GetString()!))
                    : new FunctionResponse(code, "application/json", Encoding.UTF8.GetBytes(body.GetRawText()));
        }

        return new FunctionResponse(200, "application/json", Encoding.UTF8.GetBytes(json));
    }

    private static string Message(Failure error) => error switch
    {
        NotFound e => e.Message ?? "not found",
        Conflict e => e.Message,
        Validation e => e.Message,
        PreconditionFailed e => e.Message,
        QuotaExceeded e => e.Message,
        PayloadTooLarge e => e.Message,
        Unavailable e => e.Message,
        _ => "the write was refused",
    };

    private static FunctionBody Body(FunctionRecord record, bool withSource) =>
        new(record.Name, withSource ? record.Source : null, record.Cron, record.CreatedAt, record.UpdatedAt);

    internal static string ScheduleId(TenantContext tenant, string name) => tenant.DatabaseId + "-" + name;

    private static ValueTask<FunctionRecord?> GetAsync(IStorageTransaction transaction, TenantContext tenant, string name, CancellationToken cancellationToken) =>
        SystemCatalog.GetAsync(transaction, Records(tenant), name, TerraBaseJsonContext.Default.FunctionRecord, cancellationToken);

    private static string Records(TenantContext tenant) => SystemCatalog.DatabaseState(tenant.DatabaseId!) + "functions";

    private static string Logs(TenantContext tenant, string name) => SystemCatalog.DatabaseState(tenant.DatabaseId!) + "function_logs/" + name;
}
