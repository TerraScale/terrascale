namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageEngine
{
    private void SyncPublicationDirectory(string directory)
    {
        var root = Path.GetFullPath(_dataDirectory);
        var current = Path.GetFullPath(directory);
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
                DeleteFiles(Path.Combine(directory, name));
                changed = true;
            }
            else if (name.EndsWith(FileExtension, StringComparison.Ordinal)
                && (IsId(name[..^FileExtension.Length]) || name == SystemName + FileExtension || name == IntentFile))
            {
                var path = Path.Combine(directory, name);
                RejectDatabaseLinks(path);
                if (!File.Exists(path))
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
