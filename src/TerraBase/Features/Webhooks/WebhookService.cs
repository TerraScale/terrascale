using System.Buffers.Text;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Documents;
using TerraBase.Features.Management;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Features.Replication;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Webhooks;

/// <summary>Registers durable webhook delivery and its public-only HTTPS transport.</summary>
public static class WebhooksExtensions
{
    /// <summary>Registers the always-on webhook worker and tenant-scoped API.</summary>
    public static IServiceCollection AddTerraBaseWebhooks(this IServiceCollection services) =>
        services.AddTerraBaseWebhookTransport().AddSingleton<WebhookService>()
            .AddSingleton<IWebhooks>(provider => provider.GetRequiredService<WebhookService>())
            .AddHostedService(provider => provider.GetRequiredService<WebhookService>());
}

internal sealed class WebhookService(IStorageEngine engine, SystemCatalog catalog, IWebhookTransport transport,
    TerraBaseOptions options, TimeProvider time, IMemoryBudget memory, IMeterFactory meters, ILogger<WebhookService> logger) : BackgroundService, IWebhooks
{
    private const string Dispatches = "webhook_dispatch";
    private const int MaxBodyBytes = 512 * 1024;
    private const long WorkBytes = 4 * 1024 * 1024;
    private string? _after;
    private readonly Counter<long> _attempts = meters.Create(Telemetry.MeterName)
        .CreateCounter<long>("terrabase.webhooks.attempts", "{attempt}", "Webhook attempts by bounded outcome category.");
    private readonly Counter<long> _gaps = meters.Create(Telemetry.MeterName)
        .CreateCounter<long>("terrabase.webhooks.feed_gaps", "{webhook}", "Webhooks paused after missing or oversized feed history.");

    public async ValueTask<WebhookResult> CreateAsync(TenantContext tenant, string database, string collection, string? url, CancellationToken cancellationToken)
    {
        using var reserved = memory.TryReserve(MemoryLane.Foreground, WorkBytes, tenant);
        if (reserved is null)
        {
            return Busy;
        }

        if (url is null || !await transport.ValidateAsync(url, cancellationToken).ConfigureAwait(false))
        {
            return new Validation("url must be a public HTTPS receiver on port 443 without credentials or fragment");
        }

        var store = await engine.OpenDatabaseAsync(tenant, cancellationToken).ConfigureAwait(false);
        if (store is null)
        {
            return new NotFound();
        }

        string collectionId;
        FeedPage head;
        var now = time.GetUtcNow();
        await using (var read = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false))
        {
            if (await read.GetCollectionAsync(collection, cancellationToken).ConfigureAwait(false) is not { } found)
            {
                return new NotFound();
            }

            collectionId = found.Id;
            var cursor = new FeedCursor(await read.GetFeedHeadAsync(cancellationToken).ConfigureAwait(false), FeedCursor.At(now));
            head = await read.ReadFeedMetadataAsync(cursor, new HashSet<string>(StringComparer.Ordinal) { collection }, 1, MaxBodyBytes,
                cancellationToken).ConfigureAwait(false);
        }

        var id = EntityIds.New().ToString();
        var secret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var record = new WebhookRecord(1, id, tenant, database, collection, collectionId, store.Incarnation.ToString(), options.MachineId,
            url, secret, now, "active", Cursor(head, now), null);
        return await catalog.WriteDurableAsync<WebhookResult>(async transaction =>
        {
            // Deletion purges this namespace atomically with its catalog row; never recreate it afterward.
            if (await SystemCatalog.GetAsync(transaction, $"projects/{tenant.ProjectId}/databases", tenant.DatabaseId!,
                TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken).ConfigureAwait(false) is null)
            {
                return new NotFound();
            }

            var existing = await transaction.ListAsync(Hooks(tenant, collection), null, 32, 2 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            if (existing.Documents.Count >= 32)
            {
                return new Conflict("at most 32 webhooks may be registered per collection", "webhook_limit");
            }

            await SystemCatalog.PutAsync(transaction, Hooks(tenant, collection), id,
                new WebhookRegistration(record.Format, id, tenant, database, collection, collectionId, record.Incarnation, record.Machine, url, secret, now),
                TerraBaseJsonContext.Default.WebhookRegistration, cancellationToken).ConfigureAwait(false);
            await SaveAsync(transaction, record, cancellationToken).ConfigureAwait(false);
            await SystemCatalog.PutAsync(transaction, Dispatches, id, new WebhookDispatch(id, tenant, collection, options.MachineId),
                TerraBaseJsonContext.Default.WebhookDispatch, cancellationToken).ConfigureAwait(false);
            return new CreatedWebhookBody(id, url, now, "active", secret);
        }, static error => error, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<WebhookResult> ListAsync(TenantContext tenant, string collection, string? after, int limit, CancellationToken cancellationToken)
    {
        using var reserved = memory.TryReserve(MemoryLane.Foreground, WorkBytes, tenant);
        if (reserved is null)
        {
            return Busy;
        }

        return await catalog.ReadAsync<WebhookResult>(async transaction =>
        {
            var page = await transaction.ListAsync(Hooks(tenant, collection), after, limit, 2 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            var hooks = new List<WebhookBody>();
            foreach (var document in page.Documents)
            {
                if (await GetAsync(transaction, tenant, collection, document.Id, cancellationToken).ConfigureAwait(false) is { } hook
                    && Owns(hook, tenant, collection))
                {
                    hooks.Add(new WebhookBody(hook.Id, hook.Url, hook.CreatedAt, hook.Status));
                }
            }

            return hooks.ToArray();
        }, static error => error, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<WebhookResult> DeleteAsync(TenantContext tenant, string collection, string id, CancellationToken cancellationToken)
    {
        using var reserved = memory.TryReserve(MemoryLane.Foreground, WorkBytes, tenant);
        if (reserved is null)
        {
            return Busy;
        }

        return await catalog.WriteDurableAsync<WebhookResult>(async transaction =>
        {
            if (await GetAsync(transaction, tenant, collection, id, cancellationToken).ConfigureAwait(false) is not { } hook
                || !Owns(hook, tenant, collection))
            {
                return new NotFound();
            }

            await transaction.DeleteAsync(Hooks(tenant, collection), id, cancellationToken).ConfigureAwait(false);
            await transaction.DeleteAsync(Dispatches, id, cancellationToken).ConfigureAwait(false);
            await transaction.DeleteAsync(States(tenant), id, cancellationToken).ConfigureAwait(false);
            await transaction.DropCollectionAsync(Deliveries(tenant, id), cancellationToken).ConfigureAwait(false);
            return new Done();
        }, static error => error, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<WebhookResult> DeliveriesAsync(TenantContext tenant, string collection, string id, string? after, int limit,
        CancellationToken cancellationToken)
    {
        using var reserved = memory.TryReserve(MemoryLane.Foreground, WorkBytes, tenant);
        if (reserved is null)
        {
            return Busy;
        }

        return await catalog.ReadAsync<WebhookResult>(async transaction =>
        {
            if (await GetAsync(transaction, tenant, collection, id, cancellationToken).ConfigureAwait(false) is not { } hook
                || !Owns(hook, tenant, collection))
            {
                return new NotFound();
            }

            var page = await transaction.ListAsync(Deliveries(tenant, id), after, limit, 2 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            return page.Documents.Select(document => EntityPayload.Read(document, TerraBaseJsonContext.Default.WebhookDeliveryRecord)!.Details).ToArray();
        }, static error => error, cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
        try
        {
            do
            {
                await Task.Yield();
                try
                {
                    await PassAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception) when (!stoppingToken.IsCancellationRequested)
                {
                    // Never log exception objects: transport/storage errors may contain a URL or secret-bearing document.
                    logger.ZLogWarning($"Webhook dispatch pass failed; persisted work will retry");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown leaves every unfinished batch and retry schedule in the catalog.
        }
    }

    private async Task PassAsync(CancellationToken cancellationToken)
    {
        if (!engine.SystemDatabaseExists)
        {
            return;
        }

        // Empty dispatch scans consume no buffer budget; retain admission only for rows actually read.
        using var pageMemory = new QosContext.ResultMemory(memory);
        var dispatches = await catalog.ReadAsync(async transaction =>
        {
            using var scope = QosContext.EnterFlight(ChangeGroups.System, pageMemory);
            var page = await transaction.ListAsync(Dispatches, _after, 32, 64 * 1024, cancellationToken).ConfigureAwait(false);
            _after = page.HasMore && page.Documents.Count > 0 ? page.Documents[^1].Id : null;
            return page.Documents.Select(document => EntityPayload.Read(document, TerraBaseJsonContext.Default.WebhookDispatch)!)
                .Where(dispatch => dispatch.Machine == options.MachineId).ToArray();
        }, static _ => [], cancellationToken).ConfigureAwait(false);
        await Parallel.ForEachAsync(dispatches, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (dispatch, token) =>
            {
                using var work = memory.TryReserve(MemoryLane.Bulk, WorkBytes, dispatch.Tenant);
                if (work is null)
                {
                    return;
                }

                try
                {
                    await ProcessAsync(dispatch, token).ConfigureAwait(false);
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    logger.ZLogWarning($"Webhook delivery failed; persisted work will retry");
                }
            }).ConfigureAwait(false);
    }

    private async Task ProcessAsync(WebhookDispatch dispatch, CancellationToken cancellationToken)
    {
        var hook = await catalog.ReadAsync(transaction => GetAsync(transaction, dispatch.Tenant, dispatch.Collection, dispatch.Id, cancellationToken),
            static _ => null, cancellationToken).ConfigureAwait(false);
        if (hook is null)
        {
            // Database deletion removes its prefixed documents; clear the small global dispatch reference afterward.
            await catalog.WriteDurableAsync(async transaction =>
                await transaction.DeleteAsync(Dispatches, dispatch.Id, cancellationToken).ConfigureAwait(false), static _ => false, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (hook.Format != 1 || !Owns(hook, dispatch.Tenant, dispatch.Collection) || hook.Machine != options.MachineId)
        {
            return;
        }

        if (hook.Pending is null && hook.Status == "active")
        {
            hook = await CaptureAsync(hook, cancellationToken).ConfigureAwait(false);
        }

        if (hook?.Pending is not { } pending)
        {
            return;
        }

        var delivery = await catalog.ReadAsync(transaction => SystemCatalog.GetAsync(transaction, Deliveries(hook.Tenant, hook.Id), pending,
            TerraBaseJsonContext.Default.WebhookDeliveryRecord, cancellationToken), static _ => null, cancellationToken).ConfigureAwait(false);
        if (delivery is null || delivery.Details.NextAttemptAt > time.GetUtcNow())
        {
            return;
        }

        var now = time.GetUtcNow();
        var attempts = delivery.Details.Attempts == long.MaxValue ? long.MaxValue : delivery.Details.Attempts + 1;
        var started = delivery with
        {
            Details = delivery.Details with
            {
                Attempts = attempts,
                LastAttemptAt = now,
                NextAttemptAt = now + TimeSpan.FromSeconds(10) + RetryDelay(attempts)
            }
        };
        if (!await UpdateDeliveryAsync(hook, started, complete: false, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var result = await transport.SendAsync(hook.Url, hook.Secret, delivery.Id, delivery.Body, now, cancellationToken).ConfigureAwait(false);
        var success = result.HttpStatus is >= 200 and <= 299 && result.ErrorCode is null;
        _attempts.Add(1, new KeyValuePair<string, object?>("outcome", result.ErrorCode ?? "success"));
        var finished = started with
        {
            Details = started.Details with
            {
                Status = success ? "delivered" : "pending",
                NextAttemptAt = success ? null : time.GetUtcNow() + RetryDelay(attempts),
                HttpStatus = result.HttpStatus,
                ErrorCode = result.ErrorCode
            },
            Body = success ? [] : started.Body
        };
        await UpdateDeliveryAsync(hook, finished, success, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<WebhookRecord?> CaptureAsync(WebhookRecord hook, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var cursor = ChangeCursor.Decode(hook.Cursor);
        if (cursor is null || cursor.Issued.Value < FeedCursor.At(now - FeedCursor.Retention).Value)
        {
            return await MarkGapAsync(hook, cancellationToken).ConfigureAwait(false);
        }

        var store = await engine.OpenDatabaseAsync(hook.Tenant, cancellationToken).ConfigureAwait(false);
        if (store is null || store.Incarnation.ToString() != hook.Incarnation)
        {
            return await MarkGapAsync(hook, cancellationToken).ConfigureAwait(false);
        }

        FeedPage page;
        await using (var read = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false))
        {
            if (await read.GetCollectionAsync(hook.Collection, cancellationToken).ConfigureAwait(false) is not { } collection
                || collection.Id != hook.CollectionId)
            {
                return await MarkGapAsync(hook, cancellationToken).ConfigureAwait(false);
            }

            page = await read.ReadFeedMetadataAsync(cursor, new HashSet<string>(StringComparer.Ordinal) { hook.Collection }, 1, MaxBodyBytes,
                cancellationToken).ConfigureAwait(false);
        }

        // Reserve a conservative JSON encoding bound before serializing a whole source transaction.
        if (page.Expired || page.Events.Count > 1024 || page.Events.Sum(change => (long)change.Id.Length * 6 + 128) > MaxBodyBytes - 1024)
        {
            return await MarkGapAsync(hook, cancellationToken).ConfigureAwait(false);
        }

        WebhookDeliveryRecord? delivery = null;
        if (page.Events.Count > 0)
        {
            var id = EntityIds.New().ToString();
            var payload = new WebhookPayload(id, hook.Id, hook.Tenant.ProjectId!, hook.Database, hook.Collection,
                [.. page.Events.Select(change => new WebhookChange(change.Origin.Value.ToString("x16", CultureInfo.InvariantCulture), change.Stamp.Value.ToString("x16", CultureInfo.InvariantCulture), change.Id, change.Deleted))]);
            var body = JsonSerializer.SerializeToUtf8Bytes(payload, TerraBaseJsonContext.Default.WebhookPayload);
            if (body.Length > MaxBodyBytes)
            {
                return await MarkGapAsync(hook, cancellationToken).ConfigureAwait(false);
            }

            delivery = new WebhookDeliveryRecord(id, new WebhookDeliveryBody(id, now, "pending", 0, null, now, null, null), body);
        }

        var next = hook with { Cursor = Cursor(page, now), Pending = delivery?.Id };
        return await catalog.WriteDurableAsync<WebhookRecord?>(async transaction =>
        {
            if (await GetAsync(transaction, hook.Tenant, hook.Collection, hook.Id, cancellationToken).ConfigureAwait(false) is not { } current
                || current.Cursor != hook.Cursor || current.Pending is not null)
            {
                return null;
            }

            if (delivery is not null)
            {
                var history = await transaction.ListAsync(Deliveries(hook.Tenant, hook.Id), null, 100, 2 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
                foreach (var old in history.Documents.Take(Math.Max(0, history.Documents.Count - 99)))
                {
                    await transaction.DeleteAsync(Deliveries(hook.Tenant, hook.Id), old.Id, cancellationToken).ConfigureAwait(false);
                }

                await PutDeliveryAsync(transaction, hook, delivery, cancellationToken).ConfigureAwait(false);
            }
            else if (cursor.Stamps.Count == page.Scanned.Count && cursor.Stamps.All(pair => page.Scanned.GetValueOrDefault(pair.Key) == pair.Value)
                && now - DateTimeOffset.FromUnixTimeMilliseconds((long)(cursor.Issued.Value >> 16)) < TimeSpan.FromHours(1))
            {
                return hook;
            }

            await SaveAsync(transaction, next, cancellationToken).ConfigureAwait(false);
            return next;
        }, static _ => null, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask<WebhookRecord?> MarkGapAsync(WebhookRecord hook, CancellationToken cancellationToken) =>
        catalog.WriteDurableAsync<WebhookRecord?>(async transaction =>
        {
            var current = await GetAsync(transaction, hook.Tenant, hook.Collection, hook.Id, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                return null;
            }

            var paused = current with { Status = "feed_gap" };
            if (current.Status != "feed_gap")
            {
                _gaps.Add(1);
            }
            await SaveAsync(transaction, paused, cancellationToken).ConfigureAwait(false);
            return paused;
        }, static _ => null, cancellationToken);

    private ValueTask<bool> UpdateDeliveryAsync(WebhookRecord hook, WebhookDeliveryRecord delivery, bool complete, CancellationToken cancellationToken) =>
        catalog.WriteDurableAsync(async transaction =>
        {
            var current = await GetAsync(transaction, hook.Tenant, hook.Collection, hook.Id, cancellationToken).ConfigureAwait(false);
            if (current?.Pending != delivery.Id)
            {
                return false;
            }

            await PutDeliveryAsync(transaction, hook, delivery, cancellationToken).ConfigureAwait(false);
            if (complete)
            {
                await SaveAsync(transaction, current with { Pending = null }, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }, static _ => false, cancellationToken);

    private static TimeSpan RetryDelay(long attempts) => TimeSpan.FromSeconds(Math.Min(3600, Math.Pow(2, Math.Min(attempts - 1, 12))));

    private static string Cursor(FeedPage page, DateTimeOffset now) =>
        ChangeCursor.Encode(new FeedCursor(page.Scanned, FeedCursor.At(now)) { Issuer = page.Issuer, Prefixes = page.Prefixes });

    private static bool Owns(WebhookRecord hook, TenantContext tenant, string collection) =>
        hook.Tenant.OrganizationId == tenant.OrganizationId && hook.Tenant.ProjectId == tenant.ProjectId
        && hook.Tenant.DatabaseId == tenant.DatabaseId && hook.Collection == collection;

    private static string Hooks(TenantContext tenant, string collection) => SystemCatalog.DatabaseState(tenant.DatabaseId!) + "webhooks/" + collection;
    private static string Deliveries(TenantContext tenant, string id) => SystemCatalog.DatabaseState(tenant.DatabaseId!) + "webhook_deliveries/" + id;
    private static readonly Unavailable Busy = new("webhook storage is temporarily unavailable");

    private static string States(TenantContext tenant) => SystemCatalog.DatabaseState(tenant.DatabaseId!) + "webhook_state";

    private static async ValueTask<WebhookRecord?> GetAsync(IStorageTransaction transaction, TenantContext tenant, string collection, string id,
        CancellationToken cancellationToken)
    {
        var registration = await SystemCatalog.GetAsync(transaction, Hooks(tenant, collection), id,
            TerraBaseJsonContext.Default.WebhookRegistration, cancellationToken).ConfigureAwait(false);
        if (registration is null || await SystemCatalog.GetAsync(transaction, States(tenant), id,
            TerraBaseJsonContext.Default.WebhookState, cancellationToken).ConfigureAwait(false) is not { } state)
        {
            return null;
        }

        return new WebhookRecord(registration.Format, id, registration.Tenant, registration.Database, registration.Collection, registration.CollectionId,
            registration.Incarnation, registration.Machine, registration.Url, registration.Secret, registration.CreatedAt, state.Status, state.Cursor, state.Pending);
    }

    private static ValueTask SaveAsync(IStorageTransaction transaction, WebhookRecord hook, CancellationToken cancellationToken) =>
        SystemCatalog.PutAsync(transaction, States(hook.Tenant), hook.Id, new WebhookState(hook.Id, hook.Cursor, hook.Status, hook.Pending),
            TerraBaseJsonContext.Default.WebhookState, cancellationToken);

    private static ValueTask PutDeliveryAsync(IStorageTransaction transaction, WebhookRecord hook, WebhookDeliveryRecord delivery, CancellationToken cancellationToken) =>
        SystemCatalog.PutAsync(transaction, Deliveries(hook.Tenant, hook.Id), delivery.Id, delivery, TerraBaseJsonContext.Default.WebhookDeliveryRecord, cancellationToken);
}
