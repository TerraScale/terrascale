using System.Security.Cryptography;
using System.Text;

using TerraBase.Features.Backup;
using TerraBase.Primitives;

namespace TerraBase.Engine.Turso;

internal static class WrappedKeyFile
{
    internal static void RemoveLeftover(string path)
    {
        DatabaseKeys.RejectLink(path + ".rewrapping");
        if (Directory.Exists(Path.GetDirectoryName(path)))
        {
            File.Delete(path + ".rewrapping");
        }
    }

    internal static string Rewrap(string path, string header, string wrapped, IKeyring keyring, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RemoveLeftover(path);
        var key = keyring.Unwrap(wrapped);
        try
        {
            if (!keyring.NeedsRewrap(wrapped))
            {
                return keyring.ActiveId;
            }

            var temporary = path + ".rewrapping";
            try
            {
                DatabaseKeys.RejectLink(temporary);
                using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    file.Write(Encoding.ASCII.GetBytes(header + keyring.Rewrap(key, path) + "\n"));
                    file.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
                DirectorySync.Sync(Path.GetDirectoryName(path)!);
            }
            finally
            {
                File.Delete(temporary);
            }
            return keyring.ActiveId;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
