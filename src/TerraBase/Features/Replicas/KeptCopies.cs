using System.Diagnostics;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Revisions;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Replicas;

internal enum KeepReason
{
    MoveRolledBack,
    MoveUnproven,
    LeaseUnproven,
    MoveTarget,
}

internal sealed record KeptCopy(string Id, string OrganizationId, string ProjectId, string DatabaseId, IReadOnlyList<string> Files, long Bytes);

internal sealed class KeptCopies(TerraBaseOptions options, IStorageEngine engine, IWalBackup storage, IDatabaseReplicas replicas, IDatabaseRevisions revisions, ILogger<KeptCopies> logger)
{
    private readonly string _root = Path.Combine(options.DataDirectory, PreservedFiles.Directory);

    private static string Wire(KeepReason reason) => reason switch
    {
        KeepReason.MoveRolledBack => "move_rolled_back",
        KeepReason.MoveUnproven => "move_unproven",
        KeepReason.LeaseUnproven => "lease_unproven",
        KeepReason.MoveTarget => "move_target",
        _ => throw new UnreachableException(),
    };

    internal async Task<bool> KeepAsync(TenantContext database, KeepReason reason, CancellationToken cancellationToken)
    {
        if (!engine.HasDatabaseFiles(database, cancellationToken))
        {
            return false;
        }

        var revision = await revisions.CurrentRevisionAsync(database, cancellationToken).ConfigureAwait(false);
        replicas.ArchiveCompletedImport(database);
        await storage.PreserveDatabaseAsync(database, CancellationToken.None).ConfigureAwait(false);
        logger.ZLogWarning($"Preserved a database copy this node does not serve: database {database.DatabaseId:@Database} at {revision?.ToString() ?? "an unknown position":@Revision}, reason {Wire(reason):@Reason}; it stays in the retained inventory and nothing automatic deletes it");
        return true;
    }

    internal IReadOnlyList<KeptCopy> List()
    {
        if (!Directory.Exists(_root))
        {
            return [];
        }

        List<KeptCopy> copies = [];
        foreach (var preserved in Directory.EnumerateDirectories(_root).Order(StringComparer.Ordinal))
        {
            foreach (var project in Directory.EnumerateDirectories(preserved).SelectMany(Directory.EnumerateDirectories).Order(StringComparer.Ordinal))
            {
                foreach (var files in Directory.EnumerateFiles(project).Order(StringComparer.Ordinal).GroupBy(DatabaseOf, StringComparer.Ordinal))
                {
                    copies.Add(new KeptCopy(Path.GetFileName(preserved), Path.GetFileName(Path.GetDirectoryName(project)!), Path.GetFileName(project), files.Key,
                        [.. files.Select(Path.GetFileName).OfType<string>()], files.Sum(file => new FileInfo(file).Length)));
                }
            }
        }

        return copies;
    }

    private static string DatabaseOf(string file)
    {
        var name = Path.GetFileName(file);
        var end = name.IndexOf(".db", StringComparison.Ordinal);
        return end > 0 ? name[..end] : name;
    }
}
