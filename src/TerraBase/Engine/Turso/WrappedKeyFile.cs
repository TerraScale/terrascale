using System.Security.Cryptography;
using System.Text;

using TerraBase.Features.Backup;

namespace TerraBase.Engine.Turso;

internal static class WrappedKeyFile
{
    internal static string Rewrap(string path, string header, string wrapped, IKeyring keyring, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = keyring.Unwrap(wrapped);
        try
        {
            if (keyring.KeyId(wrapped) == keyring.ActiveId && wrapped.StartsWith("v2.", StringComparison.Ordinal))
            {
                return keyring.ActiveId;
            }

            var temporary = path + ".rewrapping";
            try
            {
                DatabaseKeys.RejectLink(temporary);
                using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    file.Write(Encoding.ASCII.GetBytes(header + keyring.Wrap(key) + "\n"));
                    file.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
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
