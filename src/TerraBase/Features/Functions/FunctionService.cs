using System.Buffers;
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
using TerraBase.Features.Webhooks;
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
            .AddSingleton<IWebhookLocalReceiver, FunctionTriggerReceiver>()
            .AddSingleton<FunctionScheduler>().AddHostedService(provider => provider.GetRequiredService<FunctionScheduler>());
}

internal sealed class FunctionService(SystemCatalog catalog, IDocumentService documents, IUsageMeter usage, IMemoryBudget memory,
    TerraBaseOptions options, TimeProvider time, IWebhooks webhooks) : IFunctions
{
    public const string Schedules = "function_schedules";
    private const int MaxChangeDocumentBytes = 1024 * 1024;
    private const int MaxFunctions = 32;
    private const int KeptLogs = 100;
    private static readonly Unavailable Busy = new("function capacity is temporarily unavailable");

    public async ValueTask<FunctionResult> DeployAsync(TenantContext tenant, string database, string name, DeployFunctionRequest request,
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

        var trigger = request.Trigger;
        if (trigger is not null && (!DatabaseName.TryParse(trigger.Collection, out _)
            || trigger.Events is { } events && (events.Length is 0 or > 2 || events.Any(e => e is not ("upsert" or "delete")))))
        {
            return new Validation("trigger.collection must be a collection name; trigger.events must be upsert and/or delete");
        }

        if (request.Cron is { } cron && !FunctionScheduler.IsValid(cron))
        {
            return new Validation("cron must be a five-field cron expression");
        }

        var previous = await catalog.ReadAsync(transaction => GetAsync(transaction, tenant, name, cancellationToken), static _ => null, cancellationToken)
            .ConfigureAwait(false);
        // Keep the registration of an unchanged trigger collection: its cursor and delivery history carry over.
        var webhookId = trigger is null ? null : previous?.Trigger?.Collection == trigger.Collection ? previous.WebhookId : null;
        string? created = null;
        if (trigger is not null && webhookId is null)
        {
            switch (await webhooks.CreateAsync(tenant, database, trigger.Collection, Url(tenant, database, name), cancellationToken).ConfigureAwait(false))
            {
                case CreatedWebhookBody hook:
                    webhookId = created = hook.Id;
                    break;
                case NotFound:
                    return new NotFound("the trigger collection does not exist");
                case Conflict refused:
                    return refused;
                case Unavailable refused:
                    return refused;
                default:
                    return new Validation("the trigger could not be registered");
            }
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

            var record = new FunctionRecord(1, name, source, options.MachineId, existing?.CreatedAt ?? now, now, trigger, request.Cron, webhookId, tenant);
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

        // On success drop the replaced registration; on failure drop the one just created.
        var (staleCollection, staleId) = result is FunctionBody
            ? (previous?.Trigger?.Collection, previous?.WebhookId != webhookId ? previous?.WebhookId : null)
            : (trigger?.Collection, created);
        if (staleCollection is not null && staleId is not null)
        {
            _ = await webhooks.DeleteAsync(tenant, staleCollection, staleId, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public async ValueTask<FunctionResult> DeliveriesAsync(TenantContext tenant, string name, CancellationToken cancellationToken)
    {
        var record = await catalog.ReadAsync(transaction => GetAsync(transaction, tenant, name, cancellationToken), static _ => null, cancellationToken)
            .ConfigureAwait(false);
        if (record is not { Trigger: { } trigger, WebhookId: { } id })
        {
            return record is null ? new NotFound() : Array.Empty<WebhookDeliveryBody>();
        }

        return await webhooks.DeliveriesAsync(tenant, trigger.Collection, id, null, 100, cancellationToken).ConfigureAwait(false) switch
        {
            WebhookDeliveryBody[] deliveries => deliveries,
            Unavailable error => error,
            _ => Array.Empty<WebhookDeliveryBody>(),
        };
    }

    /// <summary>Runs a change trigger's delivery; a failed run answers a status that makes the webhook worker retry.</summary>
    public async ValueTask<WebhookAttempt> ReceiveAsync(WebhookPayload payload, string databaseId, string name, CancellationToken cancellationToken)
    {
        var record = await catalog.ReadAsync(transaction => SystemCatalog.GetAsync(transaction, SystemCatalog.DatabaseState(databaseId) + "functions", name,
            TerraBaseJsonContext.Default.FunctionRecord, cancellationToken), static _ => null, cancellationToken).ConfigureAwait(false);
        if (record is not { Trigger: { } trigger, Tenant: { } tenant } || trigger.Collection != payload.Collection
            || tenant.ProjectId != payload.ProjectId || tenant.DatabaseId != databaseId)
        {
            return new(404, "function_error");
        }

        var output = new ArrayBufferWriter<byte>();
        var included = 0L;
        var count = 0;
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("trigger", "change");
            writer.WriteString("collection", payload.Collection);
            writer.WriteStartArray("changes");
            foreach (var change in payload.Events.Where(change => trigger.Events is null || trigger.Events.Contains(change.Deleted ? "delete" : "upsert")))
            {
                count++;
                writer.WriteStartObject();
                writer.WriteString("id", change.Id);
                writer.WriteBoolean("deleted", change.Deleted);
                writer.WritePropertyName("document");
                // Current state, within one 1 MiB budget per delivery; later documents beyond it are null.
                if (!change.Deleted && await documents.GetAsync(new DocumentKey(tenant, payload.Collection, change.Id), cancellationToken).ConfigureAwait(false)
                    is StoredDocument { Deleted: false } document && included + document.Json.Length <= MaxChangeDocumentBytes)
                {
                    included += document.Json.Length;
                    writer.WriteRawValue(document.Json.Span, skipInputValidation: true);
                }
                else
                {
                    writer.WriteNullValue();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        if (count == 0)
        {
            return new(204, null);
        }

        return await RunAsync(tenant, name, "change", Encoding.UTF8.GetString(output.WrittenSpan), payload.Collection, cancellationToken).ConfigureAwait(false) switch
        {
            FunctionResponse response => new(response.Status, response.Status is >= 200 and <= 299 ? null : "function_error"),
            Unavailable => new(503, "function_unavailable"),
            _ => new(404, "function_error"),
        };
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
        FunctionRecord? deleted = null;
        var result = await catalog.WriteDurableAsync<FunctionResult>(async transaction =>
        {
            if ((deleted = await GetAsync(transaction, tenant, name, cancellationToken).ConfigureAwait(false)) is null)
            {
                return new NotFound();
            }

            await transaction.DeleteAsync(Records(tenant), name, cancellationToken).ConfigureAwait(false);
            await transaction.DeleteAsync(Schedules, ScheduleId(tenant, name), cancellationToken).ConfigureAwait(false);
            await transaction.DropCollectionAsync(Logs(tenant, name), cancellationToken).ConfigureAwait(false);
            return new Done();
        }, static error => error, cancellationToken).ConfigureAwait(false);
        if (result is Done && deleted is { Trigger: { } trigger, WebhookId: { } id })
        {
            _ = await webhooks.DeleteAsync(tenant, trigger.Collection, id, cancellationToken).ConfigureAwait(false);
        }

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
        await RunAsync(tenant, name, trigger, eventJson, readOnlyCollection: null, cancellationToken).ConfigureAwait(false);

    /// <summary>Runs, applies writes and logs; a change-trigger run passes its collection as read-only.</summary>
    internal async ValueTask<FunctionResult> RunAsync(TenantContext tenant, string name, string trigger, string eventJson, string? readOnlyCollection,
        CancellationToken cancellationToken)
    {
        if (await GetAsync(tenant, name, cancellationToken).ConfigureAwait(false) is not FunctionBody { Source: { } source })
        {
            return new NotFound();
        }

        // The engine's own limit bounds the run; reserve it before starting so concurrent runs stay within the node budget.
        using var reserved = memory.TryReserve(MemoryLane.Foreground, FunctionRunner.MemoryBytes + 2 * eventJson.Length, tenant);
        if (reserved is null)
        {
            return Busy;
        }

        var started = time.GetUtcNow();
        var clock = Stopwatch.StartNew();
        var run = await Task.Run(() => FunctionRunner.Run(source, eventJson, readOnlyCollection, cancellationToken), cancellationToken).ConfigureAwait(false);
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
            // Never recreate the log of a function deleted meanwhile.
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

    // {status, body} answers with that status; any other value is the 200 JSON body.
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
        new(record.Name, withSource ? record.Source : null, record.Trigger, record.Cron, record.CreatedAt, record.UpdatedAt);

    private static string Url(TenantContext tenant, string database, string name) => $"{FunctionTriggerReceiver.Scheme}{tenant.ProjectId}/{database}/{tenant.DatabaseId}/{name}";

    internal static string ScheduleId(TenantContext tenant, string name) => tenant.DatabaseId + "-" + name;

    private static ValueTask<FunctionRecord?> GetAsync(IStorageTransaction transaction, TenantContext tenant, string name, CancellationToken cancellationToken) =>
        SystemCatalog.GetAsync(transaction, Records(tenant), name, TerraBaseJsonContext.Default.FunctionRecord, cancellationToken);

    private static string Records(TenantContext tenant) => SystemCatalog.DatabaseState(tenant.DatabaseId!) + "functions";

    private static string Logs(TenantContext tenant, string name) => SystemCatalog.DatabaseState(tenant.DatabaseId!) + "function_logs/" + name;
}
