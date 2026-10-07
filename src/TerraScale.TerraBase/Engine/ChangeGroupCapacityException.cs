namespace TerraBase.Engine;

/// <summary>A write transaction exceeded its bounded replication change group.</summary>
public sealed class ChangeGroupCapacityException : InvalidOperationException
{
    /// <summary>Creates a change-group capacity error.</summary>
    public ChangeGroupCapacityException()
    {
    }

    /// <summary>Creates a change-group capacity error with a message.</summary>
    public ChangeGroupCapacityException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a change-group capacity error with a cause.</summary>
    public ChangeGroupCapacityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
