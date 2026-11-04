using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Hosting;

namespace TerraBase.Features.LocalAck;

/// <summary>
/// The transactions this node holds for its in-region partners, as system records of kind <c>local_holds</c> keyed
/// <c>&lt;origin&gt;.&lt;organization&gt;.&lt;project&gt;.&lt;database&gt;.&lt;sequence:D20&gt;</c>, so one origin's
/// transactions of one database list in stream order. Every write is synced to disk before it returns.
/// </summary>
internal sealed class HoldStore(IStorageEngine engine, TimeProvider time) : IDisposable
{
    internal const string Kind = "local_holds";
    internal const int MaxHeld = 65_536;
    internal const long MaxHeldBytes = 1L << 30;
    private const int Page = 64;
    private const long PageBytes = 32L << 20;

    private readonly SemaphoreSlim _counting = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _contacts = new(StringComparer.Ordinal);
    private readonly DateTimeOffset _started = time.GetUtcNow();
    private readonly Lock _totalsGate = new();
    private bool _counted;
    private long _count;
    private long _bytes;

    internal long Count
    {
        get
        {
            lock (_totalsGate)
            {
                return _count;
            }
        }
    }

    public void Dispose() => _counting.Dispose();

    /// <summary>Records that <paramref name="origin"/> reached this node now.</summary>
    public void Touch(string origin) => _contacts[origin] = time.GetUtcNow();

    /// <summary>When <paramref name="origin"/> last reached this node; this node's start when it has not since.</summary>
    public DateTimeOffset LastContact(string origin) => _contacts.TryGetValue(origin, out var last) ? last : _started;

    /// <summary>Stores <paramref name="held"/> durably; false when this node already holds its limit.</summary>
    public async ValueTask<bool> PutAsync(HeldTransaction held, CancellationToken cancellationToken)
    {
        await CountAsync(cancellationToken).ConfigureAwait(false);
        var body = JsonSerializer.SerializeToUtf8Bytes(held, TerraBaseJsonContext.Default.HeldTransaction);
        lock (_totalsGate)
        {
            if (_count >= MaxHeld || _bytes + body.Length > MaxHeldBytes)
            {
                return false;
            }

            _count++;
            _bytes += body.Length;
        }

        var stored = false;
        try
        {
            var store = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await store.BeginDurableTransactionAsync(cancellationToken).ConfigureAwait(false);
            var id = Id(held.Origin!, held.Organization!, held.Project!, held.Database!, held.Sequence);
            var replaced = await transaction.GetAsync(Kind, id, cancellationToken).ConfigureAwait(false);
            _ = await transaction.PutAsync(Kind, id, body, cancellationToken).ConfigureAwait(false);
            _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            stored = true;
            if (replaced is not null)
            {
                Forget(replaced.Json.Length);
            }

            return true;
        }
        finally
        {
            if (!stored)
            {
                Forget(body.Length);
            }
        }
    }

    /// <summary>Drops <paramref name="origin"/>'s transactions of the database up to sequence <paramref name="through"/>.</summary>
    public async ValueTask ReleaseAsync(string origin, string organization, string project, string database, ulong through, CancellationToken cancellationToken)
    {
        await CountAsync(cancellationToken).ConfigureAwait(false);
        var prefix = $"{origin}.{organization}.{project}.{database}.";
        var store = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        while (true)
        {
            var dropped = 0;
            await using (var transaction = await store.BeginDurableTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                var page = await transaction.ListAsync(Kind, prefix, Page, PageBytes, cancellationToken).ConfigureAwait(false);
                long bytes = 0;
                foreach (var record in page.Records)
                {
                    if (!record.Id.StartsWith(prefix, StringComparison.Ordinal) || Sequence(record.Id) > through)
                    {
                        break;
                    }

                    _ = await transaction.DeleteAsync(Kind, record.Id, cancellationToken).ConfigureAwait(false);
                    dropped++;
                    bytes += record.Json.Length;
                }

                if (dropped == 0)
                {
                    return;
                }

                _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                Forget(bytes, dropped);
            }

            if (dropped < Page)
            {
                return;
            }
        }
    }

    /// <summary>Drops one held transaction once the home answered it.</summary>
    public async ValueTask DropAsync(HeldTransaction held, CancellationToken cancellationToken)
    {
        await CountAsync(cancellationToken).ConfigureAwait(false);
        var store = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await store.BeginDurableTransactionAsync(cancellationToken).ConfigureAwait(false);
        var id = Id(held.Origin!, held.Organization!, held.Project!, held.Database!, held.Sequence);
        if (await transaction.GetAsync(Kind, id, cancellationToken).ConfigureAwait(false) is { } record
            && await transaction.DeleteAsync(Kind, id, cancellationToken).ConfigureAwait(false))
        {
            _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            Forget(record.Json.Length);
        }
    }

    /// <summary>The origins this node holds transactions for.</summary>
    public async ValueTask<IReadOnlyList<string>> OriginsAsync(CancellationToken cancellationToken)
    {
        var origins = new List<string>();
        var store = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        string? after = null;
        while (true)
        {
            await using var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
            var page = await transaction.ListAsync(Kind, after, 1, PageBytes, cancellationToken).ConfigureAwait(false);
            if (page.Records is not [var record])
            {
                return origins;
            }

            var origin = record.Id[..record.Id.IndexOf('.', StringComparison.Ordinal)];
            origins.Add(origin);
            after = origin + "/";
        }
    }

    /// <summary>Up to <paramref name="limit"/> of <paramref name="origin"/>'s held transactions after <paramref name="after"/>, in key order.</summary>
    public async ValueTask<IReadOnlyList<HeldTransaction>> ListAsync(string origin, HeldTransaction? after, int limit, CancellationToken cancellationToken)
    {
        var prefix = origin + ".";
        var store = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        var page = await transaction.ListAsync(Kind, after is null ? prefix : Id(origin, after.Organization!, after.Project!, after.Database!, after.Sequence),
            limit, PageBytes, cancellationToken).ConfigureAwait(false);
        return [.. page.Records.TakeWhile(record => record.Id.StartsWith(prefix, StringComparison.Ordinal))
            .Select(record => JsonSerializer.Deserialize(record.Json.Span, TerraBaseJsonContext.Default.HeldTransaction))
            .Where(held => held is { Version: 1, Transaction: not null })
            .Select(held => held!)];
    }

    internal static string Id(string origin, string organization, string project, string database, ulong sequence) =>
        string.Create(CultureInfo.InvariantCulture, $"{origin}.{organization}.{project}.{database}.{sequence:D20}");

    private static ulong Sequence(string id) => ulong.Parse(id.AsSpan(id.LastIndexOf('.') + 1), CultureInfo.InvariantCulture);

    private void Forget(long bytes, long count = 1)
    {
        lock (_totalsGate)
        {
            _count = Math.Max(0, _count - count);
            _bytes = Math.Max(0, _bytes - bytes);
        }
    }

    private async ValueTask CountAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _counted))
        {
            return;
        }

        await _counting.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_counted)
            {
                return;
            }

            var store = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
            long count = 0, bytes = 0;
            string? after = null;
            while (true)
            {
                await using var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
                var page = await transaction.ListAsync(Kind, after, 256, PageBytes, cancellationToken).ConfigureAwait(false);
                foreach (var record in page.Records)
                {
                    count++;
                    bytes += record.Json.Length;
                    after = record.Id;
                }

                if (!page.HasMore)
                {
                    break;
                }
            }

            lock (_totalsGate)
            {
                _count += count;
                _bytes += bytes;
            }

            Volatile.Write(ref _counted, true);
        }
        finally
        {
            _counting.Release();
        }
    }
}
