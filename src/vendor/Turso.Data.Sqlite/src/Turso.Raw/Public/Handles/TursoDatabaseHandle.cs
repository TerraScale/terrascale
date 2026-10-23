using System.Runtime.InteropServices;

namespace Turso.Raw.Public.Handles;

public class TursoDatabaseHandle() : SafeHandle(IntPtr.Zero, true)
{
    private IntPtr _database;
    private SafeHandle? _owner;
    private bool _ownerReferenceAdded;
    private readonly Lock _statementsGate = new();
    private readonly HashSet<WeakReference<TursoStatementHandle>> _statements = [];
    private bool _deinitializeOnly;
    internal TursoInterop.FtsProgressCallback? FtsProgressHandler;

    /// <summary>The owning copy's current refusal, read immediately before releasing its native connection.</summary>
    public Func<string?>? CloseRefusal { get; set; }

    /// <summary>
    /// Releases a refused, quiescent connection and its statements without graceful close or checkpoint.
    /// The caller must have stopped all operations and finished or disposed every executing write statement.
    /// An uncommitted transaction left by failed rollback is abandoned; this is not an interrupt API.
    /// </summary>
    public void Deinitialize()
    {
        _deinitializeOnly = true;
        Dispose();
    }

    internal TursoStatementHandle OwnStatement(IntPtr pointer)
    {
        var statement = TursoStatementHandle.FromPtr(pointer);
        var registration = new WeakReference<TursoStatementHandle>(statement, trackResurrection: true);
        lock (_statementsGate)
        {
            statement.Owner = this;
            statement.Registration = registration;
            _statements.Add(registration);
        }

        return statement;
    }

    internal void ReleaseStatement(TursoStatementHandle statement, WeakReference<TursoStatementHandle> registration)
    {
        // Also serializes a finalizer's native release with this connection's terminal release.
        lock (_statementsGate)
        {
            statement.ReleaseNativeHandle();
            _statements.Remove(registration);
        }
    }

    protected override bool ReleaseHandle()
    {
        if (handle != IntPtr.Zero)
        {
            lock (_statementsGate)
            {
                if (_deinitializeOnly || CloseRefusal?.Invoke() is not null)
                {
                    _deinitializeOnly = true;
                    // A failed command Dispose must not leave a statement's Arc keeping the file open.
                    foreach (var registration in _statements.ToArray())
                    {
                        if (registration.TryGetTarget(out var statement))
                            statement.Deinitialize();
                    }
                    _statements.Clear();
                }
                else
                {
                    _ = TursoInterop.ConnectionClose(handle, out var errorPtr);
                    if (errorPtr != IntPtr.Zero)
                        TursoInterop.FreeString(errorPtr);
                }

                TursoInterop.ConnectionDeinit(handle);
                GC.KeepAlive(FtsProgressHandler);
                FtsProgressHandler = null;
            }
        }

        if (_database != IntPtr.Zero)
            TursoInterop.DatabaseDeinit(_database);

        if (_ownerReferenceAdded)
        {
            _owner!.DangerousRelease();
            _ownerReferenceAdded = false;
        }

        handle = IntPtr.Zero;
        _database = IntPtr.Zero;
        _owner = null;
        return true;
    }

    public void ThrowIfInvalid()
    {
        if (IsInvalid)
            throw new NullReferenceException("database is invalid");
    }

    public static TursoDatabaseHandle FromPtrs(IntPtr database, IntPtr connection)
    {
        var handle = new TursoDatabaseHandle();
        handle._database = database;
        handle.SetHandle(connection);
        return handle;
    }

    public static TursoDatabaseHandle FromConnectionPtr(IntPtr connection, SafeHandle owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (connection == IntPtr.Zero)
            throw new ArgumentException("Connection pointer must not be null.", nameof(connection));
        if (owner.IsInvalid || owner.IsClosed)
            throw new ObjectDisposedException(nameof(owner));

        var ownerReferenceAdded = false;
        owner.DangerousAddRef(ref ownerReferenceAdded);
        try
        {
            var result = new TursoDatabaseHandle
            {
                _owner = owner,
                _ownerReferenceAdded = ownerReferenceAdded,
            };
            result.SetHandle(connection);
            return result;
        }
        catch
        {
            if (ownerReferenceAdded)
                owner.DangerousRelease();
            throw;
        }
    }

    public override bool IsInvalid => handle == IntPtr.Zero || _database == IntPtr.Zero && _owner is null;
}
