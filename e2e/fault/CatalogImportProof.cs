using System.Globalization;
using TerraBase.Cluster;
using TerraBase.Engine;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraBase.Storage;
using TerraScale.Contracts.Cluster;
using TerraScale.Contracts.ControlPlane;

internal static class CatalogImportProof
{
    private const string Foreign = "fra";

    public static async Task RunAsync(WebApplication app, CancellationToken cancellationToken)
    {
        await app.StartAsync(cancellationToken);
        try
        {
            await ProveAsync(app.Services, cancellationToken);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    private static async Task ProveAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var system = services.GetRequiredService<SystemDatabase>();
        var catalog = services.GetRequiredService<CatalogStore>();
        var replicator = services.GetRequiredService<DeltaReplicator>();
        var origin = services.GetRequiredService<ClusterTopology>().StorageOrigin;
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var session = system.Session;

        var organization = new OrganizationRecord(Ulid.NewUlid(now), "catalog-import", Ulid.NewUlid(now).ToString(), now, Plans.Hobby, false);
        var organizationCreated = await catalog.CreateOrganizationAsync(organization, cancellationToken);
        Require(organizationCreated switch
        {
            OrganizationRecord => null,
            StateConflict conflict => conflict.Code,
            BackendUnavailable unavailable => unavailable.Code,
        } is null, "the organization was not recorded");
        var project = new ProjectRecord(Ulid.NewUlid(now), organization.Id, "catalog-import", now, null);
        var projectCreated = await catalog.CreateProjectAsync(project, cancellationToken);
        Require(projectCreated switch
        {
            ProjectRecord => null,
            StateConflict conflict => conflict.Code,
            BackendUnavailable unavailable => unavailable.Code,
        } is null, "the project was not recorded");
        var database = new DatabaseRecord(Ulid.NewUlid(now), project.Id, "catalog-import", [origin], false, ConsistencyMode.Fast, false,
            ControlDatabaseState.Active, system.KeyId, null, null, now, null, 1, 0);

        var beforeCreate = Sequence(session.CurrentBookmark, origin);
        var databaseInserted = await catalog.InsertDatabaseAsync(database, cancellationToken);
        Require(databaseInserted switch
        {
            DatabaseRecord => null,
            StateConflict conflict => conflict.Code,
            BackendUnavailable unavailable => unavailable.Code,
        } is null, "the database was not recorded");
        var beforeDelete = Sequence(session.CurrentBookmark, origin);
        var databaseDeleted = await catalog.DeleteDatabaseAsync(database, now, cancellationToken);
        Require(databaseDeleted switch
        {
            DatabaseTombstoned => null,
            ResourceNotFound missing => missing.Code,
            StateConflict conflict => conflict.Code,
            BackendUnavailable unavailable => unavailable.Code,
        } is null, "the database deletion was not recorded");
        var databasePurged = await catalog.PurgeDatabaseAsync(database, now, cancellationToken);
        Require(databasePurged switch
        {
            TombstonePurged => null,
            ResourceNotFound missing => missing.Code,
            BackendUnavailable unavailable => unavailable.Code,
        } is null, "the database purge was not recorded");
        var afterDelete = Sequence(session.CurrentBookmark, origin);

        var create = await FindAsync(session, origin, beforeCreate + 1, beforeDelete, database.Id, MutationOp.Insert, cancellationToken);
        var delete = await FindAsync(session, origin, beforeDelete + 1, afterDelete, database.Id, MutationOp.Delete, cancellationToken);
        var schema = await SchemaCommitAsync(session, origin, cancellationToken);
        var deleted = await SnapshotAsync(system, project.Id, cancellationToken);
        Require(!deleted.Contains(database.Id.ToString(), StringComparison.Ordinal) && await AbsentAsync(catalog, project.Id, database, cancellationToken),
            "the deletion left catalog rows behind: " + deleted);

        Require(Describe(await session.ApplyRemoteBatchAsync(new RemoteCommitBatch(SystemDatabase.Id, [create, delete]), cancellationToken)) == "applied=0 duplicates=2 buffered=0 quarantined=0"
            && Describe(await replicator.ApplyAsync(SystemDatabase.Id, [create, delete], cancellationToken)) == "applied=0 duplicates=2 buffered=0 quarantined=0",
            "replaying the local catalog log was not a duplicate");
        await UnchangedAsync("identical");

        await ExpectAsync("foreign-delete", [delete with { OriginRegion = Foreign, Sequence = 1 }], "applied=1 duplicates=0 buffered=0 quarantined=0", 1);
        await ExpectAsync("stale-create", [create with { OriginRegion = Foreign, Sequence = 2 }], "applied=1 duplicates=0 buffered=0 quarantined=0", 2);
        await ExpectAsync("duplicate-transaction", [delete with { OriginRegion = Foreign, Sequence = 3 }], "applied=0 duplicates=1 buffered=0 quarantined=0", 3);
        var epoch = session.SchemaEpoch;
        await ExpectAsync("malformed", [delete with { OriginRegion = Foreign, Sequence = 4, DeltaPayload = "{"u8.ToArray(), SchemaEpoch = ulong.MaxValue }],
            "applied=0 duplicates=0 buffered=0 quarantined=1", 4);
        Require(session.SchemaEpoch == epoch, "a quarantined malformed commit moved the schema epoch");
        await ExpectAsync("invalid-origin", [delete with { OriginRegion = "FRA!", Sequence = 5 }], "storage_corrupted", 4);
        await ExpectAsync("stale-schema", [schema with { OriginRegion = Foreign, Sequence = 5 }], "applied=0 duplicates=0 buffered=0 quarantined=1", 5);
        Require(session.SchemaEpoch == epoch, "a quarantined stale schema commit moved the schema epoch");
        var ahead = Sequence(session.CurrentBookmark, origin) + 1000;
        Require(Describe(await session.ApplyRemoteBatchAsync(new RemoteCommitBatch(SystemDatabase.Id, [create with { Sequence = ahead }]), cancellationToken)) == "storage_corrupted",
            "a local-origin commit ahead of the local log was accepted");
        await UnchangedAsync("local-ahead");
        await ExpectAsync("gap", [create with { OriginRegion = Foreign, Sequence = 8 }], "applied=0 duplicates=0 buffered=1 quarantined=0", 5);
        await ExpectAsync("gap-filled", [delete with { OriginRegion = Foreign, Sequence = 6 }, delete with { OriginRegion = Foreign, Sequence = 7 }],
            "applied=0 duplicates=3 buffered=0 quarantined=0", 8);
        Console.WriteLine("CATALOG_IMPORT identical=duplicate foreign=applied stale_create=deleted quarantined=2 refused=2 gap=buffered unchanged=true");

        async Task ExpectAsync(string name, IReadOnlyList<CommitMetadata> commits, string expected, long foreignSequence)
        {
            var outcome = Describe(await replicator.ApplyAsync(SystemDatabase.Id, commits, cancellationToken));
            Require(outcome == expected, $"{name}: expected {expected}, got {outcome}");
            var observed = Sequence(session.CurrentBookmark, Foreign);
            Require(observed == foreignSequence, $"{name}: expected {Foreign} at {foreignSequence.ToString(CultureInfo.InvariantCulture)}, got {observed.ToString(CultureInfo.InvariantCulture)}");
            await UnchangedAsync(name);
        }

        async Task UnchangedAsync(string name)
        {
            var current = await SnapshotAsync(system, project.Id, cancellationToken);
            Require(current == deleted && await AbsentAsync(catalog, project.Id, database, cancellationToken), $"{name}: the catalog changed: {current}");
            Console.WriteLine($"CATALOG_CASE {name} passed");
        }
    }

    private static async Task<CommitMetadata> FindAsync(ISqlDatabaseSession session, string origin, long from, long to, Ulid id, MutationOp op, CancellationToken cancellationToken)
    {
        var key = id.ToString();
        foreach (var commit in await LogAsync(session, origin, from, to, cancellationToken))
        {
            if (MutationBatchCodec.TryDecode(commit.DeltaPayload.Span, out var batch)
                && batch.Mutations.Any(mutation => mutation.Table == "databases" && mutation.Op == op
                    && mutation.PrimaryKey.Any(value => value.TryGetText(out var text) && text == key)))
            {
                return commit;
            }
        }

        throw new InvalidOperationException($"the catalog log of {origin} holds no {op} of database {key} between {from.ToString(CultureInfo.InvariantCulture)} and {to.ToString(CultureInfo.InvariantCulture)}");
    }

    private static async Task<CommitMetadata> SchemaCommitAsync(ISqlDatabaseSession session, string origin, CancellationToken cancellationToken)
    {
        foreach (var commit in await LogAsync(session, origin, 1, Sequence(session.CurrentBookmark, origin), cancellationToken))
        {
            if (MutationBatchCodec.TryDecode(commit.DeltaPayload.Span, out var batch) && batch.Ddl.Count > 0)
            {
                return commit;
            }
        }

        throw new InvalidOperationException("the catalog log holds no schema commit");
    }

    private static async Task<IReadOnlyList<CommitMetadata>> LogAsync(ISqlDatabaseSession session, string origin, long from, long to, CancellationToken cancellationToken)
    {
        List<CommitMetadata> commits = [];
        var next = from;
        while (next <= to)
        {
            var logRead = await session.ReadCommitLogAsync(origin, next, 256, cancellationToken);
            var page = logRead switch
            {
                CommitLogPage found => found,
                CommitLogGap => null,
                StorageCorrupted => null,
                StorageBusy => null,
            };
            if (page is not { Entries.Count: > 0 } read)
            {
                throw new InvalidOperationException($"the catalog log of {origin} cannot be read from {next.ToString(CultureInfo.InvariantCulture)}");
            }

            commits.AddRange(read.Entries.Where(commit => commit.Sequence <= to));
            next = read.NextSequence;
        }

        return commits;
    }

    private static async Task<string> SnapshotAsync(SystemDatabase system, Ulid projectId, CancellationToken cancellationToken)
    {
        var queried = await system.QueryAsync(SqlBuild.Statement(
            "SELECT 'databases', id, name, state, version FROM databases WHERE project_id = ?1 UNION ALL SELECT 'database_names', database_id, name, '', 0 FROM database_names WHERE project_id = ?1 ORDER BY 1, 2",
            SqlBuild.Id(projectId)), cancellationToken);
        var rows = queried switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> found => found,
            BackendUnavailable => null,
        };
        if (rows is null)
        {
            throw new InvalidOperationException("the catalog cannot be read");
        }

        return string.Join(';', rows.Select(static row => string.Join('|', row.Select(SqlBuild.Format))));
    }

    private static async Task<bool> AbsentAsync(CatalogStore catalog, Ulid projectId, DatabaseRecord database, CancellationToken cancellationToken)
    {
        var byId = await catalog.GetDatabaseAsync(database.Id, cancellationToken);
        if (!Absent(byId))
        {
            return false;
        }

        var byName = await catalog.FindDatabaseAsync(projectId, database.Name, cancellationToken);
        return Absent(byName);
    }

    private static bool Absent(DatabaseLookup lookup) => lookup switch
    {
        DatabaseRecord => false,
        ResourceNotFound => true,
        BackendUnavailable => false,
    };

    private static long Sequence(CausalBookmark bookmark, string origin) =>
        bookmark.Entries.Where(entry => entry.Key == origin).Select(static entry => entry.Value).FirstOrDefault();

    private static string Describe(RemoteReplicationOutcome outcome) => outcome switch
    {
        RemoteApplyOutcome applied => Counts(applied),
        DatabaseNotFound => "not_found",
        SchemaMismatch => "schema_mismatch",
        StorageCorrupted => "storage_corrupted",
        StorageBusy => "storage_busy",
        ClusterUnavailable => "unavailable",
    };

    private static string Describe(ApplyRemoteBatchOutcome outcome) => outcome switch
    {
        RemoteApplyOutcome applied => Counts(applied),
        SchemaMismatch => "schema_mismatch",
        StorageCorrupted => "storage_corrupted",
        StorageBusy => "storage_busy",
    };

    private static string Counts(RemoteApplyOutcome applied) => string.Create(CultureInfo.InvariantCulture,
        $"applied={applied.Applied} duplicates={applied.Duplicates} buffered={applied.Buffered} quarantined={applied.Quarantined}");

    private static void Require(bool accepted, string message)
    {
        if (!accepted)
        {
            throw new InvalidOperationException(message);
        }
    }
}
