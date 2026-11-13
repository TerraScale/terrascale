using System.Security.Cryptography;
using System.Text;
using TerraBase.Primitives;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoReplicas
{
    internal void RecoverDetached()
    {
        lock (KeyRecords)
        {
            if (!Directory.Exists(Root))
            {
                return;
            }
            DatabaseKeys.RejectLink(Root);
            foreach (var detached in Directory.GetFiles(Root, "*.promote.db", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(Root, "*.promote.db.key", SearchOption.AllDirectories))
                .Concat(Directory.GetFiles(Root, "*.promote.db.importing", SearchOption.AllDirectories))
                .Select(path => path[..(path.LastIndexOf(".promote.db", StringComparison.Ordinal) + ".promote.db".Length)])
                .Distinct(StringComparer.Ordinal))
            {
                RecoverDetached(detached);
            }
        }
    }

    internal static void PrepareImport(string source, string temporary)
    {
        if (!source.EndsWith(".promote.db", StringComparison.Ordinal))
        {
            return;
        }
        lock (KeyRecords)
        {
            if (!ActiveDetached.Contains(source))
            {
                throw new IOException("a detached import must hold its registration");
            }
            var intent = source + ".importing";
            DatabaseKeys.RejectLink(intent);
            using (var file = new FileStream(intent, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                file.Write(DatabaseKeys.ReadRecord(temporary));
                file.Flush(flushToDisk: true);
            }
            DirectorySync.Sync(Path.GetDirectoryName(source)!);
        }
    }

    private void RecoverDetached(string detached)
    {
        if (ActiveDetached.Contains(detached))
        {
            throw new IOException("a promotion already holds the detached replica");
        }
        var directory = Path.GetDirectoryName(detached)!;
        if (!Directory.Exists(directory) || Directory.GetFiles(directory, Path.GetFileName(detached) + "*").Length == 0)
        {
            return;
        }
        var replica = detached[..^".promote.db".Length] + ".db";
        var destination = Path.Combine(options.DataDirectory, Path.GetRelativePath(Root, replica));
        var intent = detached + ".importing";
        foreach (var path in (string[])[detached, replica, destination, intent])
        {
            DatabaseKeys.RejectLink(path);
            DatabaseKeys.RejectLink(path + DatabaseKeys.Suffix);
        }
        if (File.Exists(intent) && File.Exists(destination) && File.Exists(destination + DatabaseKeys.Suffix))
        {
            if (new FileInfo(intent).Length is <= 0 or > DatabaseKeys.MaxFileBytes)
            {
                throw new InvalidDataException("the detached import intent is malformed");
            }
            var record = File.ReadAllBytes(intent);
            DatabaseKeys.ValidateRecord(record);
            var expected = master.Unwrap(Encoding.ASCII.GetString(record.AsSpan(DatabaseKeys.Header.Length, record.Length - DatabaseKeys.Header.Length - 1)));
            try
            {
                var published = master.Unwrap(DatabaseKeys.ReadWrapped(destination));
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(expected, published))
                    {
                        throw new InvalidDataException("the destination does not match the detached import");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(published);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expected);
            }
            var publishedDirectory = Path.GetDirectoryName(destination)!;
            while (true)
            {
                DirectorySync.Sync(publishedDirectory);
                if (publishedDirectory == options.DataDirectory)
                {
                    break;
                }
                publishedDirectory = Path.GetDirectoryName(publishedDirectory) ?? throw new InvalidDataException("publication escaped the data directory");
            }
            DeleteFiles(replica, keepDetached: true);
            DirectorySync.Sync(directory);
            DeleteDetachedFiles(detached);
            return;
        }
        if (File.Exists(detached))
        {
            File.Move(detached, replica, overwrite: false);
        }
        foreach (var file in Directory.GetFiles(directory, Path.GetFileName(detached) + "-*"))
        {
            DatabaseKeys.RejectLink(file);
            File.Move(file, replica + file[detached.Length..], overwrite: false);
        }
        if (!File.Exists(replica))
        {
            throw new InvalidDataException("a detached replica has no database to put back");
        }
        if (!File.Exists(replica + DatabaseKeys.Suffix))
        {
            var wrapped = DatabaseKeys.ReadWrapped(detached);
            using var file = new FileStream(replica + DatabaseKeys.Suffix, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write(Encoding.ASCII.GetBytes((wrapped.StartsWith("v2.", StringComparison.Ordinal) ? KeyHeader : PreviousKeyHeader) + wrapped + "\n"));
            file.Flush(flushToDisk: true);
        }
        var restored = DataKeyRecord(replica) ?? throw new InvalidDataException("the put-back replica key is missing");
        var verified = master.Unwrap(restored);
        try
        {
            if (File.Exists(detached + DatabaseKeys.Suffix))
            {
                var detachedKey = master.Unwrap(DatabaseKeys.ReadWrapped(detached));
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(verified, detachedKey))
                    {
                        throw new InvalidDataException("the put-back replica key does not match the detached copy");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(detachedKey);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(verified);
        }
        DirectorySync.Sync(directory);
        DeleteDetachedFiles(detached);
    }
}
