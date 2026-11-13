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
                if (!File.Exists(target) && !File.Exists(target + "-wal"))
                {
                    DeleteFileIfPresent(target + DatabaseKeys.Suffix);
                }
                DeleteFiles(Path.Combine(directory, name));
                changed = true;
            }
            else if (name.EndsWith(FileExtension, StringComparison.Ordinal)
                && (IsId(name[..^FileExtension.Length]) || name == SystemName + FileExtension || name == IntentFile))
            {
                var path = Path.Combine(directory, name);
                RejectDatabaseLinks(path);
                if (!File.Exists(path) && File.Exists(path + ".deleting"))
                {
                    DeleteSidecars(path);
                    changed = true;
                }
            }
        }

        if (changed)
        {
            DirectorySync(directory);
        }
    }
}
