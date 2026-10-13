namespace TerraBase.Engine;

/// <summary>
/// A replicated delta that cannot be joined as a delta: it is older than a deletion this machine may have collected, for a
/// document it no longer holds, and it never received it before (docs/terrabase/multi-region-replication.md section 11).
/// Only the document's whole state tells whether the delta loses to that deletion, so its sender ships the batch again as
/// whole states.
/// </summary>
public sealed class StaleDeltaException : InvalidOperationException
{
    /// <summary>Creates a stale delta error.</summary>
    public StaleDeltaException()
    {
    }

    /// <summary>Creates a stale delta error with a message.</summary>
    public StaleDeltaException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a stale delta error with a cause.</summary>
    public StaleDeltaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
