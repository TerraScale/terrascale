using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Clock;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Sql.Routing;

internal enum WriteOverride
{
    Auto,
    Home,
    Local,
}

internal enum WritePath
{
    Local,
    Home,
}

internal sealed record WriteRoute(WritePath Decided, string Reason, WritePath Commit)
{
    public string Header => $"route={Name(Decided)}; reason={Reason}; commit={Name(Commit)}";

    public static string Name(WritePath path) => path == WritePath.Local ? "local" : "home";
}

internal static class WriteReasons
{
    public const string UlidInsert = "ulid_insert";
    public const string ForeignKeyChild = "fk_child_insert";
    public const string UpdateByKey = "update_by_key";
    public const string DeleteByKey = "delete_by_key";
    public const string Read = "read";
    public const string Ddl = "ddl";
    public const string Autoincrement = "autoincrement";
    public const string RowidKey = "rowid_key";
    public const string Unique = "unique";
    public const string UniqueChange = "unique_change";
    public const string ForeignKeyChange = "fk_change";
    public const string ParentDelete = "fk_parent_delete";
    public const string Relative = "relative_update";
    public const string NotByKey = "not_by_key";
    public const string Complex = "complex";
    public const string Trigger = "trigger";
    public const string ReadThenWrite = "read_then_write";
    public const string Remembered = "remembered";
    public const string Override = "override";
    public const string Unknown = "unknown";
}

internal readonly record struct StatementRoute(WritePath Path, string Reason);

public interface ILocalCommitPath
{
    bool Ready(TenantContext database);

    /// <summary>
    /// Whether the discovered regional quorum can acknowledge a local commit: one node when the region has no partner,
    /// otherwise two healthy nodes, with a connected push stream and an admitted clock. Without it, only
    /// <c>durability=home</c> writes commit locally; the others go home.
    /// </summary>
    bool LocalAckAvailable { get; }
}

internal sealed class WriteRouter(
    SchemaFactsCache schemas, IMeterFactory meters, TerraBaseOptions options, IEnumerable<ILocalWriteClock> clocks,
    IEnumerable<ILocalCommitPath> paths)
{
    private readonly ILocalWriteClock? _clock = clocks.LastOrDefault();
    private readonly ILocalCommitPath? _path = paths.LastOrDefault();
    private const int MaxFingerprints = 65_536;
    private readonly ConcurrentDictionary<string, byte> _fingerprints = new(StringComparer.Ordinal);
    private readonly Counter<long> _routes = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.sql.write_routes", "{transaction}",
        "Writes of replica regions by the router's decision (route, reason) and where they committed (commit).");

    private bool LocalCommitAvailable(TenantContext database, bool homeDurable) =>
        _clock?.Allowed == true && _path?.Ready(database) == true && (homeDurable || _path.LocalAckAvailable);

    public async ValueTask<WriteRoute> RouteAsync(
        TenantContext database, IReplicaReader? reader, IReadOnlyList<string> statements, WriteOverride preference, bool homeDurable,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(statements);
        var decided = preference == WriteOverride.Home ? new StatementRoute(WritePath.Home, WriteReasons.Override)
            : await DecideAsync(database, reader, statements, preference, cancellationToken).ConfigureAwait(false);
        return Record(decided, decided.Path == WritePath.Local && LocalCommitAvailable(database, homeDurable));
    }

    public WriteRoute Record(StatementRoute decided, bool local = false)
    {
        var route = new WriteRoute(decided.Path, decided.Reason, local ? WritePath.Local : WritePath.Home);
        _routes.Add(1, new KeyValuePair<string, object?>("route", WriteRoute.Name(route.Decided)), new KeyValuePair<string, object?>("reason", route.Reason),
            new KeyValuePair<string, object?>("commit", WriteRoute.Name(route.Commit)), new KeyValuePair<string, object?>("region", options.Region));
        return route;
    }

    public void Remember(TenantContext database, string sql)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (_fingerprints.Count >= MaxFingerprints)
        {
            _fingerprints.Clear();
        }

        _fingerprints[Fingerprint(database, sql)] = 0;
    }

    public bool Remembers(TenantContext database, string sql)
    {
        ArgumentNullException.ThrowIfNull(database);
        return _fingerprints.ContainsKey(Fingerprint(database, sql));
    }

    private async ValueTask<StatementRoute> DecideAsync(
        TenantContext database, IReplicaReader? reader, IReadOnlyList<string> statements, WriteOverride preference, CancellationToken cancellationToken)
    {
        if (reader is null)
        {
            return new(WritePath.Home, WriteReasons.Unknown);
        }

        SchemaFacts? schema;
        try
        {
            schema = await schemas.ReadAsync(database, reader, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            schema = null;
        }

        if (schema is null)
        {
            return new(WritePath.Home, WriteReasons.Unknown);
        }

        var read = false;
        StatementRoute? local = null;
        foreach (var sql in statements)
        {
            if (SqlRouteParser.Parse(sql) is not [var facts])
            {
                return new(WritePath.Home, WriteReasons.Unknown);
            }

            if (facts.Kind == "select" && !facts.Explain)
            {
                read = true;
                continue;
            }

            var route = Classify(facts, schema);
            if (route.Path == WritePath.Home && preference == WriteOverride.Local && route.Reason is not (WriteReasons.Ddl or WriteReasons.Unknown))
            {
                route = new(WritePath.Local, WriteReasons.Override);
            }

            if (route.Path == WritePath.Home)
            {
                return route;
            }

            if (read)
            {
                return new(WritePath.Home, WriteReasons.ReadThenWrite);
            }

            local ??= route;
        }

        return local ?? new(WritePath.Local, WriteReasons.Read);
    }

    internal static StatementRoute Classify(SqlRouteFacts facts, SchemaFacts schema)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(schema);
        StatementRoute Home(string reason) => new(WritePath.Home, reason);
        StatementRoute Local(string reason) => new(WritePath.Local, reason);
        if (facts.Ddl)
        {
            return Home(WriteReasons.Ddl);
        }

        if (facts.Flags.Unknown || facts.Explain || facts.Kind is not ("insert" or "update" or "delete") || facts.Tables is not [{ Schema: null or "main" } target]
            || !facts.Flags.TablesComplete)
        {
            return Home(WriteReasons.Unknown);
        }

        if (facts.Flags.With || facts.Flags.InsertSelect || facts.Flags.UpdateFrom)
        {
            return Home(WriteReasons.Complex);
        }

        if (!schema.Tables.TryGetValue(target.Name, out var table))
        {
            return Home(WriteReasons.Unknown);
        }

        if (table.Triggers)
        {
            return Home(WriteReasons.Trigger);
        }

        if (facts.Flags.Upsert || facts.Flags.Replace || facts.Flags.ConflictClause)
        {
            return Home(WriteReasons.Unique);
        }

        var touched = facts.ColumnsTouched.ToHashSet(StringComparer.OrdinalIgnoreCase);
        switch (facts.Kind)
        {
            case "insert":
                if (table.Autoincrement)
                {
                    return Home(WriteReasons.Autoincrement);
                }

                if (table.Rowid || !facts.Flags.ColumnsComplete || !table.Key.IsSubsetOf(touched))
                {
                    return Home(WriteReasons.RowidKey);
                }

                if (table.Unique.Count > 0 || table.UniqueExpression)
                {
                    return Home(WriteReasons.Unique);
                }

                if (facts.AssignmentDependence != "row_independent")
                {
                    return Home(WriteReasons.Complex);
                }

                return touched.Overlaps(table.ForeignKeyColumns) ? Local(WriteReasons.ForeignKeyChild) : Local(WriteReasons.UlidInsert);
            case "update":
                if (!ByKey(facts, table))
                {
                    return Home(WriteReasons.NotByKey);
                }

                if (facts.AssignmentDependence != "row_independent")
                {
                    return Home(WriteReasons.Relative);
                }

                if (table.UniqueExpression || touched.Overlaps(table.Key) || table.Unique.Any(touched.Overlaps))
                {
                    return Home(WriteReasons.UniqueChange);
                }

                return touched.Overlaps(table.ForeignKeyColumns) || touched.Overlaps(table.ReferencedColumns)
                    ? Home(WriteReasons.ForeignKeyChange)
                    : Local(WriteReasons.UpdateByKey);
            case "delete":
                if (table.Referenced)
                {
                    return Home(WriteReasons.ParentDelete);
                }

                return ByKey(facts, table) ? Local(WriteReasons.DeleteByKey) : Home(WriteReasons.NotByKey);
        }

        return Home(WriteReasons.Unknown);
    }

    private static bool ByKey(SqlRouteFacts facts, TableFacts table)
    {
        if (facts.Where.Shape is not ("equality" or "and_equalities") || table.Key.Count == 0)
        {
            return false;
        }

        var columns = facts.Where.EqualityColumns.Select(column => column.Name).ToList();
        return columns.Count == table.Key.Count && columns.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(table.Key);
    }

    private static string Fingerprint(TenantContext database, string sql)
    {
        var shape = new StringBuilder(database.DatabaseId).Append('\u001f');
        if (SqlText.TryTokenize(sql, out var tokens))
        {
            foreach (var token in tokens)
            {
                shape.Append(token.Kind is SqlTokenKind.String or SqlTokenKind.Number or SqlTokenKind.Blob ? "?" : token.Value.ToLowerInvariant()).Append(' ');
            }
        }
        else
        {
            shape.Append(sql);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(shape.ToString())));
    }
}
