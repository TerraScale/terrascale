using TerraBase.Features.Backup;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageEngine : IWrappedKeyFiles
{
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
