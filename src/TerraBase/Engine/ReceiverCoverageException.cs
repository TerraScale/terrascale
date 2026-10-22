namespace TerraBase.Engine;

/// <summary>The receiver no longer holds a lower bound it previously acknowledged; a pinned refill is required.</summary>
public sealed class ReceiverCoverageException : IOException
{
    /// <summary>Creates a missing receiver coverage refusal.</summary>
    public ReceiverCoverageException() : base("the receiver requires a refill before advancing origin coverage")
    {
    }

    /// <summary>Creates a missing receiver coverage refusal with a message.</summary>
    public ReceiverCoverageException(string message) : base(message)
    {
    }

    /// <summary>Creates a missing receiver coverage refusal with its cause.</summary>
    public ReceiverCoverageException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
