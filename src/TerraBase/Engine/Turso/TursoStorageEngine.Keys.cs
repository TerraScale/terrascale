using System.Collections.Concurrent;
using TerraBase.Features.Backup;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageEngine : IWrappedKeyFiles
{
    private readonly ConcurrentDictionary<string, byte> _detachedRefusals = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _detachedRefusalScopes = new(StringComparer.Ordinal);
    private long _detachedRecoveryFailures;

    internal void RefuseDetachedScope(string relative)
    {
        _detachedRefusalScopes.TryAdd(Path.GetFullPath(Path.Combine(_dataDirectory, relative)) + Path.DirectorySeparatorChar, 0);
        Interlocked.Increment(ref _detachedRecoveryFailures);
    }

    internal void RefuseDetached(string relative)
    {
        _detachedRefusals.TryAdd(Path.Combine(_dataDirectory, relative), 0);
        Interlocked.Increment(ref _detachedRecoveryFailures);
    }

    internal void ThrowIfDetachedRefused(string path)
    {
        if (_keyRefusals.TryGetValue(path, out var id))
        {
            throw new InvalidDataException($"database '{Path.GetFileNameWithoutExtension(path)}' is refused under key id '{id}'");
        }
        if (_detachedRefusals.ContainsKey(path) || _detachedRefusalScopes.Keys.Any(scope => Path.GetFullPath(path).StartsWith(scope, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("database '" + Path.GetFileNameWithoutExtension(path) + "' has an unresolved detached import");
        }
    }

    async Task IWrappedKeyFiles.RewrapAsync(KeyInventory inventory, CancellationToken cancellationToken)
    {
        foreach (var path in KeyFiles(_dataDirectory, inventory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await EnterCreationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                DatabaseKeys.RejectLink(path);
                if (!File.Exists(path))
                {
                    continue;
                }
                var id = await Io.RunAsync(() =>
                {
                    var wrapped = WrappedKeyFile.Read(path);
                    var header = Encryption.Keyring.ActiveId == "k0" ? DatabaseKeys.PreviousHeader : DatabaseKeys.Header;
                    return WrappedKeyFile.Rewrap(path, header, wrapped, Encryption.Keyring, cancellationToken);
                }, cancellationToken).ConfigureAwait(false);
                inventory.Count(id);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                inventory.Unreadable(path);
            }
            finally
            {
                ExitCreation();
            }
        }
    }

    private static IEnumerable<string> KeyFiles(string directory, KeyInventory inventory) =>
        WrappedKeyFile.Paths(directory).Where(path => !Path.GetRelativePath(directory, path).StartsWith(TursoReplicas.DirectoryName + Path.DirectorySeparatorChar, StringComparison.Ordinal));
}
