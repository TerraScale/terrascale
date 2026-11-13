using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Primitives;
using Turso.Data.Sqlite;

namespace TerraBase.Engine.Turso;

internal sealed record DetachedImport(
    int Version, string Database, string Incarnation, long Position, long SourceBytes, long Rows, long ImportedBytes, string Digest,
    string SourceKey, string? DestinationKey)
{
    internal static DetachedImport Read(string detached, string database)
    {
        DatabaseKeys.RejectLink(detached + ".importing");
        using var file = new FileStream(detached + ".importing", FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > 16384)
        {
            throw new InvalidDataException("the detached import intent is malformed");
        }
        var result = JsonSerializer.Deserialize(file, DetachedImportJsonContext.Default.DetachedImport)
            ?? throw new InvalidDataException("the detached import intent is missing");
        if (result.Version != 1 || result.Database != database || !Ulid.TryParse(result.Incarnation, out _)
            || result.Position < 0 || result.SourceBytes <= 0 || result.Rows < 0 || result.ImportedBytes <= 0 || result.Digest.Length != 64
            || !result.Digest.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F'))
        {
            throw new InvalidDataException("the detached import intent does not bind this database");
        }
        return result;
    }

    internal void Write(string detached, bool create)
    {
        var path = detached + ".importing";
        var writing = create ? path : path + ".writing";
        DatabaseKeys.RejectLink(writing);
        using (var file = new FileStream(writing, create ? FileMode.CreateNew : FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, this, DetachedImportJsonContext.Default.DetachedImport);
            file.Flush(flushToDisk: true);
        }
        if (!create)
        {
            File.Move(writing, path, overwrite: true);
        }
        DirectorySync.Sync(Path.GetDirectoryName(detached)!);
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

    internal static (string Incarnation, long Position, long Rows, long Bytes, string Digest) Inspect(SqliteConnection connection)
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
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var tables = new List<string>();
        var bytes = 0L;
        command.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_schema WHERE sql IS NOT NULL ORDER BY type, name;";
        using (var rows = command.ExecuteReader())
        {
            while (rows.Read())
            {
                if (!ChangeLog.EngineTable(rows.GetString(2)))
                {
                    bytes += Append(hash, rows);
                    if (rows.GetString(0) == "table")
                    {
                        tables.Add(rows.GetString(1));
                    }
                }
            }
        }
        var count = 0L;
        foreach (var table in tables.Where(table => table != "_tb_shape_snapshot"))
        {
            var projection = table == "commit_offset" ? "id, value" : "*";
            var filter = table == "_tb_shape_chunk" ? " WHERE handle >= '~grant:' AND handle < '~grant;' AND offset = 0" : "";
            command.CommandText = $"SELECT {projection} FROM {TableShape.Quote(table)} LIMIT 0;";
            int columns;
            using (var shape = command.ExecuteReader())
            {
                columns = shape.FieldCount;
            }
            command.CommandText = $"SELECT {projection} FROM {TableShape.Quote(table)}{filter} ORDER BY {string.Join(", ", Enumerable.Range(1, columns).Select(column => column.ToString(CultureInfo.InvariantCulture)))};";
            hash.AppendData(Encoding.UTF8.GetBytes(table + "\0"));
            using var rows = command.ExecuteReader();
            while (rows.Read())
            {
                bytes += Append(hash, rows);
                count++;
            }
        }
        return (incarnation, position, count, bytes, Convert.ToHexString(hash.GetHashAndReset()));
    }

    internal void Verify(string path, byte[] key)
    {
        using var connection = Open(path, key);
        var observed = Inspect(connection);
        if (observed.Incarnation != Incarnation || observed.Position < Position || observed.Rows != Rows || observed.Bytes < ImportedBytes || observed.Digest != Digest)
        {
            throw new InvalidDataException("the detached import database content does not match its source evidence");
        }
    }

    private static long Append(IncrementalHash hash, SqliteDataReader row)
    {
        var length = 0L;
        Span<byte> scalar = stackalloc byte[9];
        for (var i = 0; i < row.FieldCount; i++)
        {
            switch (row.IsDBNull(i) ? null : row.GetValue(i))
            {
                case null:
                    hash.AppendData([0]);
                    length++;
                    break;
                case long integer:
                    scalar[0] = 1;
                    BinaryPrimitives.WriteInt64BigEndian(scalar[1..], integer);
                    hash.AppendData(scalar);
                    length += scalar.Length;
                    break;
                case double real:
                    scalar[0] = 2;
                    BinaryPrimitives.WriteDoubleBigEndian(scalar[1..], real);
                    hash.AppendData(scalar);
                    length += scalar.Length;
                    break;
                case string text:
                    var bytes = Encoding.UTF8.GetBytes(text);
                    scalar[0] = 3;
                    BinaryPrimitives.WriteInt64BigEndian(scalar[1..], bytes.Length);
                    hash.AppendData(scalar);
                    hash.AppendData(bytes);
                    length += scalar.Length + bytes.Length;
                    break;
                case byte[] blob:
                    scalar[0] = 4;
                    BinaryPrimitives.WriteInt64BigEndian(scalar[1..], blob.Length);
                    hash.AppendData(scalar);
                    hash.AppendData(blob);
                    length += scalar.Length + blob.Length;
                    break;
                default:
                    throw new InvalidDataException("the detached import has an unsupported value");
            }
        }
        return length;
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, AllowDuplicateProperties = false)]
[JsonSerializable(typeof(DetachedImport))]
internal sealed partial class DetachedImportJsonContext : JsonSerializerContext;
