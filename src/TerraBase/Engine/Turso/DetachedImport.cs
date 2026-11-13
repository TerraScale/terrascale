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
        if (result.Version != 1 || result.Database != database || !Ulid.TryParse(result.Incarnation, out _)
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

    internal static SqliteConnection Open(string path, byte[] key)
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

    internal static (string Incarnation, long Position) Inspect(SqliteConnection connection)
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
            incarnation = rows.GetString(0);
            position = rows.GetInt64(1);
        }
        return (incarnation, position);
    }

    internal void Verify(string path, byte[] key)
    {
        using var connection = Open(path, key);
        var observed = Inspect(connection);
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
