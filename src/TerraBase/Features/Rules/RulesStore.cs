using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraBase.Cluster;
using TerraBase.Engine;
using TerraBase.Hosting;

namespace TerraBase.Features.Rules;

internal readonly union StoredRulesLookup(StoredRules, ResourceNotFound, BackendUnavailable);

internal readonly union RulesVersionsOutcome(RulesVersionsBody, BackendUnavailable);

internal readonly union RulesSaveOutcome(StoredRules, StaleRules, BackendUnavailable);

internal sealed class RulesStore(SystemDatabase system, AntiEntropyService antiEntropy, TimeProvider time)
{
    private const int MaxCacheEntries = 4096;

    private static readonly TimeSpan SyncTimeout = TimeSpan.FromMilliseconds(500);

    private static readonly ResourceNotFound MissingRules = new("the database has no rules; end users are denied", "no_rules");

    private readonly ConcurrentDictionary<Ulid, (StoredRules? Rules, long Generation)> _cache = new();

    private int _cacheCount;

    public ValueTask<StoredRulesLookup> CurrentAsync(Ulid databaseId, CancellationToken cancellationToken) =>
        CurrentAsync(databaseId, fresh: false, cancellationToken);

    public ValueTask<StoredRulesLookup> CurrentAsync(Ulid databaseId, bool fresh, CancellationToken cancellationToken)
    {
        if (!system.IsOpen)
        {
            return ValueTask.FromResult<StoredRulesLookup>(SystemDatabase.Opening);
        }

        if (!fresh)
        {
            var generation = system.CatalogGeneration;
            if (_cache.TryGetValue(databaseId, out var cached) && cached.Generation == generation)
            {
                return ValueTask.FromResult<StoredRulesLookup>(cached.Rules is { } rules ? rules : MissingRules);
            }

            return CurrentSlowAsync(databaseId, fresh: false, generation, cancellationToken);
        }

        return CurrentSlowAsync(databaseId, fresh: true, 0, cancellationToken);
    }

    private async ValueTask<StoredRulesLookup> CurrentSlowAsync(Ulid databaseId, bool fresh, long generation, CancellationToken cancellationToken)
    {
        if (fresh)
        {
            await SyncSystemAsync(cancellationToken);
            generation = system.CatalogGeneration;
        }

        var current = await system.QueryAsync(SqlBuild.Statement(
            "SELECT v.version, v.body, v.updated_at, v.updated_by, v.digest FROM rules_current c JOIN rules_versions v ON v.database_id = c.database_id AND v.version = c.version WHERE c.database_id = ?",
            SqlBuild.Id(databaseId)), cancellationToken);
        return current switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> { Count: > 0 } rows when Read(rows[0]) is { } rules => Cache(databaseId, rules, generation),
            IReadOnlyList<IReadOnlyList<SqlValue>> => CacheMissing(databaseId, generation),
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<StoredRulesLookup> VersionAsync(Ulid databaseId, string? version, CancellationToken cancellationToken)
    {
        await SyncSystemAsync(cancellationToken);
        var found = await system.QueryAsync(SqlBuild.Statement(
            "SELECT version, body, updated_at, updated_by, digest FROM rules_versions WHERE database_id = ? AND version = ?", SqlBuild.Id(databaseId), SqlBuild.OptionalText(version)),
            cancellationToken);
        return found switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> { Count: > 0 } rows when Read(rows[0]) is { } rules => rules,
            IReadOnlyList<IReadOnlyList<SqlValue>> => new ResourceNotFound("the database has no such rules version", "unknown_version"),
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<RulesVersionsOutcome> VersionsAsync(Ulid databaseId, string? after, int limit, CancellationToken cancellationToken)
    {
        await SyncSystemAsync(cancellationToken);
        var listed = await system.QueryAsync(SqlBuild.Statement(
            "SELECT version, updated_at, updated_by, digest FROM rules_versions WHERE database_id = ?1 AND (?2 IS NULL OR version < ?2) ORDER BY version DESC LIMIT ?3",
            SqlBuild.Id(databaseId), SqlBuild.OptionalText(after), SqlBuild.Integer(limit + 1)), cancellationToken);
        return listed switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> rows => Versions(rows, limit),
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<RulesSaveOutcome> DeployAsync(
        Ulid databaseId, RulesSet rules, string? ifMatch, string actor, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var body = JsonSerializer.Serialize(rules, RulesJsonContext.Default.RulesSet);
        var stored = new StoredRules(Ulid.NewUlid(now).ToString(), rules, now, actor, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))));
        var insertVersion = ifMatch is { } matchExpected
            ? SqlBuild.Statement(
                "INSERT INTO rules_versions (database_id, version, body, updated_at, updated_by, digest) SELECT ?, ?, ?, ?, ?, ? "
                + "WHERE EXISTS (SELECT 1 FROM rules_current WHERE database_id = ? AND version = ?)",
                SqlBuild.Id(databaseId), SqlBuild.Text(stored.Version), SqlBuild.Text(body), SqlBuild.Time(now), SqlBuild.Text(actor), SqlBuild.Text(stored.Digest),
                SqlBuild.Id(databaseId), SqlBuild.Text(matchExpected))
            : SqlBuild.Statement(
                "INSERT INTO rules_versions (database_id, version, body, updated_at, updated_by, digest) SELECT ?, ?, ?, ?, ?, ? "
                + "WHERE NOT EXISTS (SELECT 1 FROM rules_current WHERE database_id = ?)",
                SqlBuild.Id(databaseId), SqlBuild.Text(stored.Version), SqlBuild.Text(body), SqlBuild.Time(now), SqlBuild.Text(actor), SqlBuild.Text(stored.Digest),
                SqlBuild.Id(databaseId));
        var pointer = ifMatch is { } expected
            ? SqlBuild.Statement("UPDATE rules_current SET version = ? WHERE database_id = ? AND version = ?", SqlBuild.Text(stored.Version), SqlBuild.Id(databaseId), SqlBuild.Text(expected))
            : SqlBuild.Statement("INSERT INTO rules_current (database_id, version) VALUES (?, ?)", SqlBuild.Id(databaseId), SqlBuild.Text(stored.Version));
        await SyncSystemAsync(cancellationToken);
        var written = await system.WriteAsync([insertVersion, pointer], ConsistencyMode.Strong, cancellationToken);
        if (_cache.TryRemove(databaseId, out _))
        {
            Interlocked.Decrement(ref _cacheCount);
        }

        return written switch
        {
            SqlWriteResult { ResultSets: [_, { RowsAffected: > 0 }] } => stored,
            SqlWriteResult or StateConflict => new StaleRules(await CurrentVersionAsync(databaseId, cancellationToken)),
            BackendUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<string?> CurrentVersionAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        var outcome = await CurrentAsync(databaseId, fresh: true, cancellationToken);
        return outcome switch
        {
            StoredRules rules => rules.Version,
            ResourceNotFound or BackendUnavailable => null,
        };
    }

    private async ValueTask SyncSystemAsync(CancellationToken cancellationToken) =>
        _ = await antiEntropy.SyncInRegionAsync(SystemDatabase.Id, SyncTimeout, cancellationToken);

    private StoredRulesLookup Cache(Ulid databaseId, StoredRules rules, long generation)
    {
        if (generation > 0 && system.CatalogGeneration == generation)
        {
            Store(databaseId, (rules, generation));
        }

        return rules;
    }

    private StoredRulesLookup CacheMissing(Ulid databaseId, long generation)
    {
        if (generation > 0 && system.CatalogGeneration == generation)
        {
            Store(databaseId, (null, generation));
        }

        return MissingRules;
    }

    private void Store(Ulid databaseId, (StoredRules? Rules, long Generation) entry)
    {
        if (!_cache.ContainsKey(databaseId) && Volatile.Read(ref _cacheCount) >= MaxCacheEntries)
        {
            _cache.Clear();
            Volatile.Write(ref _cacheCount, 0);
        }

        if (_cache.TryAdd(databaseId, entry))
        {
            Interlocked.Increment(ref _cacheCount);
        }
        else
        {
            _cache[databaseId] = entry;
        }
    }

    private static StoredRules? Read(IReadOnlyList<SqlValue> row)
    {
        try
        {
            return JsonSerializer.Deserialize(row.TextAt(1), RulesJsonContext.Default.RulesSet) is { Tables: not null } rules
                ? new StoredRules(row.TextAt(0), rules, row.TimeAt(2), row.TextAt(3), row.TextAt(4))
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static RulesVersionsBody Versions(IReadOnlyList<IReadOnlyList<SqlValue>> rows, int limit)
    {
        var count = Math.Min(rows.Count, limit);
        if (count == 0)
        {
            return new RulesVersionsBody([], null);
        }

        var items = new RulesVersionItem[count];
        for (var i = 0; i < count; i++)
        {
            var row = rows[i];
            items[i] = new RulesVersionItem(row.TextAt(0), row.TimeAt(1), row.TextAt(2), row.TextAt(3));
        }

        return new RulesVersionsBody(items, rows.Count > limit ? items[^1].Version : null);
    }
}
