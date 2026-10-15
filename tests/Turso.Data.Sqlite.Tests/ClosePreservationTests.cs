using System.Data;
using System.Reflection;
using System.Runtime.CompilerServices;
using Turso.Raw.Public.Handles;

namespace Turso.Data.Sqlite.Tests;

/// <summary>Refused local closes preserve both files and release native ownership; healthy closes still checkpoint.</summary>
public sealed class ClosePreservationTests
{
    /// <summary>Healthy close checkpoints; a refusal published after open preserves the files and releases their handles.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ClosingAFinalizedWriterUsesTheCurrentCopyRefusal(bool refused)
    {
        var directory = Directory.CreateTempSubdirectory("turso-close-").FullName;
        try
        {
            var path = Path.Combine(directory, "t.db");
            string? refusal = null;
            using var connection = new SqliteConnection($"Data Source={path};Pooling=False")
            {
                CloseRefusal = () => refusal,
            };
            OpenWithoutRetainingTheSchemaConnection(connection, path);
            CreateNonemptyWal(connection);
            await AssertCommittedRowIsVisibleToAnotherConnection(path);
            var main = File.ReadAllBytes(path);
            var wal = File.ReadAllBytes(path + "-wal");
            await Assert.That(wal.Length).IsGreaterThan(32);
            // Opened healthy: close must read the live refusal, not the value at open.
            refusal = refused ? "this copy is faulted" : null;
            connection.Close();
            await Assert.That(connection.State).IsEqualTo(ConnectionState.Closed);
            if (refused)
            {
                await Assert.That(File.ReadAllBytes(path).SequenceEqual(main)).IsTrue();
                await Assert.That(File.ReadAllBytes(path + "-wal").SequenceEqual(wal)).IsTrue();
            }
            else
            {
                await Assert.That(File.ReadAllBytes(path).SequenceEqual(main)).IsFalse();
                await Assert.That(File.ReadAllBytes(path + "-wal").Length).IsEqualTo(0);
            }
            if (OperatingSystem.IsLinux())
            {
                await Assert.That(OpenHandles(path)).IsEqualTo(0);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Terminal native cleanup releases prepared statements even when provider disposal throws.</summary>
    [Test]
    public async Task AFailedProviderDisposeCannotGracefullyCloseTheDeinitializedHandle()
    {
        var directory = Directory.CreateTempSubdirectory("turso-close-").FullName;
        try
        {
            var path = Path.Combine(directory, "t.db");
            using var connection = new FailingCloseConnection($"Data Source={path};Pooling=False");
            OpenWithoutRetainingTheSchemaConnection(connection, path);
            CreateNonemptyWal(connection);
            await AssertCommittedRowIsVisibleToAnotherConnection(path);
            // Deliberately remains prepared when the provider's Dispose throws: its native Arc must be released too.
            using var prepared = connection.CreateCommand();
            prepared.CommandText = "SELECT 1;";
            prepared.Prepare();
            var native = (TursoDatabaseHandle)(typeof(SqliteConnection)
                .GetField("_database", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(connection) ?? throw new InvalidOperationException("the native handle is missing"));
            var main = File.ReadAllBytes(path);
            var wal = File.ReadAllBytes(path + "-wal");
            await Assert.That(wal.Length).IsGreaterThan(32);
            native.Deinitialize();
            connection.FailNextClose();
            _ = Assert.Throws<InvalidOperationException>(() =>
            {
                try
                {
                    connection.Dispose();
                }
                finally
                {
                    // The session's fallback: must remain a deinit-only no-op, never graceful close.
                    native.Dispose();
                }
            });
            await Assert.That(native.IsClosed).IsTrue();
            await Assert.That(File.ReadAllBytes(path).SequenceEqual(main)).IsTrue();
            await Assert.That(File.ReadAllBytes(path + "-wal").SequenceEqual(wal)).IsTrue();
            if (OperatingSystem.IsLinux())
            {
                await Assert.That(OpenHandles(path)).IsEqualTo(0);
            }
            connection.Close();
            await Assert.That(connection.State).IsEqualTo(ConnectionState.Closed);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Refused teardown finishes native release while an already-closed statement waits on its owner gate.</summary>
    [Test]
    public async Task RefusedTeardownReleasesAStatementWhoseDisposeIsWaitingOnTheOwnerGate()
    {
        var directory = Directory.CreateTempSubdirectory("turso-close-").FullName;
        try
        {
            var path = Path.Combine(directory, "t.db");
            using var connection = new SqliteConnection($"Data Source={path};Pooling=False")
            {
                CloseRefusal = () => "this copy is faulted",
            };
            OpenWithoutRetainingTheSchemaConnection(connection, path);
            CreateNonemptyWal(connection);
            await AssertCommittedRowIsVisibleToAnotherConnection(path);
            using var prepared = connection.CreateCommand();
            prepared.CommandText = "SELECT 1;";
            prepared.Prepare();
            var native = PrivateField<TursoDatabaseHandle>(connection, "_database");
            var statement = PrivateField<TursoStatementHandle>(prepared, "_statement");
            var gate = PrivateField<Lock>(native, "_statementsGate");
            var main = File.ReadAllBytes(path);
            var wal = File.ReadAllBytes(path + "-wal");
            await Assert.That(wal.Length).IsGreaterThan(32);

            Task release;
            bool closedWhileBlocked;
            bool workerWasBlocked;
            bool releasedWhileBlocked;
            int handlesWhileBlocked;
            // EnterScope stays on this thread; terminal teardown reenters the same owner gate.
            using (gate.EnterScope())
            {
                release = Task.Run(statement.Dispose);
                closedWhileBlocked = SpinWait.SpinUntil(() => statement.IsClosed, TimeSpan.FromSeconds(10));
                workerWasBlocked = !release.IsCompleted;
                native.Deinitialize();
                releasedWhileBlocked = statement.IsInvalid;
                handlesWhileBlocked = OperatingSystem.IsLinux() ? OpenHandles(path) : 0;
            }
            await release.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(closedWhileBlocked).IsTrue();
            await Assert.That(workerWasBlocked).IsTrue();
            await Assert.That(releasedWhileBlocked).IsTrue();
            await Assert.That(native.IsClosed).IsTrue();

            statement.Dispose();
            native.Deinitialize();
            native.Dispose();
            prepared.Dispose();
            prepared.Dispose();
            connection.Dispose();
            connection.Dispose();
            await Assert.That(connection.State).IsEqualTo(ConnectionState.Closed);
            await Assert.That(File.ReadAllBytes(path).SequenceEqual(main)).IsTrue();
            await Assert.That(File.ReadAllBytes(path + "-wal").SequenceEqual(wal)).IsTrue();
            if (OperatingSystem.IsLinux())
            {
                await Assert.That(handlesWhileBlocked).IsEqualTo(0);
                await Assert.That(OpenHandles(path)).IsEqualTo(0);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>The owner's long weak registration reaches an abandoned statement after its short weak reference clears.</summary>
    [Test]
    public async Task RefusedTeardownReleasesAnAbandonedStatementWaitingInItsFinalizer()
    {
        var directory = Directory.CreateTempSubdirectory("turso-close-").FullName;
        try
        {
            var path = Path.Combine(directory, "t.db");
            using var connection = new SqliteConnection($"Data Source={path};Pooling=False")
            {
                CloseRefusal = () => "this copy is faulted",
            };
            OpenWithoutRetainingTheSchemaConnection(connection, path);
            CreateNonemptyWal(connection);
            await AssertCommittedRowIsVisibleToAnotherConnection(path);
            var native = PrivateField<TursoDatabaseHandle>(connection, "_database");
            var gate = PrivateField<Lock>(native, "_statementsGate");
            var main = File.ReadAllBytes(path);
            var wal = File.ReadAllBytes(path + "-wal");
            await Assert.That(wal.Length).IsGreaterThan(32);

            bool shortReferenceCleared;
            bool finalizerWasBlocked;
            bool releasedWhileBlocked;
            int handlesWhileBlocked;
            using (gate.EnterScope())
            {
                var (shortReference, registered) = AbandonPreparedSelect(connection, native);
                GC.Collect();
                shortReferenceCleared = !shortReference.TryGetTarget(out _);
                finalizerWasBlocked = SpinWait.SpinUntil(
                    () => registered.TryGetTarget(out var statement) && statement.IsClosed,
                    TimeSpan.FromSeconds(10));
                native.Deinitialize();
                releasedWhileBlocked = registered.TryGetTarget(out var released) && released.IsInvalid;
                handlesWhileBlocked = OperatingSystem.IsLinux() ? OpenHandles(path) : 0;
            }
            await Task.Run(GC.WaitForPendingFinalizers).WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(shortReferenceCleared).IsTrue();
            await Assert.That(finalizerWasBlocked).IsTrue();
            await Assert.That(releasedWhileBlocked).IsTrue();
            await Assert.That(native.IsClosed).IsTrue();

            native.Deinitialize();
            native.Dispose();
            connection.Dispose();
            connection.Dispose();
            await Assert.That(connection.State).IsEqualTo(ConnectionState.Closed);
            await Assert.That(File.ReadAllBytes(path).SequenceEqual(main)).IsTrue();
            await Assert.That(File.ReadAllBytes(path + "-wal").SequenceEqual(wal)).IsTrue();
            if (OperatingSystem.IsLinux())
            {
                await Assert.That(handlesWhileBlocked).IsEqualTo(0);
                await Assert.That(OpenHandles(path)).IsEqualTo(0);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static T PrivateField<T>(object instance, string name) =>
        (T)(instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(instance) ?? throw new InvalidOperationException($"the {name} field is missing"));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<TursoStatementHandle> Short, WeakReference<TursoStatementHandle> Registered)
        AbandonPreparedSelect(SqliteConnection connection, TursoDatabaseHandle native)
    {
        // A separate non-inlined frame leaves neither the command nor its statement rooted during collection.
        var prepared = connection.CreateCommand();
        prepared.CommandText = "SELECT 1;";
        prepared.Prepare();
        var statement = PrivateField<TursoStatementHandle>(prepared, "_statement");
        return (new WeakReference<TursoStatementHandle>(statement),
            PrivateField<HashSet<WeakReference<TursoStatementHandle>>>(native, "_statements").Single());
    }

    private static void OpenWithoutRetainingTheSchemaConnection(SqliteConnection connection, string path)
    {
        // Native 0.8.2 retains the first opener's schema connection; release it before testing the last close.
        using var schemaOwner = new SqliteConnection($"Data Source={path};Pooling=False");
        schemaOwner.Open();
        connection.Open();
    }

    private static async Task AssertCommittedRowIsVisibleToAnotherConnection(string path)
    {
        using var observer = new SqliteConnection($"Data Source={path};Pooling=False");
        observer.Open();
        using var command = observer.CreateCommand();
        command.CommandText = "SELECT id FROM t;";
        using var rows = command.ExecuteReader();
        await Assert.That(rows.Read()).IsTrue();
        await Assert.That(rows.GetInt64(0)).IsEqualTo(1);
        await Assert.That(rows.Read()).IsFalse();
    }

    private static void CreateNonemptyWal(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY);";
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        command.ExecuteNonQuery();
        using var transaction = connection.BeginTransaction();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO t VALUES (1);";
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static int OpenHandles(string path)
    {
        var count = 0;
        foreach (var descriptor in Directory.EnumerateFiles("/proc/self/fd"))
        {
            try
            {
                var target = new FileInfo(descriptor).LinkTarget;
                if (target == path || target == path + "-wal")
                {
                    count++;
                }
            }
            catch (IOException)
            {
                // A descriptor owned by a different parallel test closed between enumeration and readlink.
            }
        }
        return count;
    }

    private sealed class FailingCloseConnection(string connectionString) : SqliteConnection(connectionString)
    {
        private bool _fail;

        internal void FailNextClose() => _fail = true;

        public override void Close()
        {
            if (_fail)
            {
                _fail = false;
                throw new InvalidOperationException("provider close failed before releasing its native handle");
            }
            base.Close();
        }
    }
}
