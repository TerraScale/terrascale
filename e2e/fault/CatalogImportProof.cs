using System.Globalization;
using TerraBase.Cluster;
using TerraBase.Engine;
using TerraBase.Features.Catalog;
using TerraBase.Storage;
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
        Require(await catalog.CreateOrganizationAsync(organization, cancellationToken) switch
        {
            OrganizationRecord => null,
            Conflict conflict => conflict.Code,
            Unavailable unavailable => unavailable.Code,
        } is null, "the organization was not recorded");
        var project = new ProjectRecord(Ulid.NewUlid(now), organization.Id, "catalog-import", now, null);
        Require(await catalog.CreateProjectAsync(project, cancellationToken) switch
        {
            ProjectRecord => null,
            Conflict conflict => conflict.Code,
            Unavailable unavailable => unavailable.Code,
        } is null, "the project was not recorded");
        var database = new DatabaseRecord(Ulid.NewUlid(now), project.Id, "catalog-import", [origin], false, ConsistencyMode.Fast, false,
            ControlDatabaseState.Active, system.KeyId, null, null, now, null, 1, 0);

        var beforeCreate = Sequence(session.CurrentBookmark, origin);
        Require(await catalog.InsertDatabaseAsync(database, cancellationToken) switch
        {
            DatabaseRecord => null,
            Conflict conflict => conflict.Code,
            Unavailable unavailable => unavailable.Code,
        } is null, "the database was not recorded");
        var beforeDelete = Sequence(session.CurrentBookmark, origin);
        Require(await catalog.DeleteDatabaseAsync(database, cancellationToken) switch
        {
            Done => null,
            Unavailable unavailable => unavailable.Code,
        } is null, "the database deletion was not recorded");
        var afterDelete = Sequence(session.CurrentBookmark, origin);

        var create = await FindAsync(session, origin, beforeCreate + 1, beforeDelete, database.Id, MutationOp.Insert, cancellationToken);
        var delete = await FindAsync(session, origin, beforeDelete + 1, afterDelete, database.Id, MutationOp.Delete, cancellationToken);
        var schema = await SchemaCommitAsync(session, origin, cancellationToken);
        var deleted = await SnapshotAsync(system, project.Id, cancellationToken);
        Require(!deleted.Contains(database.Id.ToString(), StringComparison.Ordinal) && await AbsentAsync(catalog, project.Id, database, cancellationToken),
            "the deletion left catalog rows behind: " + deleted);

        Require(Describe(await session.ApplyRemoteBatchAsync(new RemoteCommitBatch(SystemDatabase.Id, [create, delete]), cancellationToken)) == "applied=0 duplicates=2 buffered=0"
            && Describe(await replicator.ApplyAsync(SystemDatabase.Id, [create, delete], cancellationToken)) == "applied=0 duplicates=2 buffered=0",
            "replaying the local catalog log was not a duplicate");
        await UnchangedAsync("identical");

        await ExpectAsync("foreign-delete", [delete with { OriginRegion = Foreign, Sequence = 1 }], "applied=1 duplicates=0 buffered=0", 1);
        await ExpectAsync("stale-create", [create with { OriginRegion = Foreign, Sequence = 2 }], "applied=1 duplicates=0 buffered=0", 2);
        await ExpectAsync("duplicate-transaction", [delete with { OriginRegion = Foreign, Sequence = 3 }], "applied=0 duplicates=1 buffered=0", 3);
        await ExpectAsync("malformed", [delete with { OriginRegion = Foreign, Sequence = 4, DeltaPayload = "{"u8.ToArray() }], "storage_corrupted", 3);
        await ExpectAsync("invalid-origin", [delete with { OriginRegion = "FRA!", Sequence = 4 }], "storage_corrupted", 3);
        await ExpectAsync("stale-schema", [schema with { OriginRegion = Foreign, Sequence = 4 }], "schema_mismatch", 3);
        var ahead = Sequence(session.CurrentBookmark, origin) + 1000;
        Require(Describe(await session.ApplyRemoteBatchAsync(new RemoteCommitBatch(SystemDatabase.Id, [create with { Sequence = ahead }]), cancellationToken)) == "storage_corrupted",
            "a local-origin commit ahead of the local log was accepted");
        await UnchangedAsync("local-ahead");
        await ExpectAsync("gap", [create with { OriginRegion = Foreign, Sequence = 6 }], "applied=0 duplicates=0 buffered=1", 3);
        await ExpectAsync("gap-filled", [delete with { OriginRegion = Foreign, Sequence = 4 }, delete with { OriginRegion = Foreign, Sequence = 5 }],
            "applied=0 duplicates=3 buffered=0", 6);
        Console.WriteLine("CATALOG_IMPORT identical=duplicate foreign=applied stale_create=deleted refused=4 gap=buffered unchanged=true");

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
            var page = await session.ReadCommitLogAsync(origin, next, 256, cancellationToken) switch
            {
                CommitLogPage found => found,
                CommitLogGap => null,
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
        var rows = await system.QueryAsync(SqlBuild.Statement(
            "SELECT 'databases', id, name, state, version FROM databases WHERE project_id = ?1 UNION ALL SELECT 'database_names', database_id, name, '', 0 FROM database_names WHERE project_id = ?1 ORDER BY 1, 2",
            SqlBuild.Id(projectId)), cancellationToken) switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> found => found,
            Unavailable => null,
        };
        if (rows is null)
        {
            throw new InvalidOperationException("the catalog cannot be read");
        }

        return string.Join(';', rows.Select(static row => string.Join('|', row.Select(SqlBuild.Format))));
    }

    private static async Task<bool> AbsentAsync(CatalogStore catalog, Ulid projectId, DatabaseRecord database, CancellationToken cancellationToken) =>
        await catalog.GetDatabaseAsync(database.Id, cancellationToken) switch
        {
            DatabaseRecord => false,
            NotFound => true,
            Unavailable => false,
        }
        && await catalog.FindDatabaseAsync(projectId, database.Name, cancellationToken) switch
        {
            DatabaseRecord => false,
            NotFound => true,
            Unavailable => false,
        };

    private static long Sequence(CausalBookmark bookmark, string origin) =>
        bookmark.Entries.Where(entry => entry.Key == origin).Select(static entry => entry.Value).FirstOrDefault();

    private static string Describe(Result<RemoteApplyOutcome, NotFound, SchemaMismatch, StorageCorrupted, StorageBusy, Unavailable> outcome) => outcome switch
    {
        RemoteApplyOutcome applied => Counts(applied),
        NotFound => "not_found",
        SchemaMismatch => "schema_mismatch",
        StorageCorrupted => "storage_corrupted",
        StorageBusy => "storage_busy",
        Unavailable => "unavailable",
    };

    private static string Describe(Result<RemoteApplyOutcome, SchemaMismatch, StorageCorrupted, StorageBusy> outcome) => outcome switch
    {
        RemoteApplyOutcome applied => Counts(applied),
        SchemaMismatch => "schema_mismatch",
        StorageCorrupted => "storage_corrupted",
        StorageBusy => "storage_busy",
    };

    private static string Counts(RemoteApplyOutcome applied) => string.Create(CultureInfo.InvariantCulture,
        $"applied={applied.Applied} duplicates={applied.Duplicates} buffered={applied.Buffered}");

    private static void Require(bool accepted, string message)
    {
        if (!accepted)
        {
            throw new InvalidOperationException(message);
        }
    }
}
