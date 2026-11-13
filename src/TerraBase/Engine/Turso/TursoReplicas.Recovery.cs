using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraBase.Features.Backup;
using TerraBase.Primitives;
using Turso.Data.Sqlite;
using ZLogger;

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
            foreach (var detached in DetachedPaths(Root))
            {
                TryRecoverDetached(detached);
            }
        }
    }

    private IEnumerable<string> DetachedPaths(string directory)
    {
        string[] files;
        string[] children;
        try
        {
            DatabaseKeys.RejectLink(directory);
            files = Directory.GetFiles(directory, "*.promote.db*");
            children = Directory.GetDirectories(directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            var scope = Path.GetRelativePath(Root, directory);
            if (storage is TursoStorageEngine engine)
            {
                engine.RefuseDetachedScope(scope);
            }
            logger.ZLogError($"Detached import recovery cannot enumerate database scope {scope:@Scope}; its databases are refused and all copies preserved: {error.GetType().Name:@Error}");
            yield break;
        }
        foreach (var detached in files.Select(path => path[..(path.LastIndexOf(".promote.db", StringComparison.Ordinal) + ".promote.db".Length)]).Distinct(StringComparer.Ordinal))
        {
            yield return detached;
        }
        foreach (var child in children)
        {
            foreach (var detached in DetachedPaths(child))
            {
                yield return detached;
            }
        }
    }

    private void TryRecoverDetached(string detached)
    {
        try
        {
            RecoverDetached(detached);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or SqliteException or JsonException)
        {
            var database = Path.GetRelativePath(Root, detached[..^".promote.db".Length] + ".db");
            if (storage is TursoStorageEngine engine)
            {
                engine.RefuseDetached(database);
            }
            logger.ZLogError($"Detached import recovery refused database {database:@Database}; all remaining copies are preserved: {error.GetType().Name:@Error}");
        }
    }

    internal static void PrepareImport(string source, string temporary, IKeyring master)
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
            var intent = DetachedImport.Read(source, Path.GetFileName(source)[..^".promote.db".Length]);
            var sourceKey = master.Unwrap(intent.SourceKey);
            try
            {
                intent.Verify(source, sourceKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(sourceKey);
            }
            (intent with { DestinationKey = DatabaseKeys.ReadWrapped(temporary) }).Write(source, create: false);
        }
    }

    internal static void SealImport(string source, string temporary, string targetConnection, IKeyring master)
    {
        if (!source.EndsWith(".promote.db", StringComparison.Ordinal))
        {
            return;
        }
        var intent = DetachedImport.Read(source, Path.GetFileName(source)[..^".promote.db".Length]);
        using (var connection = new SqliteConnection(targetConnection))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = TursoStatements.RenewIncarnation;
            command.Parameters.AddWithValue("$incarnation", intent.Incarnation);
            command.ExecuteNonQuery();
            command.Parameters.Clear();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            command.ExecuteScalar();
        }
        var key = master.Unwrap(DatabaseKeys.ReadWrapped(temporary));
        try
        {
            intent.Verify(temporary, key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
        using var handle = File.OpenHandle(temporary, FileMode.Open, FileAccess.ReadWrite);
        RandomAccess.FlushToDisk(handle);
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
        foreach (var path in (string[])[detached, replica, destination])
        {
            DatabaseKeys.RejectLink(path);
            DatabaseKeys.RejectLink(path + DatabaseKeys.Suffix);
        }
        var intent = DetachedImport.Read(detached, Path.GetFileName(replica)[..^3]);
        var sourceKey = master.Unwrap(intent.SourceKey);
        byte[]? destinationKey = null;
        try
        {
            destinationKey = intent.DestinationKey is { } wrapped ? master.Unwrap(wrapped) : null;
            if (File.Exists(destination))
            {
                if (destinationKey is null)
                {
                    throw new InvalidDataException("a destination exists without publication evidence");
                }
                VerifyKey(DatabaseKeys.ReadWrapped(destination), destinationKey);
                intent.Verify(destination, destinationKey);
                if (DataKeyRecord(replica) is { } replicaKey)
                {
                    VerifyKey(replicaKey, sourceKey);
                }
                if (File.Exists(replica))
                {
                    if (DataKeyRecord(replica) is null)
                    {
                        throw new InvalidDataException("an existing replica has no key");
                    }
                    intent.Verify(replica, sourceKey);
                }
                if (File.Exists(detached))
                {
                    VerifyKey(DatabaseKeys.ReadWrapped(detached), sourceKey);
                    intent.Verify(detached, sourceKey);
                }
                SyncDatabase(destination);
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
            if (File.Exists(destination + DatabaseKeys.Suffix))
            {
                if (destinationKey is null)
                {
                    throw new InvalidDataException("an unpublished destination key has no intent");
                }
                VerifyKey(DatabaseKeys.ReadWrapped(destination), destinationKey);
            }
            if (File.Exists(detached) && File.Exists(replica))
            {
                throw new InvalidDataException("an independent replica conflicts with the detached source");
            }
            if (File.Exists(detached))
            {
                VerifyKey(DatabaseKeys.ReadWrapped(detached), sourceKey);
                File.Move(detached, replica, overwrite: false);
            }
            foreach (var file in Directory.GetFiles(directory, Path.GetFileName(detached) + "-*"))
            {
                DatabaseKeys.RejectLink(file);
                File.Move(file, replica + file[detached.Length..], overwrite: false);
            }
            intent.Verify(replica, sourceKey);
            if (DataKeyRecord(replica) is { } restored)
            {
                VerifyKey(restored, sourceKey);
            }
            else
            {
                using var file = new FileStream(replica + DatabaseKeys.Suffix, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                file.Write(Encoding.ASCII.GetBytes((intent.SourceKey.StartsWith("v2.", StringComparison.Ordinal) ? KeyHeader : PreviousKeyHeader) + intent.SourceKey + "\n"));
                file.Flush(flushToDisk: true);
            }
            SyncDatabase(replica);
            DirectorySync.Sync(directory);
            if (File.Exists(destination + DatabaseKeys.Suffix))
            {
                File.Delete(destination + DatabaseKeys.Suffix);
                DirectorySync.Sync(Path.GetDirectoryName(destination)!);
            }
            DeleteDetachedFiles(detached);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sourceKey);
            if (destinationKey is not null)
            {
                CryptographicOperations.ZeroMemory(destinationKey);
            }
        }
    }

    private static void SyncDatabase(string database)
    {
        foreach (var path in (string[])[database, database + "-wal"])
        {
            DatabaseKeys.RejectLink(path);
            if (File.Exists(path))
            {
                using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);
                RandomAccess.FlushToDisk(handle);
            }
        }
    }

    private void VerifyKey(string wrapped, byte[] expected)
    {
        var actual = master.Unwrap(wrapped);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                throw new InvalidDataException("a detached import key does not match its intent");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actual);
        }
    }
}
