using TerraBase.Primitives;
using ZLogger;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageEngine
{
    private void SyncPublicationDirectory(string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_dataDirectory));
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        while (true)
        {
            DirectorySync(current);
            if (current == root)
            {
                DirectorySync(Path.GetDirectoryName(root)!);
                return;
            }

            current = Path.GetDirectoryName(current)!;
        }
    }

    private void SyncExistingDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            DirectorySync(directory);
        }
    }

    private void RecoverPublications()
    {
        if (Directory.EnumerateDirectories(_dataDirectory).Any(path => IsId(Path.GetFileName(path)))
            && (!File.Exists(Path.Combine(_dataDirectory, SystemName + FileExtension))
                || !File.Exists(Path.Combine(_dataDirectory, IntentFile))))
        {
            throw new IOException("a required catalog or intent store is missing its main file while the data directory contains organizations");
        }

        RecoverDirectory(_dataDirectory);
        foreach (var organization in Directory.EnumerateDirectories(_dataDirectory).Where(path => IsId(Path.GetFileName(path))))
        {
            RejectLink(organization);
            foreach (var project in Directory.EnumerateDirectories(organization).Where(path => IsId(Path.GetFileName(path))))
            {
                RejectLink(project);
                RecoverDirectory(project);
            }
        }
    }

    private const string RetainedKeySuffix = "retained-key";

    private long _retainedKeys;

    private readonly List<(StorageFaultLedger.Key Key, string Path)> _deletions = [];

    private bool CompleteDeletions()
    {
        foreach (var (key, path) in _deletions)
        {
            DeleteFileIfPresent(path + DeletionMarker);
            SyncExistingDirectory(Path.GetDirectoryName(path)!);
            Ledger.DeletionFinished(key);
        }
        _deletions.Clear();
        return true;
    }

    private static bool OrphanKey(string directory, string name, string path) =>
        IsId(name[..^FileExtension.Length]) && File.Exists(path + DatabaseKeys.Suffix)
            && !File.Exists(path) && !File.Exists(path + "-wal") && !File.Exists(path + DeletionMarker)
            && !Directory.EnumerateFiles(directory, $".{name[..^FileExtension.Length]}.*.creating*").Any();

    private void RecoverDirectory(string directory)
    {
        RejectLink(directory);
        var changed = false;
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(file);
            foreach (var sidecar in Sidecars)
            {
                if (name.EndsWith(sidecar, StringComparison.Ordinal))
                {
                    name = name[..^sidecar.Length];
                    break;
                }
            }

            if (name.Split('.') is ["", var database, var nonce, "creating"]
                && (IsId(database) || database is SystemName or IntentName) && IsId(nonce))
            {
                var target = Path.Combine(directory, database + FileExtension);
                if (!File.Exists(target) && !File.Exists(target + "-wal") && File.Exists(target + DatabaseKeys.Suffix))
                {
                    RetainKey(directory, database, target + DatabaseKeys.Suffix);
                }
                DeleteFiles(Path.Combine(directory, name));
                changed = true;
            }
            else if (name.EndsWith(FileExtension, StringComparison.Ordinal)
                && (IsId(name[..^FileExtension.Length]) || name == SystemName + FileExtension || name == IntentFile))
            {
                var path = Path.Combine(directory, name);
                RejectDatabaseLinks(path);
                if (OrphanKey(directory, name, path))
                {
                    RetainKey(directory, name[..^FileExtension.Length], path + DatabaseKeys.Suffix);
                    changed = true;
                }
                else if (!File.Exists(path) && File.Exists(path + DeletionMarker))
                {
                    var tenant = IsId(name[..^FileExtension.Length]);
                    DeleteSidecars(path, keepMarker: tenant);
                    if (tenant && _deletions.All(deletion => deletion.Path != path))
                    {
                        _deletions.Add((new StorageFaultLedger.Key(
                            Path.GetFileName(Path.GetDirectoryName(directory)!), Path.GetFileName(directory), name[..^FileExtension.Length]), path));
                    }
                    changed = true;
                }
            }
        }

        if (changed)
        {
            DirectorySync(directory);
        }

        _retainedKeys += Directory.EnumerateFiles(directory, $".*.*.{RetainedKeySuffix}").LongCount();
    }

    private void RetainKey(string directory, string database, string key)
    {
        RejectLink(key);
        var retained = Path.Combine(directory, $".{database}.{EntityIds.New()}.{RetainedKeySuffix}");
        File.Move(key, retained, overwrite: false);
        _logger.ZLogWarning($"Retained database key {Path.GetFileName(key):@Key} without its main file or write-ahead log as {Path.GetFileName(retained):@Retained}; it stays until an operator removes it");
    }
}
