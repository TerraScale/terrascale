using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Primitives;
using Turso.Data.Sqlite;

namespace TerraBase.Engine.Turso;

internal sealed record DetachedImport(
    int Version, string Database, string Incarnation, long Position, string SourceKey, string? DestinationKey,
    bool Published = false, bool Restored = false)
{
    internal static DetachedImport Read(string path, string database)
    {
        DatabaseKeys.RejectLink(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > 16384)
        {
            throw new InvalidDataException("the detached import intent is malformed");
        }
        var result = JsonSerializer.Deserialize(file, DetachedImportJsonContext.Default.DetachedImport)
            ?? throw new InvalidDataException("the detached import intent is missing");
        if (result.Version != 1 || result.Database != database || string.IsNullOrEmpty(result.Incarnation) || !Ulid.TryParse(result.Incarnation, out _)
            || result.Position < 0 || string.IsNullOrEmpty(result.SourceKey) || result.Published && result.Restored)
        {
            throw new InvalidDataException("the detached import intent does not bind this database");
        }
        return result;
    }

    internal void Write(string path, bool create)
    {
        var writing = create ? path : path + ".writing." + EntityIds.New();
        DatabaseKeys.RejectLink(writing);
        using (var file = new FileStream(writing, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, this, DetachedImportJsonContext.Default.DetachedImport);
            file.Flush(flushToDisk: true);
        }
        if (!create)
        {
            File.Move(writing, path, overwrite: true);
        }
        DirectorySync.Sync(Path.GetDirectoryName(path)!);
    }

    private static SqliteConnection Open(string path, byte[] key)
    {
        DatabaseKeys.RejectLink(path);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            EncryptionCipher = "aegis256",
            EncryptionKey = Convert.ToHexString(key),
        }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static string ScratchRoot(string dataDirectory)
    {
        var root = Path.GetFullPath(dataDirectory);
        DatabaseKeys.RejectLink(root);
        var temporary = Path.Combine(root, ".tmp");
        DatabaseKeys.RejectLink(temporary);
        var inspection = Path.Combine(temporary, "inspection");
        DatabaseKeys.RejectLink(inspection);
        return inspection;
    }

    internal static void CleanScratch(string dataDirectory)
    {
        var root = ScratchRoot(dataDirectory);
        if (Directory.Exists(root))
        {
            foreach (var key in WrappedKeyFile.Paths(root).ToArray())
            {
                DatabaseKeys.RejectLink(key);
                File.Move(key, Path.Combine(dataDirectory, $".inspection.{EntityIds.New()}.retained-key"), overwrite: false);
                DirectorySync.Sync(dataDirectory);
            }
            Directory.Delete(root, recursive: true);
        }
    }

    internal static (string Incarnation, long Position) Inspect(string path, byte[] key, string dataDirectory, IDiskBudget disk)
    {
        DatabaseKeys.RejectLink(path);
        var name = Path.GetFileName(path);
        var files = Directory.EnumerateFiles(Path.GetDirectoryName(path)!, name + "*")
            .Where(file => file == path || file.StartsWith(path + "-", StringComparison.Ordinal)
                || file.StartsWith(path + ".", StringComparison.Ordinal)).ToArray();
        long bytes = 0;
        foreach (var file in files)
        {
            DatabaseKeys.RejectLink(file);
            bytes = checked(bytes + new FileInfo(file).Length);
        }
        using var claim = disk.Reserve(DiskBudget.WorkspaceBytes(bytes));
        var scratch = Directory.CreateDirectory(Path.Combine(ScratchRoot(dataDirectory), EntityIds.New().ToString()));
        try
        {
            foreach (var file in files.Where(file => !WrappedKeyFile.IsKey(Path.GetFileName(file))))
            {
                File.Copy(file, Path.Combine(scratch.FullName, Path.GetFileName(file)));
            }
            using var connection = Open(Path.Combine(scratch.FullName, name), key);
            return Inspect(connection);
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    private static (string Incarnation, long Position) Inspect(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA integrity_check;";
        using (var rows = command.ExecuteReader())
        {
            if (!rows.Read() || rows.GetString(0) != "ok" || rows.Read())
            {
                throw new InvalidDataException("the detached import database failed its integrity check");
            }
        }
        command.CommandText = "SELECT incarnation, value FROM commit_offset WHERE id = 1;";
        string incarnation;
        long position;
        using (var rows = command.ExecuteReader())
        {
            if (!rows.Read())
            {
                throw new InvalidDataException("the detached import database has no identity");
            }
            if (rows.IsDBNull(0) || rows.GetValue(0) is not string identity || !Ulid.TryParse(identity, out _)
                || rows.IsDBNull(1) || rows.GetValue(1) is not long offset || offset < 0)
            {
                throw new InvalidDataException("the detached import database identity is malformed");
            }
            incarnation = identity;
            position = offset;
        }
        return (incarnation, position);
    }

    internal void Verify(string path, byte[] key, string dataDirectory, IDiskBudget disk)
    {
        var observed = Inspect(path, key, dataDirectory, disk);
        if (observed.Incarnation != Incarnation || observed.Position < Position)
        {
            throw new InvalidDataException("the detached import database incarnation or position does not match its intent");
        }
    }

}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, AllowDuplicateProperties = false)]
[JsonSerializable(typeof(DetachedImport))]
internal sealed partial class DetachedImportJsonContext : JsonSerializerContext;
