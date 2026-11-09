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
    private const long FlushBytes = (Kept + FlushPageEntries) * (2L * MaxRecordBytes + 2048)
        + 2L * FlushPageBytes + MaxSaved * 128L + Kept * MaxRecordBytes + 128 * 1024;
    private const long ReadBytes = 4 * 1024 * 1024;
    private const string Format = "alpha";
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(30);
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly Dictionary<DatabaseOwner, Ring> _pending = [];
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
            IMemoryReservation reserved;
            if (entries?.Count == Kept)
            {
                reserved = entries.RemoveOldest().Memory;
                _count--;
            }
            else if (_count >= MaxPending || memory.TryReserve(MaxRecordBytes, tenant: null) is not { } admitted)
            {
                RecordingLimited = true;
                _drops.Add(1);
                return;
            }
            else
            {
                reserved = admitted;
            }

            if (entries is null)
            {
                entries = new Ring();
                _pending.Add(owner, entries);
            }

            entries.Append(new(EntityIds.New(time), time.GetUtcNow(), method, route, status,
                elapsed.TotalMilliseconds, operations, route.EndsWith("/query", StringComparison.Ordinal) || route.EndsWith("/sql", StringComparison.Ordinal) || statements > 0,
                TerraScale.Endpoints.RequestIdentity.IsValid(requestId) ? requestId : null, statements, statementErrors, reserved));
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
            reserved = memory.TryReserve(ReadBytes, database);
            if (reserved is null) { return new Unavailable("request logs are at memory capacity", "capacity"); }
            var admission = reserved;
            var result = await catalog.ReadAsync<RequestLogReadResult>(async transaction =>
            {
                var saved = await LoadAsync(transaction, database.DatabaseId!, cancellationToken).ConfigureAwait(false);
                if (saved is null) { return new Unavailable("request logs are unavailable or exceed the read budget"); }
                DatabaseRequestLog[] pending;
                lock (_gate) { pending = _pending.TryGetValue(DatabaseOwner.Of(database), out var entries) ? entries.Logs(options.Region) : []; }
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
                    if (evictions-- == 0 || !DiscardOldest()) { return; }
                }
            }
        }
        finally { _io.Release(); }
    }

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
        using var admission = memory.TryReserve(FlushBytes, tenant: null);
        if (admission is null) { return false; }
        DatabaseRequestLog[] batch;
        lock (_gate)
        {
            if (!_pending.TryGetValue(owner, out var entries)) { return true; }
            batch = entries.Logs(options.Region);
        }
        var saved = await catalog.WriteAsync<bool>(async transaction =>
        {
            if (await EngineManagementService.DatabaseTenantUncheckedAsync(transaction, owner.OrganizationId, owner.ProjectId, owner.DatabaseId, cancellationToken).ConfigureAwait(false) is null) { return true; }
            var kind = Records(owner.DatabaseId);
            var previous = new List<DatabaseRequestLog>(Kept);
            var savedIds = new List<string>(MaxSaved);
            string? after = null;
            RecordPage page;
            do
            {
                using var pageAdmission = QosContext.Enter(tenant: null, budget: memory);
                page = await transaction.ListAsync(kind, after, FlushPageEntries, FlushPageBytes, cancellationToken).ConfigureAwait(false);
                foreach (var stored in page.Records)
                {
                    if (savedIds.Count == MaxSaved)
                    {
                        foreach (var id in savedIds.Take(Kept)) { await transaction.DeleteAsync(kind, id, cancellationToken).ConfigureAwait(false); }
                        return false;
                    }
                    if (Decode(stored) is not { } log) { return false; }
                    savedIds.Add(stored.Id);
                    previous.Add(log);
                    after = stored.Id;
                }
                previous = [.. Recent(previous).Take(Kept)];
            }
            while (page.HasMore);
            var kept = Recent(previous.Concat(batch)).Take(Kept).ToArray();
            var ids = kept.Select(log => log.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in savedIds.Where(id => !ids.Contains(id))) { await transaction.DeleteAsync(kind, id, cancellationToken).ConfigureAwait(false); }
            foreach (var log in kept.Where(log => batch.Any(item => item.Id == log.Id))) { await PutAsync(transaction, kind, log.Id, new RequestLogRecord(Format, log), TerraBaseJsonContext.Default.RequestLogRecord, cancellationToken).ConfigureAwait(false); }
            return true;
        }, static _ => false, cancellationToken).ConfigureAwait(false);
        if (!saved) { return true; }
        var flushed = batch.Select(log => log.Id).ToHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            var entries = _pending[owner];
            while (entries.Count > 0 && flushed.Contains(entries.Oldest.Id.ToString()))
            {
                entries.RemoveOldest().Memory.Dispose();
                _count--;
            }
            if (entries.Count == 0) { _pending.Remove(owner); }
        }
        return true;
    }

    private bool DiscardOldest()
    {
        lock (_gate)
        {
            if (_pending.Count == 0) { return false; }
            var oldest = _pending.MinBy(pair => pair.Value.Oldest.Time);
            var entries = oldest.Value;
            entries.RemoveOldest().Memory.Dispose();
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

    private static DatabaseRequestLog[]? Decode(RecordPage page)
    {
        var result = new List<DatabaseRequestLog>(page.Records.Count);
        foreach (var stored in page.Records)
        {
            if (Decode(stored) is not { } log) { return null; }
            result.Add(log);
        }
        return [.. result];
    }

    private static DatabaseRequestLog? Decode(StoredRecord stored)
    {
        if (stored.Json.Length > MaxRecordBytes) { return null; }
        RequestLogRecord? record;
        try { record = EntityPayload.Read(stored, TerraBaseJsonContext.Default.RequestLogRecord); }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return null; }
        if (record is not { Format: Format, Request: { } log } || log.Id != stored.Id || !EntityIds.IsValid(log.Id)
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
                foreach (var entries in _pending.Values)
                {
                    while (entries.Count > 0) { entries.RemoveOldest().Memory.Dispose(); }
                }
                _pending.Clear();
                _count = 0;
            }
        }
        finally { _io.Release(); }
    }

    private readonly record struct Entry(Ulid Id, DateTimeOffset Time, string Method, string Route, int Status, double LatencyMs, long Operations,
        bool IsQuery, string? RequestId, long Statements, long StatementErrors, IMemoryReservation Memory)
    {
        public DatabaseRequestLog Log(string region) =>
            new(Id.ToString(), Time, region, Method, Route, Status, LatencyMs, Operations, IsQuery, RequestId, Statements, StatementErrors);
    }

    private sealed class Ring
    {
        private Entry[] _entries = new Entry[4];
        private int _start;

        public int Count { get; private set; }

        public Entry Oldest => _entries[_start];

        public void Append(in Entry entry)
        {
            if (Count == _entries.Length)
            {
                var grown = new Entry[Math.Min(Kept, _entries.Length * 2)];
                for (var i = 0; i < Count; i++)
                {
                    grown[i] = _entries[(_start + i) % _entries.Length];
                }

                (_entries, _start) = (grown, 0);
            }

            _entries[(_start + Count) % _entries.Length] = entry;
            Count++;
        }

        public Entry RemoveOldest()
        {
            var oldest = _entries[_start];
            _entries[_start] = default;
            _start = (_start + 1) % _entries.Length;
            Count--;
            return oldest;
        }

        public DatabaseRequestLog[] Logs(string region)
        {
            var logs = new DatabaseRequestLog[Count];
            for (var i = 0; i < Count; i++)
            {
                logs[i] = _entries[(_start + i) % _entries.Length].Log(region);
            }

            return logs;
        }
    }
}
