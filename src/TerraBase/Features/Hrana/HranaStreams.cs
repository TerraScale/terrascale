using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using TerraBase.Features.Sql;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Hrana;

/// <summary>
/// The node's open Hrana streams. A request takes its stream out by baton, owns it alone, then parks it under a new baton
/// or closes it. Parked streams idle out after <see cref="HranaStreams.IdleTimeout"/>; closing disposes the SQL session, which rolls
/// back an open transaction.
/// </summary>
internal interface IHranaStreams
{
    /// <summary>
    /// Opens a stream, or refuses past <see cref="HranaStreams.MaxStreams"/> per node,
    /// <see cref="HranaStreams.MaxOrganizationStreams"/> per organization or <see cref="HranaStreams.MaxDatabaseStreams"/> per database.
    /// </summary>
    HranaOpen Open(TenantContext database, string owner, bool canWrite);

    /// <summary>Takes the stream parked under <paramref name="baton"/> for this owner and database; null when unknown, expired or someone else's.</summary>
    HranaStream? Take(string baton, string owner, TenantContext database);

    /// <summary>Parks a taken stream and returns its new baton.</summary>
    string Park(HranaStream stream);

    /// <summary>Closes a taken stream.</summary>
    ValueTask CloseAsync(HranaStream stream);
}

/// <summary>A new stream, or why none opens.</summary>
internal readonly union HranaOpen(HranaStream, HranaError);

/// <inheritdoc cref="IHranaStreams"/>
internal sealed class HranaStreams : IHranaStreams, IAsyncDisposable
{
    /// <summary>Open streams per node, parked or in a request.</summary>
    public const int MaxStreams = 1024;

    /// <summary>Open streams per database, so one tenant cannot hold the node's streams.</summary>
    public const int MaxDatabaseStreams = 64;

    /// <summary>Open streams per organization, across its databases.</summary>
    public const int MaxOrganizationStreams = 256;

    /// <summary>How long a parked stream lives without a request.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, HranaStream> _parked = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _perDatabase = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _perOrganization = new(StringComparer.Ordinal);
    private readonly ISqlService _sql;
    private readonly TimeProvider _time;
    private readonly ILogger<HranaStreams> _logger;
    private readonly ITimer _sweep;
    private int _open;

    public HranaStreams(ISqlService sql, TimeProvider time, ILogger<HranaStreams> logger)
    {
        _sql = sql;
        _time = time;
        _logger = logger;
        _sweep = time.CreateTimer(_ => Sweep(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public HranaOpen Open(TenantContext database, string owner, bool canWrite)
    {
        if (Interlocked.Increment(ref _open) > MaxStreams)
        {
            Interlocked.Decrement(ref _open);
            return new HranaError("this node has too many open streams; retry later", "STREAMS_EXHAUSTED");
        }

        if (!Enter(_perOrganization, database.OrganizationId, MaxOrganizationStreams))
        {
            Interlocked.Decrement(ref _open);
            return new HranaError($"an organization has at most {MaxOrganizationStreams} open streams on a node; close streams or retry later", "ORGANIZATION_STREAMS_EXHAUSTED");
        }

        if (!Enter(_perDatabase, Key(database), MaxDatabaseStreams))
        {
            Leave(_perOrganization, database.OrganizationId);
            Interlocked.Decrement(ref _open);
            return new HranaError($"a database has at most {MaxDatabaseStreams} open streams on a node; close streams or retry later", "DATABASE_STREAMS_EXHAUSTED");
        }

        return new HranaStream(_sql.Open(database, canWrite), owner, database);
    }

    public HranaStream? Take(string baton, string owner, TenantContext database)
    {
        if (!_parked.TryGetValue(baton, out var stream)
            || !string.Equals(stream.Owner, owner, StringComparison.Ordinal)
            || !string.Equals(stream.Database.DatabaseId, database.DatabaseId, StringComparison.Ordinal)
            || !_parked.TryRemove(new KeyValuePair<string, HranaStream>(baton, stream)))
        {
            return null;
        }

        if (_time.GetElapsedTime(stream.ParkedAt) <= IdleTimeout)
        {
            return stream;
        }

        _ = CloseIdleAsync(stream);
        return null;
    }

    public string Park(HranaStream stream)
    {
        // The database identity survives stream expiry/restart; the suffix records whether losing state is safe.
        var baton = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
            + "." + stream.Database.DatabaseId + (stream.IsStateless ? StatelessSuffix : ".t");
        stream.ParkedAt = _time.GetTimestamp();
        _parked[baton] = stream;
        return baton;
    }

    /// <summary>Ends batons of streams that held no transaction and no stored statements when they were parked.</summary>
    public const string StatelessSuffix = ".a";

    /// <summary>Checks the opaque envelope before either taking a stream or recovering a stateless one.</summary>
    public static bool IsForDatabase(string baton, string? databaseId) =>
        baton.Length == 51 && databaseId is { Length: 26 } && baton[22] == '.' && baton[49] == '.'
        && baton[50] is 'a' or 't' && baton.AsSpan(23, 26).SequenceEqual(databaseId)
        && Base64Url.IsValid(baton.AsSpan(0, 22), out var length) && length == 16;

    public async ValueTask CloseAsync(HranaStream stream)
    {
        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _open);
            Leave(_perDatabase, Key(stream.Database));
            Leave(_perOrganization, stream.Database.OrganizationId);
        }
    }

    private static string Key(TenantContext database) => database.DatabaseId ?? string.Empty;

    private static bool Enter(ConcurrentDictionary<string, int> counts, string key, int max)
    {
        if (counts.AddOrUpdate(key, 1, (_, count) => count + 1) <= max)
        {
            return true;
        }

        Leave(counts, key);
        return false;
    }

    private static void Leave(ConcurrentDictionary<string, int> counts, string key)
    {
        if (counts.AddOrUpdate(key, 0, (_, count) => count - 1) <= 0)
        {
            counts.TryRemove(new KeyValuePair<string, int>(key, 0));
        }
    }

    // Expiry closes outside any request; a failure is logged, never left as an unobserved task.
    private async Task CloseIdleAsync(HranaStream stream)
    {
        try
        {
            await CloseAsync(stream).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _logger.ZLogWarning(error, $"Closing an idle Hrana stream of database {stream.Database.DatabaseId} failed");
        }
    }

    private void Sweep()
    {
        foreach (var (baton, stream) in _parked)
        {
            if (_time.GetElapsedTime(stream.ParkedAt) > IdleTimeout && _parked.TryRemove(new KeyValuePair<string, HranaStream>(baton, stream)))
            {
                _ = CloseIdleAsync(stream);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _sweep.DisposeAsync().ConfigureAwait(false);
        foreach (var baton in _parked.Keys)
        {
            if (_parked.TryRemove(baton, out var stream))
            {
                await CloseIdleAsync(stream).ConfigureAwait(false);
            }
        }
    }
}
