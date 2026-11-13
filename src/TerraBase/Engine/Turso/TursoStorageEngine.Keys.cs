using TerraBase.Features.Backup;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageEngine : IWrappedKeyFiles
{
    async Task<IReadOnlyDictionary<string, long>> IWrappedKeyFiles.RewrapAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, long> counts = new(StringComparer.Ordinal);
        await EnterCreationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var path in KeyFiles(_dataDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var wrapped = await Io.RunAsync(() => DatabaseKeys.ReadWrapped(path[..^DatabaseKeys.Suffix.Length]), cancellationToken).ConfigureAwait(false);
                var id = await Io.RunAsync(() => WrappedKeyFile.Rewrap(path, DatabaseKeys.Header, wrapped, Encryption.Keyring, cancellationToken), cancellationToken).ConfigureAwait(false);
                counts[id] = counts.GetValueOrDefault(id) + 1;
            }
        }
        finally
        {
            ExitCreation();
        }
        return counts;
    }

    private static IEnumerable<string> KeyFiles(string directory)
    {
        DatabaseKeys.RejectLink(directory);
        foreach (var file in Directory.EnumerateFiles(directory, "*" + DatabaseKeys.Suffix))
        {
            DatabaseKeys.RejectLink(file);
            yield return file;
        }
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (Path.GetFileName(child) != TursoReplicas.DirectoryName)
            {
                foreach (var file in KeyFiles(child))
                {
                    yield return file;
                }
            }
        }
    }
}
