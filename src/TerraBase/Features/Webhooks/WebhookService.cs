using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
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
            .AddSingleton<IWebhookErasure>(provider => provider.GetRequiredService<WebhookService>())
            .AddSingleton<WebhookReplicaAdmission>()
            .AddSingleton<IWebhookReplicaAdmission>(provider => provider.GetRequiredService<WebhookReplicaAdmission>())
            .AddHostedService(provider => provider.GetRequiredService<WebhookService>());
}

internal sealed class WebhookService(IStorageEngine engine, SystemCatalog catalog, IWebhookTransport transport,
    TerraBaseOptions options, TimeProvider time, IMemoryBudget memory, IMeterFactory meters, ILogger<WebhookService> logger,
    ICatalogGuard guard, ICatalogGate gate, WebhookReplicaAdmission admission) : BackgroundService, IWebhooks, IWebhookErasure
{
    private const string Dispatches = "webhook_dispatch";
    private const int MaxBodyBytes = 512 * 1024;
    private const long WorkBytes = 4 * 1024 * 1024;
    private string? _after;
    // In-flight sends by owner-held token: erasure cancels them and waits for the owner's own cleanup, never disposes them.
    private readonly ConcurrentDictionary<CancellationTokenSource, TenantContext> _sends = new();
    private const int ErasePages = 4;
    private readonly SemaphoreSlim _erasing = new(1, 1);
    // A restart drops these cursors, so a pass restarts from the beginning; certification never trusts a partial pass.
    private readonly ConcurrentDictionary<string, (string? After, long Conflicts)> _erasures = new(StringComparer.Ordinal);
    // The conflict generation each database's last complete pass certified; Certifies compares it under the receipt's writer.
    private readonly ConcurrentDictionary<string, long> _certified = new(StringComparer.Ordinal);
    private TaskCompletionSource _passed = new(TaskCreationOptions.RunContinuationsAsynchronously);
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

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Warm existing state before listeners start; a fresh node does not open or create its catalog.
            _ = await engine.MayHaveSystemCollectionStateAsync(Dispatches, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.ZLogWarning($"Webhook discovery failed; persisted work will retry");
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
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
                finally
                {
                    Interlocked.Exchange(ref _passed, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
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
        if (!gate.IsOpen || !await engine.MayHaveSystemCollectionStateAsync(Dispatches, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        // Empty dispatch scans consume no buffer budget; retain admission only for rows actually read.
        using var pageMemory = new QosContext.ResultMemory(memory);
        var (dispatches, purged) = await catalog.ReadUncheckedAsync<(WebhookDispatch[], WebhookDispatch[])>(async transaction =>
        {
            using var scope = QosContext.EnterFlight(ChangeGroups.System, pageMemory);
            if (await guard.CheckReadAsync(transaction, cancellationToken).ConfigureAwait(false) is not Done)
            {
                return ([], []);
            }

            var page = await transaction.ListAsync(Dispatches, _after, 32, 64 * 1024, cancellationToken).ConfigureAwait(false);
            _after = page.HasMore && page.Documents.Count > 0 ? page.Documents[^1].Id : null;
            List<WebhookDispatch> owned = [], purged = [];
            foreach (var document in page.Documents)
            {
                // A replicated row without a readable tenant can name no scope: skip it, never fail the page for its neighbors.
                if (WebhookReplicaAdmission.TenantOf(document.Json.Span) is not (not null, not null, not null) || Read(document) is not { } dispatch)
                {
                    continue;
                }

                // Every node sweeps purged scopes, so an offline owner cannot retain them; mismatched authority holds the row untouched.
                switch (await AuthorityAsync(transaction, dispatch.Tenant, cancellationToken).ConfigureAwait(false))
                {
                    case true:
                        purged.Add(dispatch);
                        break;
                    case null when dispatch.Machine == options.MachineId:
                        owned.Add(dispatch);
                        break;
                }
            }

            return ([.. owned], [.. purged]);
        }, cancellationToken).ConfigureAwait(false);
        foreach (var group in purged.GroupBy(dispatch => dispatch.Tenant.DatabaseId))
        {
            var database = group.First().Tenant;
            CancelSends(database);
            try
            {
                _ = await PurgeAsync(database, [.. group.Select(dispatch => dispatch.Id)], cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.ZLogWarning($"Webhook erasure sweep failed; persisted work will retry");
            }
        }

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
        WebhookAttempt result;
        using (var send = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            // Track before the start write: that write refuses a purged scope, so erasure either sees this send or prevents it.
            _sends[send] = hook.Tenant;
            try
            {
                if (!await UpdateDeliveryAsync(hook, started, complete: false, send.Token).ConfigureAwait(false))
                {
                    return;
                }

                result = await transport.SendAsync(hook.Url, hook.Secret, delivery.Id, delivery.Body, now, send.Token).ConfigureAwait(false);
            }
            finally
            {
                _sends.TryRemove(send, out _);
            }
        }

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
                || current.Cursor != hook.Cursor || current.Pending is not null
                || await DatabasePurge.IsDeletedAsync(transaction, hook.Tenant.DatabaseId!, cancellationToken).ConfigureAwait(false))
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
            if (current is null || await DatabasePurge.IsDeletedAsync(transaction, hook.Tenant.DatabaseId!, cancellationToken).ConfigureAwait(false))
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
            if (current?.Pending != delivery.Id || await DatabasePurge.IsDeletedAsync(transaction, hook.Tenant.DatabaseId!, cancellationToken).ConfigureAwait(false))
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

    /// <summary>Completes when the dispatch pass running now, or else the next one, has finished every send it started.</summary>
    internal Task NextPassAsync() => Volatile.Read(ref _passed).Task;

    /// <inheritdoc />
    public async ValueTask<bool> EraseAsync(TenantContext database, CancellationToken cancellationToken)
    {
        using var reserved = memory.TryReserve(MemoryLane.Bulk, WorkBytes, database);
        if (reserved is null)
        {
            return false;
        }

        CancelSends(database);
        // One erasure at a time keeps each database's resumable cursor consistent; every call does bounded work.
        await _erasing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var id = database.DatabaseId!;
            _certified.TryRemove(id, out _);
            // A conflicting dispatch admitted since the pass began may sit behind its cursor: start the pass again.
            var (after, conflicts) = _erasures.TryGetValue(id, out var resumed) && resumed.Conflicts == admission.Conflicts(id) ? resumed : (null, 0);
            if (after is null)
            {
                // Snapshot under the writer: a replicated conflict counted but not yet committed has committed by now, so the scan sees it.
                if (await PurgeAsync(database, [], cancellationToken).ConfigureAwait(false) is not { } snapshot)
                {
                    return false;
                }

                conflicts = snapshot;
            }

            for (var page = 0; page < ErasePages; page++)
            {
                if (await ScanAsync(database, after, cancellationToken).ConfigureAwait(false) is not (var ids, var next)
                    || ids.Length > 0 && await PurgeAsync(database, ids, cancellationToken).ConfigureAwait(false) is null)
                {
                    _erasures.TryRemove(id, out _);
                    return false;
                }

                if (next is null)
                {
                    // The pass reached the end with every matching row deleted; certify only once local state and sends are gone.
                    _erasures.TryRemove(id, out _);
                    var erased = await PurgeAsync(database, [], cancellationToken).ConfigureAwait(false) == conflicts
                        && await catalog.ReadAsync(async transaction =>
                            await AuthorityAsync(transaction, database, cancellationToken).ConfigureAwait(false) is true
                            && !await transaction.HasCollectionStateAsync(Prefix(database), prefix: true, cancellationToken).ConfigureAwait(false),
                            static _ => false, cancellationToken).ConfigureAwait(false)
                        && !_sends.Values.Any(tenant => tenant.DatabaseId == id);
                    if (erased)
                    {
                        _certified[id] = conflicts;
                    }

                    return erased;
                }

                after = next;
            }

            _erasures[id] = (after, conflicts);
            return false;
        }
        finally
        {
            _erasing.Release();
        }
    }

    /// <inheritdoc />
    public bool Certifies(TenantContext database) =>
        _certified.TryGetValue(database.DatabaseId!, out var conflicts) && conflicts == admission.Conflicts(database.DatabaseId!);

    // shortcut: dispatch rows are keyed by webhook, so a pass walks the global collection ErasePages pages per call; index by database if it grows large.
    private async ValueTask<(string[] Ids, string? Next)?> ScanAsync(TenantContext database, string? after, CancellationToken cancellationToken)
    {
        var page = await catalog.ReadAsync<DocumentPage?>(async transaction =>
            await AuthorityAsync(transaction, database, cancellationToken).ConfigureAwait(false) is true
                ? await transaction.ListAsync(Dispatches, after, 256, 1024 * 1024, cancellationToken).ConfigureAwait(false)
                : null, static _ => null, cancellationToken).ConfigureAwait(false);
        if (page is null)
        {
            return null;
        }

        List<string> ids = [];
        var named = Encoding.UTF8.GetBytes(database.DatabaseId!);
        foreach (var document in page.Documents)
        {
            // A row stored with the reserved wrapper field reads back unwrapped, hiding its tenant: never certify around it.
            if (document.Json.Span.IndexOf(named) >= 0 && !WebhookReplicaAdmission.IsWholeObject(document.Json.Span))
            {
                return null;
            }

            // Read only the tenant's ids, so one malformed row cannot stop every later erasure.
            var (organization, project, owner) = WebhookReplicaAdmission.TenantOf(document.Json.Span);
            if (owner != database.DatabaseId)
            {
                continue;
            }

            // A claim without an organization or project is the purged scope, as admission treats it; another named scope is a conflict.
            if (!Claims(database, organization, project))
            {
                return null;
            }

            ids.Add(document.Id);
        }

        return ([.. ids], page.HasMore && page.Documents.Count > 0 ? page.Documents[^1].Id : null);
    }

    // Returns the conflict generation read under the writer, or null when refused.
    private ValueTask<long?> PurgeAsync(TenantContext database, string[] ids, CancellationToken cancellationToken) =>
        catalog.WriteDurableAsync<long?>(async transaction =>
        {
            if (await AuthorityAsync(transaction, database, cancellationToken).ConfigureAwait(false) is not true)
            {
                return null;
            }

            foreach (var id in ids)
            {
                if (await transaction.GetAsync(Dispatches, id, cancellationToken).ConfigureAwait(false) is { Deleted: false } document
                    && WebhookReplicaAdmission.TenantOf(document.Json.Span) is var (organization, project, owner)
                    && owner == database.DatabaseId && Claims(database, organization, project))
                {
                    await transaction.DeleteAsync(Dispatches, id, cancellationToken).ConfigureAwait(false);
                }
            }

            // Registrations, cursors and delivery bodies are node-local purges, like the database's own prefix cleanup.
            await transaction.DeleteCollectionsAsync(Prefix(database), cancellationToken).ConfigureAwait(false);
            return admission.Conflicts(database.DatabaseId!);
        }, static _ => null, cancellationToken);

    /// <summary>True for a matching permanent purge, false for a conflicting or unreadable one, null while the database is not purged.</summary>
    private static async ValueTask<bool?> AuthorityAsync(IStorageTransaction transaction, TenantContext database, CancellationToken cancellationToken) =>
        await WebhookReplicaAdmission.PurgeAsync(transaction, database.DatabaseId!, cancellationToken).ConfigureAwait(false) switch
        {
            null => null,
            (true, var purged) => DatabasePurge.SameDatabase(purged!, database),
            _ => false,
        };

    private void CancelSends(TenantContext database)
    {
        foreach (var (send, tenant) in _sends)
        {
            if (DatabasePurge.SameDatabase(tenant, database))
            {
                try
                {
                    send.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The owner already finished and released this send.
                }
            }
        }
    }

    // Admission refuses a claim missing its organization or project as the purged scope, so erasure deletes it the same way.
    private static bool Claims(TenantContext database, string? organization, string? project) =>
        organization is null || project is null || organization == database.OrganizationId && project == database.ProjectId;

    private static WebhookDispatch? Read(StoredDocument document)
    {
        try
        {
            return WebhookReplicaAdmission.IsObject(document)
                && EntityPayload.Read(document, TerraBaseJsonContext.Default.WebhookDispatch) is { Tenant.DatabaseId: not null, Id: not null } dispatch ? dispatch : null;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            // ArgumentException: a repeated nested key, which the canonical wire decoder accepts.
            return null;
        }
    }

    private static TimeSpan RetryDelay(long attempts) => TimeSpan.FromSeconds(Math.Min(3600, Math.Pow(2, Math.Min(attempts - 1, 12))));

    private static string Cursor(FeedPage page, DateTimeOffset now) =>
        ChangeCursor.Encode(new FeedCursor(page.Scanned, FeedCursor.At(now)) { Issuer = page.Issuer, Prefixes = page.Prefixes });

    private static bool Owns(WebhookRecord hook, TenantContext tenant, string collection) =>
        hook.Tenant.OrganizationId == tenant.OrganizationId && hook.Tenant.ProjectId == tenant.ProjectId
        && hook.Tenant.DatabaseId == tenant.DatabaseId && hook.Collection == collection;

    private static string Hooks(TenantContext tenant, string collection) => SystemCatalog.DatabaseState(tenant.DatabaseId!) + "webhooks/" + collection;
    private static string Deliveries(TenantContext tenant, string id) => SystemCatalog.DatabaseState(tenant.DatabaseId!) + "webhook_deliveries/" + id;
    private static readonly Unavailable Busy = new("webhook storage is temporarily unavailable");

    private static string Prefix(TenantContext tenant) => SystemCatalog.DatabaseState(tenant.DatabaseId!) + "webhook";
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

internal sealed class WebhookReplicaAdmission(IMeterFactory meters) : IWebhookReplicaAdmission
{
    private const string Dispatches = "webhook_dispatch";
    private readonly ConcurrentDictionary<string, long> _conflicts = new(StringComparer.Ordinal);
    private readonly Counter<long> _refused = meters.Create(Telemetry.MeterName)
        .CreateCounter<long>("terrabase.webhooks.replica_refused", "{state}", "Replicated webhook states refused under permanent deletion authority.");
    private readonly Counter<long> _conflicting = meters.Create(Telemetry.MeterName)
        .CreateCounter<long>("terrabase.webhooks.replica_conflicts", "{state}", "Replicated webhook dispatches kept because their scope conflicts with a permanent deletion.");

    public bool Decides(string collection) => collection == Dispatches || DatabaseOf(collection) is not null;

    /// <summary>How many conflicting dispatches this process admitted for the database; erasure certifies one value of it.</summary>
    public long Conflicts(string databaseId) => _conflicts.GetValueOrDefault(databaseId);

    public async ValueTask<bool> AdmitAsync(IStorageTransaction transaction, string collection, string id, DocumentState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(state);
        if (DatabaseOf(collection) is { } owner)
        {
            // The prefix is the database's own namespace: refuse all of it under valid authority, without reading the body.
            return await PurgeAsync(transaction, owner, cancellationToken).ConfigureAwait(false) is not (true, _) || Refuse();
        }

        if (state.Materialize() is not { } body)
        {
            // A deletion carries no payload; joining it keeps the CRDT tombstone and its collection floor.
            return true;
        }

        // The reserved scalar-wrapper field, which local writes refuse, would hide this row's tenant when read back.
        if (state.Fields.ContainsKey("\0"))
        {
            return Refuse();
        }

        // Dispatches are whole puts, so a delta names its tenant; fall back to the local row's.
        var tenant = TenantOf(body);
        if (tenant.DatabaseId is null && await transaction.GetAsync(Dispatches, id, cancellationToken).ConfigureAwait(false) is { Deleted: false } local)
        {
            tenant = TenantOf(local.Json.Span);
        }

        if (tenant.DatabaseId is not { } database || await PurgeAsync(transaction, database, cancellationToken).ConfigureAwait(false) is not (true, var purged))
        {
            return true;
        }

        if (tenant.OrganizationId is null || tenant.ProjectId is null
            || tenant.OrganizationId == purged!.OrganizationId && tenant.ProjectId == purged.ProjectId)
        {
            // The purged scope, or a malformed claim on it.
            return Refuse();
        }

        // Another scope's row is kept, never discarded; erasure certifies nothing for this database while it exists.
        _conflicts.AddOrUpdate(database, 1, static (_, count) => count + 1);
        _conflicting.Add(1);
        return true;
    }

    /// <summary>
    /// The permanent purge of <paramref name="databaseId"/>: null when absent, (false, null) when unreadable or of another
    /// format, which no node treats as authority, otherwise (true, its database).
    /// </summary>
    internal static async ValueTask<(bool Valid, TenantContext? Database)?> PurgeAsync(IStorageTransaction transaction, string databaseId, CancellationToken cancellationToken)
    {
        if (await transaction.GetAsync(DatabasePurge.Collection, databaseId, cancellationToken).ConfigureAwait(false) is not { Deleted: false } document)
        {
            return null;
        }

        // A replicated scalar ({"\u0000": value}) reads back unwrapped and is no record.
        if (!IsObject(document))
        {
            return (false, null);
        }

        try
        {
            return EntityPayload.Read(document, TerraBaseJsonContext.Default.DatabasePurgeRecord) is { FormatVersion: 1 } purge
                && purge.Database?.DatabaseId == databaseId && purge.Database.OrganizationId is not null && purge.Database.ProjectId is not null
                ? (true, purge.Database)
                : (false, null);
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            // ArgumentException: a repeated nested key, which the canonical wire decoder accepts.
            return (false, null);
        }
    }

    /// <summary>
    /// The string ids of a stored dispatch's tenant, matched case-insensitively like the typed reader; each null when
    /// missing, malformed or not decodable (an unpaired surrogate). Never throws, and reads past undecodable tokens.
    /// </summary>
    internal static (string? OrganizationId, string? ProjectId, string? DatabaseId) TenantOf(ReadOnlySpan<byte> body)
    {
        // The decoder bounds each field value to MaxStoredDepth; a set element and the root object add one level each.
        var reader = new Utf8JsonReader(body, new JsonReaderOptions { MaxDepth = DocumentJson.MaxStoredDepth + 2 });
        string? organization = null, project = null, database = null;
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return default;
            }

            var found = false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var tenant = string.Equals(Text(ref reader), "tenant", StringComparison.OrdinalIgnoreCase);
                if (!reader.Read())
                {
                    break;
                }

                if (!tenant || reader.TokenType != JsonTokenType.StartObject)
                {
                    reader.Skip();
                    continue;
                }

                // The last tenant wins, as in the typed reader.
                found = true;
                organization = project = database = null;
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    var name = Text(ref reader);
                    if (!reader.Read())
                    {
                        break;
                    }

                    var value = reader.TokenType == JsonTokenType.String ? Text(ref reader) : null;
                    reader.Skip();
                    if (string.Equals(name, "organizationId", StringComparison.OrdinalIgnoreCase))
                    {
                        organization = value;
                    }
                    else if (string.Equals(name, "projectId", StringComparison.OrdinalIgnoreCase))
                    {
                        project = value;
                    }
                    else if (string.Equals(name, "databaseId", StringComparison.OrdinalIgnoreCase))
                    {
                        database = value;
                    }
                }
            }

            return found ? (organization, project, database) : default;
        }
        catch (JsonException)
        {
            // A malformed body still yields the ids read before the fault.
        }

        return (organization, project, database);
    }

    internal static bool IsObject(StoredDocument document) => document.Json.Span.TrimStart(" \t\r\n"u8) is [(byte)'{', ..];

    // Exactly one JSON object and nothing after it: an unwrapped reserved-field row can start with an object and continue.
    internal static bool IsWholeObject(ReadOnlySpan<byte> body)
    {
        var reader = new Utf8JsonReader(body, new JsonReaderOptions { MaxDepth = DocumentJson.MaxStoredDepth + 2 });
        try
        {
            return reader.Read() && reader.TokenType == JsonTokenType.StartObject && reader.TrySkip() && !reader.Read();
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Text(ref Utf8JsonReader reader)
    {
        try
        {
            return reader.GetString();
        }
        catch (InvalidOperationException)
        {
            // An escaped unpaired surrogate does not decode to a string; it names nothing.
            return null;
        }
    }

    private bool Refuse()
    {
        _refused.Add(1);
        return false;
    }

    // databases/<id>/webhooks/<collection>, databases/<id>/webhook_state and databases/<id>/webhook_deliveries/<hook>.
    private static string? DatabaseOf(string collection)
    {
        const string Prefix = "databases/";
        if (!collection.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var slash = collection.IndexOf('/', Prefix.Length);
        return slash > Prefix.Length && collection.AsSpan(slash + 1).StartsWith("webhook", StringComparison.Ordinal)
            ? collection[Prefix.Length..slash]
            : null;
    }
}
