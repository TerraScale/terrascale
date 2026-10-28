using System.Diagnostics.Metrics;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using static TerraBase.Features.Management.SystemCatalog;

namespace TerraBase.Features.Management.Logs;

internal sealed class RequestLogRecorder(SystemCatalog catalog, TimeProvider time, IMemoryBudget memory,
    TerraBaseOptions options, IMeterFactory meters) : IRequestLogs, IAsyncDisposable
{
    private const int Kept = 100;
    private const int MaxPending = 1024;
    private const int MaxSaved = 512;
    private const int MaxRecordBytes = 4096;
    private const int FlushPageEntries = 32;
    private const int FlushPageBytes = FlushPageEntries * MaxRecordBytes;
    // Latest decoded records plus one page, overlapping raw pages, saved IDs, a concurrently evicted batch and serialization.
    private const long FlushBytes = (Kept + FlushPageEntries) * (2L * MaxRecordBytes + 2048)
        + 2L * FlushPageBytes + MaxSaved * 128L + Kept * MaxRecordBytes + 128 * 1024;
    private const long ReadBytes = 4 * 1024 * 1024;
    private const string Format = "alpha";
    // Remote observations reach the catalog after a flush tick plus replication; cursor reads only cover requests this old.
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(30);
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly Dictionary<DatabaseOwner, List<Entry>> _pending = [];
    private readonly Counter<long> _drops = meters.Create(Telemetry.MeterName).CreateCounter<long>(
        "terrabase.request_logs.dropped", "{observation}", "Request observations omitted at bounded memory capacity.");
    private int _count;
    private bool _disposed;

    public void Record(TenantContext database, string method, string route, int status, TimeSpan elapsed, long operations, string? requestId = null,
        long statements = 0, long statementErrors = 0)
    {
        if (!EntityIds.IsValid(database.OrganizationId) || !EntityIds.IsValid(database.ProjectId) || !EntityIds.IsValid(database.DatabaseId)
            || route.Length is 0 or > 512 || route.Contains('?', StringComparison.Ordinal) || route.EndsWith("/logs", StringComparison.Ordinal)
            || status is < 100 or > 599 || elapsed < TimeSpan.Zero || operations < 0 || statements < 0 || statementErrors is < 0 || statementErrors > statements) { return; }
        method = method is "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS" ? method : "OTHER";
        lock (_gate)
        {
            if (_disposed) { return; }
            var owner = DatabaseOwner.Of(database);
            _pending.TryGetValue(owner, out var entries);
            if (entries?.Count == Kept)
            {
                entries[0].Memory.Dispose();
                entries.RemoveAt(0);
                _count--;
            }
            // Retained server diagnostics belong to the process, even inside the completing request's QoS context.
            if (_count >= MaxPending || memory.TryReserve(MemoryLane.Foreground, MaxRecordBytes, tenant: null) is not { } reserved)
            {
                RecordingLimited = true;
                _drops.Add(1);
                return;
            }
            entries ??= [];
            _pending[owner] = entries;
            entries.Add(new(new(EntityIds.New(time).ToString(), time.GetUtcNow(), options.Region, method, route, status,
                elapsed.TotalMilliseconds, operations, route.EndsWith("/query", StringComparison.Ordinal) || route.EndsWith("/sql", StringComparison.Ordinal) || statements > 0,
                TerraScale.Endpoints.RequestIdentity.IsValid(requestId) ? requestId : null, statements, statementErrors), reserved));
            _count++;
        }
    }

    public async ValueTask<RequestLogReadResult> ReadAsync(TenantContext database, int? statusClass, string? after, CancellationToken cancellationToken)
    {
        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        IMemoryReservation? reserved = null;
        try
        {
            lock (_gate) { if (_disposed) { return new Unavailable("request logs are shutting down"); } }
            reserved = memory.TryReserve(MemoryLane.Foreground, ReadBytes, database);
            if (reserved is null) { return new Unavailable("request logs are at memory capacity", "capacity"); }
            var admission = reserved;
            var result = await catalog.ReadAsync<RequestLogReadResult>(async transaction =>
            {
                var saved = await LoadAsync(transaction, database.DatabaseId!, cancellationToken).ConfigureAwait(false);
                if (saved is null) { return new Unavailable("request logs are unavailable or exceed the read budget"); }
                DatabaseRequestLog[] pending;
                lock (_gate) { pending = _pending.TryGetValue(DatabaseOwner.Of(database), out var entries) ? [.. entries.Select(entry => entry.Log)] : []; }
                var recent = Recent(saved.Concat(pending)).ToArray();
                var window = recent.Take(Kept).Where(log => statusClass is null || log.Status / 100 == statusClass).ToArray();
                var body = new DatabaseRequestLogs(database.DatabaseId!, window, recent.Length >= Kept, RecordingLimited);
                return new RequestLogRead(after is null ? body : Page(body, recent, after), admission);
            }, static error => error, cancellationToken).ConfigureAwait(false);
            if (result is RequestLogRead) { reserved = null; }
            return result;
        }
        catch (IOException)
        {
            return new Unavailable("request logs could not be read");
        }
        finally
        {
            reserved?.Dispose();
            _io.Release();
        }
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DatabaseOwner[] owners;
            int evictions;
            // Bound losses during unrelated foreground pressure; later ticks can retry what remains.
            lock (_gate)
            {
                owners = [.. _pending.Keys];
                evictions = Math.Min(Kept, _count / 2);
            }
            foreach (var owner in owners)
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (await FlushOwnerAsync(owner, cancellationToken).ConfigureAwait(false)) { break; }
                    }
                    catch (QosMemoryException) { }
                    // The failed attempt has released its batch and admission before any lease is discarded.
                    if (evictions-- == 0 || !DiscardOldest()) { return; }
                }
            }
        }
        finally { _io.Release(); }
    }

    // Every retained request between the cursor and the settled boundary is returned, so the boundary is the next cursor.
    private DatabaseRequestLogs Page(DatabaseRequestLogs body, DatabaseRequestLog[] recent, string after)
    {
        var now = time.GetUtcNow();
        var settled = Ulid.NewUlid(now - Settle, stackalloc byte[10]).ToString();
        var items = body.Items.Where(log => string.CompareOrdinal(log.Id, after) > 0 && string.CompareOrdinal(log.Id, settled) < 0)
            .OrderBy(log => log.Id, StringComparer.Ordinal).ToArray();
        var evicted = body.Truncated && string.CompareOrdinal(recent[Kept - 1].Id, after) > 0;
        return body with
        {
            Items = items,
            NextCursor = string.CompareOrdinal(settled, after) > 0 ? settled : after,
            Gap = evicted || Ulid.Parse(after).Time < now.AddHours(-24),
        };
    }

    private async ValueTask<bool> FlushOwnerAsync(DatabaseOwner owner, CancellationToken cancellationToken)
    {
        lock (_gate) { if (!_pending.ContainsKey(owner)) { return true; } }
        using var admission = memory.TryReserve(MemoryLane.Foreground, FlushBytes, tenant: null);
        if (admission is null) { return false; }
        DatabaseRequestLog[] batch;
        lock (_gate)
        {
            if (!_pending.TryGetValue(owner, out var entries)) { return true; }
            batch = [.. entries.Select(entry => entry.Log)];
        }
        var saved = await catalog.WriteAsync<bool>(async transaction =>
        {
            // Deleting a database must not let a delayed observation recreate its state.
            if (await EngineManagementService.DatabaseTenantUncheckedAsync(transaction, owner.OrganizationId, owner.ProjectId, owner.DatabaseId, cancellationToken).ConfigureAwait(false) is null) { return true; }
            var collection = Records(owner.DatabaseId);
            var previous = new List<DatabaseRequestLog>(Kept);
            var savedIds = new List<string>(MaxSaved);
            string? after = null;
            DocumentPage page;
            do
            {
                // A page may include an oversized first row; admit its actual bytes before the engine copies it.
                using var pageAdmission = QosContext.Enter(tenant: null, budget: memory);
                page = await transaction.ListAsync(collection, after, FlushPageEntries, FlushPageBytes, cancellationToken).ConfigureAwait(false);
                foreach (var document in page.Documents)
                {
                    if (savedIds.Count == MaxSaved)
                    {
                        // Replication can temporarily join several bounded rings. Trim before admitting more.
                        foreach (var id in savedIds.Take(Kept)) { await transaction.DeleteAsync(collection, id, cancellationToken).ConfigureAwait(false); }
                        return false;
                    }
                    if (Decode(document) is not { } log) { return false; }
                    savedIds.Add(document.Id);
                    previous.Add(log);
                    after = document.Id;
                }
                previous = [.. Recent(previous).Take(Kept)];
            }
            while (page.HasMore);
            var kept = Recent(previous.Concat(batch)).Take(Kept).ToArray();
            var ids = kept.Select(log => log.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in savedIds.Where(id => !ids.Contains(id))) { await transaction.DeleteAsync(collection, id, cancellationToken).ConfigureAwait(false); }
            foreach (var log in kept.Where(log => batch.Any(item => item.Id == log.Id))) { await PutAsync(transaction, collection, log.Id, new RequestLogRecord(Format, log), TerraBaseJsonContext.Default.RequestLogRecord, cancellationToken).ConfigureAwait(false); }
            return true;
        }, static _ => false, cancellationToken).ConfigureAwait(false);
        if (!saved) { return true; }
        var flushed = batch.Select(log => log.Id).ToHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            var entries = _pending[owner];
            foreach (var entry in entries.Where(entry => flushed.Contains(entry.Log.Id))) { entry.Memory.Dispose(); }
            _count -= entries.RemoveAll(entry => flushed.Contains(entry.Log.Id));
            if (entries.Count == 0) { _pending.Remove(owner); }
        }
        return true;
    }

    private bool DiscardOldest()
    {
        lock (_gate)
        {
            if (_pending.Count == 0) { return false; }
            var oldest = _pending.MinBy(pair => pair.Value[0].Log.Time);
            var entries = oldest.Value;
            entries[0].Memory.Dispose();
            entries.RemoveAt(0);
            _count--;
            if (entries.Count == 0) { _pending.Remove(oldest.Key); }
            RecordingLimited = true;
            _drops.Add(1);
            return true;
        }
    }

    private static async ValueTask<DatabaseRequestLog[]?> LoadAsync(IStorageTransaction transaction, string databaseId, CancellationToken cancellationToken)
    {
        var page = await transaction.ListAsync(Records(databaseId), null, MaxSaved, 512 * 1024, cancellationToken).ConfigureAwait(false);
        return page.HasMore ? null : Decode(page);
    }

    private static DatabaseRequestLog[]? Decode(DocumentPage page)
    {
        var result = new List<DatabaseRequestLog>(page.Documents.Count);
        foreach (var document in page.Documents)
        {
            if (Decode(document) is not { } log) { return null; }
            result.Add(log);
        }
        return [.. result];
    }

    private static DatabaseRequestLog? Decode(StoredDocument document)
    {
        if (document.Json.Length > MaxRecordBytes) { return null; }
        RequestLogRecord? record;
        try { record = EntityPayload.Read(document, TerraBaseJsonContext.Default.RequestLogRecord); }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return null; }
        if (record is not { Format: Format, Request: { } log } || log.Id != document.Id || !EntityIds.IsValid(log.Id)
            || log.Time == default || log.Route is not { Length: > 0 and <= 512 } || log.Route.Contains('?', StringComparison.Ordinal)
            || log.Status is < 100 or > 599 || !double.IsFinite(log.LatencyMs) || log.LatencyMs < 0 || log.Operations < 0) { return null; }
        return log;
    }

    private IEnumerable<DatabaseRequestLog> Recent(IEnumerable<DatabaseRequestLog> logs) => logs
        .Where(log => log.Time >= time.GetUtcNow().AddHours(-24) && log.Time <= time.GetUtcNow())
        .DistinctBy(log => log.Id).OrderByDescending(log => log.Time).ThenByDescending(log => log.Id, StringComparer.Ordinal);

    private bool RecordingLimited { get { lock (_gate) { return field; } } set; }

    private static string Records(string databaseId) => DatabaseState(databaseId) + "request_logs";

    public async ValueTask DisposeAsync()
    {
        lock (_gate) { _disposed = true; }
        await _io.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                foreach (var entry in _pending.Values.SelectMany(entries => entries)) { entry.Memory.Dispose(); }
                _pending.Clear();
                _count = 0;
            }
        }
        finally { _io.Release(); }
    }

    private sealed record Entry(DatabaseRequestLog Log, IMemoryReservation Memory);
}
