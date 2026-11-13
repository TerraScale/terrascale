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
                WrappedKeyFile.RemoveLeftover(path);
                DatabaseKeys.RejectLink(path);
                if (!File.Exists(path))
                {
                    continue;
                }
                var id = await Io.RunAsync(() =>
                {
                    var wrapped = DatabaseKeys.ReadWrapped(path[..^DatabaseKeys.Suffix.Length]);
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

    private static IEnumerable<string> KeyFiles(string directory, KeyInventory inventory)
    {
        string[] files;
        string[] children;
        try
        {
            DatabaseKeys.RejectLink(directory);
            files = Directory.GetFiles(directory, "*" + DatabaseKeys.Suffix)
                .Concat(Directory.GetFiles(directory, "*" + DatabaseKeys.Suffix + ".rewrapping").Select(path => path[..^11]))
                .Distinct(StringComparer.Ordinal).ToArray();
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
            if (Path.GetFileName(child) != TursoReplicas.DirectoryName)
            {
                foreach (var file in KeyFiles(child, inventory))
                {
                    yield return file;
                }
            }
        }
    }
}
