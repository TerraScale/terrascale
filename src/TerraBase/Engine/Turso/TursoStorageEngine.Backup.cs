using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Turso.Data.Sqlite;
using TerraBase.Tenancy;
using TerraBase.Primitives;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageEngine
{
    public async ValueTask<IWalTap?> OpenTapAsync(TenantContext? database, CancellationToken cancellationToken)
    {
        TursoDatabaseStore? store;
        if (database is null)
        {
            store = (TursoDatabaseStore)await OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
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
            return await Io.WithAdmissionAsync(async () => await TursoChangeTap.OpenAsync(store, Io, HostTime, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            Io.Release();
        }
    }

    public LogCut CutLog(Stream segment, DateTimeOffset at, long? previous) => ChangeLog.Cut(segment, at, previous);

    public DateTimeOffset? ReadMark(Stream segment) => ChangeLog.EndMark(segment);

    public async ValueTask<bool> RollForwardAsync(string file, IAsyncEnumerable<Stream> segments, CancellationToken cancellationToken)
    {
        Io.Admit();
        try
        {
            var connectionString = await Encryption.ExistingAsync(file, Io, cancellationToken).ConfigureAwait(false);
            if (!await ChangeLog.ApplyAsync(file, segments, Io, connectionString, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            return await Io.RunAsync(() =>
            {
                try
                {
                    using var connection = new SqliteConnection(connectionString);
                    TursoSession.OpenExisting(connection, file);
                    using var command = connection.CreateCommand();
                    command.CommandText = "PRAGMA integrity_check;";
                    if (command.ExecuteScalar() as string != "ok")
                    {
                        return false;
                    }

                    command.CommandText = TursoStatements.RenewIncarnation;
                    command.Parameters.AddWithValue("$incarnation", EntityIds.New().ToString());
                    command.ExecuteNonQuery();
                    command.Parameters.Clear();
                    command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    command.ExecuteScalar();
                    return true;
                }
                catch (SqliteException)
                {

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
        Task<TursoDatabaseStore> initialization;
        await EnterCreationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await ThrowIfPurgeFencedAsync(key.Database, cancellationToken).ConfigureAwait(false);
            ThrowIfIntentPending(key);
            RejectDatabaseLinks(path);
            if (File.Exists(path))
            {
                throw new InvalidOperationException($"database '{key.Database}' already exists");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await PublishAsync(key.Database, path, async temporary =>
            {
                var sourceConnection = await Encryption.ExistingAsync(file, Io, cancellationToken).ConfigureAwait(false);
                var targetConnection = await Encryption.CreateAsync(temporary, Io, cancellationToken).ConfigureAwait(false);
                await Io.RunAsync(() =>
                {
                    using var source = new global::Turso.TursoConnection(sourceConnection);
                    TursoSession.OpenExisting(source, file);
                    using var begin = source.CreateCommand();
                    begin.CommandText = "BEGIN;";
                    begin.ExecuteNonQuery();

                    TursoCopy.Write(source, targetConnection, cancellationToken);
                }, cancellationToken).ConfigureAwait(false);
                await TursoDatabaseStore.InitializeAsync(key.Database, temporary, Io, _pragmas, Encryption, cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
            await Io.RunAsync(() => DeleteFiles(file), CancellationToken.None).ConfigureAwait(false);
            _checkpoint("create.published", database);
            initialization = GetInitializationLocked(key, path, cancellationToken, out _);
        }
        finally
        {
            ExitCreation();
        }

        await initialization.ConfigureAwait(false);
    }

    internal ValueTask<byte[]> RestoreDrillChecksumAsync(string file, CancellationToken cancellationToken) =>
    ChecksumAsync(file, cancellationToken);

    public async ValueTask<byte[]> ChecksumAsync(string file, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        Io.Admit();
        try
        {
            var connectionString = await Encryption.ExistingAsync(file, Io, cancellationToken).ConfigureAwait(false);
            return await Io.RunAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var connection = new SqliteConnection(connectionString);
                TursoSession.OpenExisting(connection, file);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var tables = new List<string>();
                using (var schema = connection.CreateCommand())
                {
                    schema.CommandText = "SELECT type, name, sql FROM sqlite_schema ORDER BY type, name;";
                    using var rows = schema.ExecuteReader();
                    while (rows.Read())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Append(hash, rows);

                        if (rows.GetString(0) == "table" && !ChangeLog.EngineTable(rows.GetString(1)))
                        {
                            tables.Add(rows.GetString(1));
                        }
                    }
                }

                foreach (var table in tables)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = "\"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
                    using var command = connection.CreateCommand();

                    var projection = table == "commit_offset" ? "id, value" : "*";
                    command.CommandText = $"SELECT {projection} FROM {name} LIMIT 0;";
                    int columns;
                    using (var shape = command.ExecuteReader())
                    {
                        columns = shape.FieldCount;
                    }

                    command.CommandText = $"SELECT {projection} FROM {name} ORDER BY {string.Join(", ", Enumerable.Range(1, columns).Select(column => column.ToString(CultureInfo.InvariantCulture)))};";
                    using var rows = command.ExecuteReader();
                    hash.AppendData(Encoding.UTF8.GetBytes(table + "\0"));
                    while (rows.Read())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Append(hash, rows);
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                return hash.GetHashAndReset();
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Io.Release();
        }

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
