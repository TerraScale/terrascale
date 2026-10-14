using System.Runtime.InteropServices;

namespace Turso.Raw.Public.Handles;

public class TursoStatementHandle() : SafeHandle(IntPtr.Zero, true)
{
    private bool _deinitializeOnly;
    internal TursoDatabaseHandle? Owner { get; set; }
    internal WeakReference<TursoStatementHandle>? Registration { get; set; }

    internal void Deinitialize()
    {
        _deinitializeOnly = true;
        Dispose();
        // The owner holds its gate: finish a finalizer that already marked this SafeHandle closed but awaits that gate.
        ReleaseNativeHandle();
    }

    internal void ReleaseNativeHandle()
    {
        if (handle == IntPtr.Zero)
            return;
        if (!_deinitializeOnly)
        {
            _ = TursoInterop.StatementFinalize(handle, out var errorPtr);
            if (errorPtr != IntPtr.Zero)
                TursoInterop.FreeString(errorPtr);
        }

        TursoInterop.StatementDeinit(handle);
        handle = IntPtr.Zero;
    }

    protected override bool ReleaseHandle()
    {
        if (Owner is { } owner && Registration is { } registration)
            owner.ReleaseStatement(this, registration);
        else
            ReleaseNativeHandle();
        Owner = null;
        Registration = null;
        return true;
    }

    public void ThrowIfInvalid()
    {
        if (IsInvalid)
            throw new NullReferenceException("statement is invalid");
    }

    public static TursoStatementHandle FromPtr(IntPtr ptr)
    {
        var handle = new TursoStatementHandle();
        handle.SetHandle(ptr);
        return handle;
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

}
