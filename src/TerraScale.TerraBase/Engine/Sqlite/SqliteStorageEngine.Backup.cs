using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TerraBase.Tenancy;

namespace TerraBase.Engine.Sqlite;

/// <summary>Continuous backup of the engine's files (TS-633): log taps, roll-forward and import of restored files.</summary>
internal sealed partial class SqliteStorageEngine
{
    public async ValueTask<IWalTap?> OpenTapAsync(TenantContext? database, CancellationToken cancellationToken)
    {
        SqliteDatabaseStore? store;
        if (database is null)
        {
            store = (SqliteDatabaseStore)await OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var (key, path) = Locate(database);
            store = File.Exists(path) ? await OpenStoreAsync(key, path, cancellationToken).ConfigureAwait(false) : null;
        }

        if (store is null)
        {
            return null;
        }

        Io.Admit();
        try
        {
            return new SqliteWalTap(store, Io, await store.RentSessionAsync(cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            Io.Release();
        }
    }

    public async ValueTask<bool> RollForwardAsync(string file, IAsyncEnumerable<Stream> segments, CancellationToken cancellationToken)
    {
        if (!await SqliteWalTap.ApplyAsync(file, segments, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        Io.Admit();
        try
        {
            return await Io.RunAsync(() =>
            {
                try
                {
                    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = file,
                        Mode = SqliteOpenMode.ReadWrite,
                        Pooling = false,
                    }.ToString());
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "PRAGMA integrity_check;";
                    return command.ExecuteScalar() as string == "ok";
                }
                catch (SqliteException)
                {
                    // Not a database, or corrupt beyond what integrity_check reports.
                    return false;
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Io.Release();
        }
    }

    public async ValueTask ImportDatabaseAsync(TenantContext database, string file, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        var (key, path) = Locate(database);
        Task<SqliteDatabaseStore> initialization;
        await EnterCreationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfIntentPending(key);
            RejectDatabaseLinks(path);
            if (File.Exists(path))
            {
                throw new InvalidOperationException($"database '{key.Database}' already exists");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await PublishAsync(key.Database, path, async temporary =>
            {
                File.Move(file, temporary);
                await SqliteDatabaseStore.InitializeAsync(key.Database, temporary, Io, _pragmas, SqliteDatabaseStore.CopyReset, cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
            _checkpoint("create.published", database);
            initialization = GetInitializationLocked(key, path, cancellationToken, out _);
        }
        finally
        {
            ExitCreation();
        }

        await initialization.ConfigureAwait(false);
    }

    /// <remarks>ponytail: sorts every table by all its columns, a full scan each; fine for a daily drill of small databases.</remarks>
    public async ValueTask<byte[]> ChecksumAsync(string file, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        Io.Admit();
        try
        {
            return await Io.RunAsync(() =>
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = file,
                    Mode = SqliteOpenMode.ReadWrite,
                    Pooling = false,
                }.ToString());
                connection.Open();
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var tables = new List<string>();
                using (var schema = connection.CreateCommand())
                {
                    schema.CommandText = "SELECT type, name, sql FROM sqlite_schema ORDER BY type, name;";
                    using var rows = schema.ExecuteReader();
                    while (rows.Read())
                    {
                        Append(hash, rows);
                        if (rows.GetString(0) == "table" && !rows.GetString(1).StartsWith("sqlite_", StringComparison.Ordinal))
                        {
                            tables.Add(rows.GetString(1));
                        }
                    }
                }

                foreach (var table in tables)
                {
                    var name = "\"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
                    using var command = connection.CreateCommand();
                    command.CommandText = $"SELECT * FROM {name} LIMIT 0;";
                    int columns;
                    using (var shape = command.ExecuteReader())
                    {
                        columns = shape.FieldCount;
                    }

                    command.CommandText = $"SELECT * FROM {name} ORDER BY {string.Join(", ", Enumerable.Range(1, columns).Select(column => column.ToString(CultureInfo.InvariantCulture)))};";
                    using var rows = command.ExecuteReader();
                    hash.AppendData(Encoding.UTF8.GetBytes(table + "\0"));
                    while (rows.Read())
                    {
                        Append(hash, rows);
                    }
                }

                return hash.GetHashAndReset();
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Io.Release();
        }

        // Each value with a type tag and, for text and blobs, its length, so no two rows hash alike by concatenation.
        static void Append(IncrementalHash hash, SqliteDataReader row)
        {
            Span<byte> scalar = stackalloc byte[9];
            for (var i = 0; i < row.FieldCount; i++)
            {
                switch (row.IsDBNull(i) ? null : row.GetValue(i))
                {
                    case null:
                        hash.AppendData([0]);
                        break;
                    case long integer:
                        scalar[0] = 1;
                        BinaryPrimitives.WriteInt64BigEndian(scalar[1..], integer);
                        hash.AppendData(scalar);
                        break;
                    case double real:
                        scalar[0] = 2;
                        BinaryPrimitives.WriteDoubleBigEndian(scalar[1..], real);
                        hash.AppendData(scalar);
                        break;
                    case string text:
                        var bytes = Encoding.UTF8.GetBytes(text);
                        scalar[0] = 3;
                        BinaryPrimitives.WriteInt64BigEndian(scalar[1..], bytes.Length);
                        hash.AppendData(scalar);
                        hash.AppendData(bytes);
                        break;
                    case byte[] blob:
                        scalar[0] = 4;
                        BinaryPrimitives.WriteInt64BigEndian(scalar[1..], blob.Length);
                        hash.AppendData(scalar);
                        hash.AppendData(blob);
                        break;
                }
            }
        }
    }
}
