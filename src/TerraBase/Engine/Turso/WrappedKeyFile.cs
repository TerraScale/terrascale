using System.Security.Cryptography;
using System.Text;

using TerraBase.Features.Backup;
using TerraBase.Primitives;

namespace TerraBase.Engine.Turso;

internal static class WrappedKeyFile
{
    internal static bool IsKey(string path) =>
        path.EndsWith(".key", StringComparison.Ordinal) || path.Contains(".key.rewrapping", StringComparison.Ordinal)
        || path.Contains(".key.tmp", StringComparison.Ordinal) || path.Contains(".retained-key", StringComparison.Ordinal);

    internal static void Retain(string path)
    {
        DatabaseKeys.RejectLink(path);
        if (!File.Exists(path) || path.Contains(".retained-key", StringComparison.Ordinal))
        {
            return;
        }
        var directory = Path.GetDirectoryName(path)!;
        var retained = Path.Combine(directory, $".{Path.GetFileName(path)}.{EntityIds.New()}.retained-key");
        File.Move(path, retained, overwrite: false);
        DirectorySync.Sync(directory);
    }

    internal static IEnumerable<string> Paths(string directory)
    {
        DatabaseKeys.RejectLink(directory);
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (IsKey(Path.GetFileName(file)))
            {
                yield return file;
            }
        }
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            foreach (var file in Paths(child))
            {
                yield return file;
            }
        }
    }

    internal static string Read(string path)
    {
        DatabaseKeys.RejectLink(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > DatabaseKeys.MaxFileBytes)
        {
            throw new InvalidDataException("wrapped key file is malformed");
        }
        using var reader = new StreamReader(file, Encoding.ASCII);
        var lines = reader.ReadToEnd().Split('\n');
        if (lines is not [var header, var wrapped, ""])
        {
            throw new InvalidDataException("wrapped key file is malformed");
        }
        if (header is not ("TERRABASE-DB-KEY-1" or "TERRABASE-DB-KEY-2" or "TERRABASE-REPLICA-KEY-1" or "TERRABASE-REPLICA-KEY-2")
            || string.IsNullOrEmpty(wrapped)
            || header.EndsWith('2') != wrapped.StartsWith("v2.", StringComparison.Ordinal))
        {
            throw new InvalidDataException("wrapped key file is malformed");
        }
        return wrapped;
    }

    internal static string Rewrap(string path, string header, string wrapped, IKeyring keyring, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = keyring.Unwrap(wrapped);
        try
        {
            if (!keyring.NeedsRewrap(wrapped))
            {
                return keyring.ActiveId;
            }

            if (path.Contains(".retained-key", StringComparison.Ordinal))
            {
                var replica = File.ReadLines(path).First().StartsWith("TERRABASE-REPLICA-KEY-", StringComparison.Ordinal);
                header = replica ? keyring.ActiveId == "k0" ? TursoReplicas.PreviousKeyHeader : TursoReplicas.KeyHeader
                    : keyring.ActiveId == "k0" ? DatabaseKeys.PreviousHeader : DatabaseKeys.Header;
            }
            var temporary = path + ".rewrapping." + EntityIds.New();
            DatabaseKeys.RejectLink(temporary);
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(Encoding.ASCII.GetBytes(header + keyring.Rewrap(key, path) + "\n"));
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            DirectorySync.Sync(Path.GetDirectoryName(path)!);
            return keyring.ActiveId;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
