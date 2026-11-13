using System.Security.Cryptography;
using System.Text.Json;
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
        foreach (var file in WrappedKeyFile.Paths(Root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (KeyRecords)
            {
                try
                {
                    if (ActiveDetached.Contains(file[..^DatabaseKeys.Suffix.Length])
                        || ActiveDetached.Contains(file[..^".db.key".Length] + ".promote.db"))
                    {
                        continue;
                    }
                    DatabaseKeys.RejectLink(file);
                    if (!File.Exists(file))
                    {
                        continue;
                    }
                    var detached = file.Contains(".promote.db.key", StringComparison.Ordinal);
                    var wrapped = WrappedKeyFile.Read(file);
                    var header = detached ? (master.ActiveId == "k0" ? DatabaseKeys.PreviousHeader : DatabaseKeys.Header)
                        : master.ActiveId == "k0" ? PreviousKeyHeader : KeyHeader;
                    inventory.Count(WrappedKeyFile.Rewrap(file, header, wrapped, master, cancellationToken));
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    inventory.Unreadable(file);
                }
            }
        }
        foreach (var path in Directory.EnumerateFiles(Root, "*.promote.db.importing*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (KeyRecords)
            {
                var detached = path[..path.IndexOf(".importing", StringComparison.Ordinal)];
                if (ActiveDetached.Contains(detached))
                {
                    continue;
                }
                try
                {
                    var intent = DetachedImport.Read(path, Path.GetFileName(detached)[..^".promote.db".Length]);
                    var source = RewrapIntent(intent.SourceKey, path, inventory);
                    var destination = intent.DestinationKey is { } wrapped ? RewrapIntent(wrapped, path, inventory) : null;
                    if (source != intent.SourceKey || destination != intent.DestinationKey)
                    {
                        (intent with { SourceKey = source, DestinationKey = destination }).Write(path, create: false);
                    }
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
                {
                    inventory.Unreadable(path);
                }
            }
        }
        return Task.CompletedTask;
    }

    private string RewrapIntent(string wrapped, string item, KeyInventory inventory)
    {
        var key = master.Unwrap(wrapped);
        try
        {
            var replacement = master.NeedsRewrap(wrapped) ? master.Rewrap(key, item) : wrapped;
            inventory.Count(master.KeyId(replacement));
            return replacement;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
