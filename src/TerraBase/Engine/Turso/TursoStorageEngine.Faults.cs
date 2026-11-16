using System.Diagnostics;
using System.Security.Cryptography;
using TerraBase.Features.Sql;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using Turso.Data.Sqlite;
using Turso.Raw.Public;
using ZLogger;

namespace TerraBase.Engine.Turso;

internal closed record StorageProof;
internal sealed record ProofPassed : StorageProof;
internal sealed record ProofDamaged(string Reason) : StorageProof;
internal sealed record ProofIncomplete(string Reason) : StorageProof;

internal sealed partial class TursoStorageEngine
{
    private static readonly string[] ProofFiles = ["", "-wal"];

    internal string CopyPath(TenantContext database)
    {
        var path = Place(database).Path;
        RejectDatabaseLinks(path);
        return path;
    }

    internal bool HasCopyFiles(TenantContext database) => ProofFiles.Any(suffix => File.Exists(CopyPath(database) + suffix));

    internal IEnumerable<StorageFaultLedger.Key> CopyFaulted() =>
        _stores.Values.Where(store => store.CopyFault is not null && store.Tenant is not null).Select(store => StorageFaultLedger.Key.Of(store.Tenant!));

    internal async Task<StorageProof> RecoverCopyAsync(TenantContext database, Ulid incarnation, Ulid replacement, CancellationToken cancellationToken)
    {
        var path = CopyPath(database);
        var scratch = Path.Combine(_dataDirectory, ".tmp", "proof-" + EntityIds.New());
        var checking = false;
        StorageProof proof;
        try
        {
            var bytes = await Io.RunAsync(() =>
            {
                try
                {
                    using var main = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    return RandomAccess.GetLength(main);
                }
                catch (FileNotFoundException)
                {
                    using var log = File.OpenHandle(path + "-wal", FileMode.Open, FileAccess.Read, FileShare.Read);
                    return -1L;
                }
            }, cancellationToken).ConfigureAwait(false);
            if (bytes <= 0)
            {
                return new ProofDamaged("the existing database main file is missing or empty");
            }

            var connectionString = await Encryption.ExistingAsync(path, Io, cancellationToken).ConfigureAwait(false);
            proof = await Io.RunAsync<StorageProof>(() =>
            {
                using var disk = Disk.Reserve(DiskBudget.WorkspaceBytes(DiskBudget.FileBytes(path)));
                Directory.CreateDirectory(scratch);
                var copy = Path.Combine(scratch, "copy.db");
                foreach (var suffix in ProofFiles)
                {
                    var source = path + suffix;
                    try
                    {
                        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                        using var output = new FileStream(copy + suffix, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        input.CopyTo(output);
                    }
                    catch (FileNotFoundException) when (suffix.Length > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                }
                var options = new SqliteConnectionStringBuilder(connectionString) { DataSource = copy, DefaultTimeout = 1 };
                var started = Stopwatch.GetTimestamp();
                using var session = new TursoSession(new SqliteConnection(options.ToString()), Time);
                var connection = session.Connection;
                connection.CloseRefusal = () => "a recovery proof never checkpoints its source";
                TursoSession.OpenExisting(connection, copy);
                connection.UseManualWal();
                TursoBindings.SetQueryTimeout(session.Native, TimeSpan.FromSeconds(30));
                using var interrupted = cancellationToken.Register(() => TursoBindings.Interrupt(session.Native));
                using var command = connection.CreateCommand();
                command.CommandText = TursoStatements.Incarnation;
                var recovered = command.ExecuteScalar() as string;
                var replayMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                started = Stopwatch.GetTimestamp();
                command.CommandText = "PRAGMA integrity_check;";
                checking = true;
                string? integrity = null;
                using (var results = command.ExecuteReader())
                {
                    while (results.Read())
                    {
                        var result = results.GetString(0);
                        if (integrity is null || result != "ok")
                        {
                            integrity = result;
                        }
                    }
                }
                checking = false;
                if (integrity is null)
                {
                    return new ProofIncomplete("integrity_unavailable");
                }
                if (integrity != "ok")
                {
                    return new ProofDamaged("the recovered database failed its integrity check");
                }
                cancellationToken.ThrowIfCancellationRequested();

                _logger.ZLogInformation($"Database restart proof: replay {replayMs:@ReplayMs} ms, integrity {Stopwatch.GetElapsedTime(started).TotalMilliseconds:@IntegrityMs} ms, file {bytes:@Bytes} bytes");
                return recovered is null ? new ProofIncomplete("identity_unavailable")
                    : recovered == incarnation.ToString() || recovered == replacement.ToString()
                        ? new ProofPassed() : new ProofDamaged("the recovered database incarnation does not match its write intent");
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException error)
        {
            var (failure, _) = SqlErrors.Classify(error.Message, false, false,
                error.SqliteExtendedErrorCode == 0 ? error.SqliteErrorCode : error.SqliteExtendedErrorCode);
            proof = new ProofIncomplete(failure switch
            {
                Busy => "busy_or_storage",
                Interrupted => "interrupted",
                StorageFailure => checking ? "integrity_engine_corruption" : "storage",
                Constraint or PrimaryKey or Unique or NotNull or Check or ForeignKeyFailure => "constraint",
                Error or KnownError or Syntax or NoSuchTable or NoSuchColumn or AlreadyExists or TooBig or ReadOnly or StaleRow => "unavailable",
            });
        }
        catch (Exception error)
        {
            _logger.ZLogWarning($"A recovery proof could not complete: {error.GetType().Name:@Failure}");

            proof = new ProofIncomplete(error switch
            {
                OperationCanceledException => "cancelled",
                TimeoutException => "timeout",
                StorageBusyException => "busy",
                IOException => "io",
                InvalidDataException or CryptographicException or UnauthorizedAccessException => "key_unavailable",
                _ => "unavailable",
            });
        }
        try
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
        catch (Exception error)
        {
            _logger.ZLogWarning($"Recovery proof scratch cleanup could not complete: {error.GetType().Name:@Failure}");
            return new ProofIncomplete("scratch_cleanup");
        }
        return proof;
    }
}
