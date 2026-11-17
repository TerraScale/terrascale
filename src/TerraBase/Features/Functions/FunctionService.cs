using System.Text.Json;
using System.Text;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Functions;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Management;
using TerraBase.Features.Memory;
using TerraBase.Features.Replicas;
using TerraBase.Features.Sql;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Functions;

public static class FunctionsExtensions
{
    public static IServiceCollection AddTerraBaseFunctions(this IServiceCollection services) =>
        services.AddSingleton<FunctionService>().AddSingleton<IFunctions>(provider => provider.GetRequiredService<FunctionService>())
            .AddHostedService(provider => provider.GetRequiredService<FunctionService>())
            .AddSingleton<FunctionScheduler>().AddHostedService(provider => provider.GetRequiredService<FunctionScheduler>());
}

internal sealed partial class FunctionService(SystemCatalog catalog, ISqlService sql, IUsageMeter usage, IMemoryBudget memory,
    TerraBaseOptions options, TimeProvider time, RegionalIngress ingress) : BackgroundService, IFunctions, IHostedLifecycleService
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
            if (await CatalogTables.DatabaseAsync(transaction, tenant.ProjectId!, tenant.DatabaseId!, cancellationToken).ConfigureAwait(false) is null)
            {
                return new NotFound();
            }

            var existing = await GetAsync(transaction, tenant, name, cancellationToken).ConfigureAwait(false);
            if (existing is null && (await transaction.ListAsync(Records(tenant), null, MaxFunctions, 8 * 1024 * 1024, cancellationToken)
                .ConfigureAwait(false)).Records.Count >= MaxFunctions)
            {
                return new Conflict($"at most {MaxFunctions} functions may be deployed per database", "function_limit");
            }

            var record = new FunctionRecord(1, name, source, options.MachineId, existing?.CreatedAt ?? now, now, request.Cron);
            await SystemCatalog.PutAsync(transaction, Records(tenant), name, record, FunctionsJsonContext.Default.FunctionRecord, cancellationToken)
                .ConfigureAwait(false);
            if (request.Cron is { } schedule)
            {
                await SystemCatalog.PutAsync(transaction, Schedules, ScheduleId(tenant, name), new FunctionSchedule(tenant, name, schedule, options.MachineId),
                    FunctionsJsonContext.Default.FunctionSchedule, cancellationToken).ConfigureAwait(false);
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
            (await SystemCatalog.ListAllAsync(transaction, Records(tenant), FunctionsJsonContext.Default.FunctionRecord, cancellationToken)
                .ConfigureAwait(false)).Select(record => Body(record, withSource: false)).ToArray(),
            static error => error, cancellationToken).ConfigureAwait(false);

    public async ValueTask<FunctionResult> GetAsync(TenantContext tenant, string name, CancellationToken cancellationToken) =>
        await catalog.ReadAsync<FunctionResult>(async transaction =>
            await GetAsync(transaction, tenant, name, cancellationToken).ConfigureAwait(false) is { } record
                ? Body(record, withSource: true) : new NotFound(),
            static error => error, cancellationToken).ConfigureAwait(false);

    public async ValueTask<FunctionResult> DeleteAsync(TenantContext tenant, string name, CancellationToken cancellationToken)
    {
        if (await FlushAsync(cancellationToken).ConfigureAwait(false) is { } unavailable)
        {
            return unavailable;
        }
        var result = await catalog.WriteDurableAsync<FunctionResult>(async transaction =>
        {
            if (await GetAsync(transaction, tenant, name, cancellationToken).ConfigureAwait(false) is null)
            {
                return new NotFound();
            }

            await transaction.DeleteAsync(Records(tenant), name, cancellationToken).ConfigureAwait(false);
            await transaction.DeleteAsync(Schedules, ScheduleId(tenant, name), cancellationToken).ConfigureAwait(false);
            await transaction.DropKindAsync(Logs(tenant, name), cancellationToken).ConfigureAwait(false);
            await transaction.DropKindAsync(FunctionMetrics.Kind(tenant, name), cancellationToken).ConfigureAwait(false);
            return new Done();
        }, static error => error, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<FunctionResult> LogsAsync(TenantContext tenant, string name, int limit, CancellationToken cancellationToken)
    {
        if (await FlushAsync(cancellationToken).ConfigureAwait(false) is { } unavailable)
        {
            return unavailable;
        }
        return await catalog.ReadAsync<FunctionResult>(async transaction =>
        {
            if (await GetAsync(transaction, tenant, name, cancellationToken).ConfigureAwait(false) is null)
            {
                return new NotFound();
            }

            var logs = await SystemCatalog.ListAllAsync(transaction, Logs(tenant, name), FunctionsJsonContext.Default.FunctionLogBody, cancellationToken)
                .ConfigureAwait(false);
            return logs.AsEnumerable().Reverse().Take(limit).ToArray();
        }, static error => error, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<FunctionResult> InvokeAsync(TenantContext tenant, string name, string trigger, string eventJson, string? requestId,
        CancellationToken cancellationToken) =>
        await RunAsync(tenant, name, trigger, eventJson, cancellationToken, requestId).ConfigureAwait(false);

    internal async ValueTask<FunctionResult> RunAsync(TenantContext tenant, string name, string trigger, string eventJson,
        CancellationToken cancellationToken, string? requestId = null)
    {
        lock (_recordGate)
        {
            var held = _tenantRecords.GetValueOrDefault(tenant.OrganizationId);
            if (_stopping || _recordCount == RecordCapacity || held is { Count: TenantRecordCapacity })
            {
                return _recordError ?? Busy;
            }
            _running++;
            _recordCount++;
            if (held is null)
            {
                held = new TenantRecords();
                _tenantRecords.Add(tenant.OrganizationId, held);
            }
            if (++held.Count == TenantRecordCapacity)
            {
                held.Space = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        var queued = false;
        IMemoryReservation? reserved = null;
        RegionalIngress.Lease? lease = null;
        try
        {
            lease = await ingress.EnterAsync(parkWhileHeld: false, cancellationToken).ConfigureAwait(false);
            if (lease is null)
            {
                return Busy;
            }
            if (await GetAsync(tenant, name, cancellationToken).ConfigureAwait(false) is not FunctionBody { Source: { } source })
            {
                return new NotFound();
            }
            reserved = memory.TryReserve(FunctionRunner.MemoryBytes + 2 * eventJson.Length, tenant);
            if (reserved is null)
            {
                return Busy;
            }

            var started = time.GetUtcNow();
            var clock = time.GetTimestamp();
            FunctionRun run;
            var session = sql.Open(tenant, canWrite: true);
            var calls = new FunctionSql(session, requestId, time);
            await using (session.ConfigureAwait(false))
            {
                session.BeginRequest();
                using var running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _invocationsStopping.Token);
                run = await Task.Run(() => FunctionRunner.RunAsync(source, eventJson, calls, running.Token), CancellationToken.None)
                    .ConfigureAwait(false);
            }

            var error = run.Error;

            usage.RecordOperation(tenant, "invoke", 1);
            var response = error is null ? Response(run.Result!) : new FunctionResponse(500, "application/json",
                JsonSerializer.SerializeToUtf8Bytes(new ErrorBody(new ErrorDetail("function_error", error, RequestId: requestId,
                    DocsUrl: ErrorDocs.Url("function_error"))), TerraBaseJsonContext.Default.ErrorBody));
            var log = new FunctionLogBody(EntityIds.New().ToString(), started, trigger, error is null ? "ok" : "error",
                (long)Math.Ceiling(time.GetElapsedTime(clock).TotalMilliseconds), [.. run.Lines], error,
                calls.SqlCalls, calls.SqlMs, calls.RowsRead, calls.RowsWritten, ResponseBytes: response.Body.LongLength, HttpStatus: trigger == "http" && error is null ? response.Status : null);
            var retainedBytes = 4096L + 2L * (name.Length + tenant.OrganizationId.Length + tenant.ProjectId!.Length + tenant.DatabaseId!.Length
                + (log.Error?.Length ?? 0) + log.Lines.Sum(line => line.Length));
            var space = Queue(tenant, name, log, reserved, lease, retainedBytes);
            queued = true;
            reserved = null;
            lease = null;
            await space.ConfigureAwait(false);
            return response;
        }
        finally
        {
            reserved?.Dispose();
            lease?.Dispose();
            if (!queued)
            {
                ReleaseRecord(tenant);
            }
            lock (_recordGate)
            {
                if (--_running == 0 && _stopping)
                {
                    _records.Writer.TryComplete();
                }
            }
        }
    }

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

    private static FunctionBody Body(FunctionRecord record, bool withSource) =>
        new(record.Name, withSource ? record.Source : null, record.Cron, record.CreatedAt, record.UpdatedAt);

    internal static string ScheduleId(TenantContext tenant, string name) => tenant.DatabaseId + "-" + name;

    private static ValueTask<FunctionRecord?> GetAsync(IStorageTransaction transaction, TenantContext tenant, string name, CancellationToken cancellationToken) =>
        SystemCatalog.GetAsync(transaction, Records(tenant), name, FunctionsJsonContext.Default.FunctionRecord, cancellationToken);

    private static string Records(TenantContext tenant) => SystemCatalog.DatabaseState(tenant.DatabaseId!) + "functions";

    private static string Logs(TenantContext tenant, string name) => SystemCatalog.DatabaseState(tenant.DatabaseId!) + "function_logs/" + name;
}
