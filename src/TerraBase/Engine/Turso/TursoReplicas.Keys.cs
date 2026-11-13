using TerraBase.Features.Backup;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoReplicas : IWrappedKeyFiles
{
    Task<IReadOnlyDictionary<string, long>> IWrappedKeyFiles.RewrapAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, long> counts = new(StringComparer.Ordinal);
        lock (KeyRecords)
        {
            if (!Directory.Exists(Root))
            {
                return Task.FromResult<IReadOnlyDictionary<string, long>>(counts);
            }

            foreach (var file in ReplicaKeyFiles(Root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var wrapped = DataKeyRecord(file[..^DatabaseKeys.Suffix.Length])
                    ?? throw new InvalidDataException("the replica wrapped key record is missing");
                var id = WrappedKeyFile.Rewrap(file, KeyHeader, wrapped, master, cancellationToken);
                counts[id] = counts.GetValueOrDefault(id) + 1;
            }
        }
        return Task.FromResult<IReadOnlyDictionary<string, long>>(counts);
    }

    private static IEnumerable<string> ReplicaKeyFiles(string directory)
    {
        DatabaseKeys.RejectLink(directory);
        foreach (var file in Directory.EnumerateFiles(directory, "*" + DatabaseKeys.Suffix))
        {
            DatabaseKeys.RejectLink(file);
            yield return file;
        }
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            foreach (var file in ReplicaKeyFiles(child))
            {
                yield return file;
            }
        }
    }
}
