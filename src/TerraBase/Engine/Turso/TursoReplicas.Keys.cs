using TerraBase.Features.Backup;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoReplicas : IWrappedKeyFiles
{
    Task IWrappedKeyFiles.RewrapAsync(KeyInventory inventory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Root))
        {
            return Task.CompletedTask;
        }
        foreach (var file in ReplicaKeyFiles(Root, inventory).Where(file => !file.EndsWith(".promote.db.key", StringComparison.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (KeyRecords)
            {
                try
                {
                    DatabaseKeys.RejectLink(file);
                    if (!File.Exists(file))
                    {
                        continue;
                    }
                    var wrapped = DataKeyRecord(file[..^DatabaseKeys.Suffix.Length])
                        ?? throw new InvalidDataException("the replica wrapped key record is missing");
                    var header = master.ActiveId == "k0" ? PreviousKeyHeader : KeyHeader;
                    inventory.Count(WrappedKeyFile.Rewrap(file, header, wrapped, master, cancellationToken));
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    inventory.Unreadable(file);
                }
            }
        }
        return Task.CompletedTask;
    }

    private static IEnumerable<string> ReplicaKeyFiles(string directory, KeyInventory inventory)
    {
        string[] files;
        string[] children;
        try
        {
            DatabaseKeys.RejectLink(directory);
            files = Directory.GetFiles(directory, "*" + DatabaseKeys.Suffix);
            children = Directory.GetDirectories(directory);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            inventory.Unreadable(directory);
            yield break;
        }
        foreach (var file in files)
        {
            yield return file;
        }
        foreach (var child in children)
        {
            foreach (var file in ReplicaKeyFiles(child, inventory))
            {
                yield return file;
            }
        }
    }
}
