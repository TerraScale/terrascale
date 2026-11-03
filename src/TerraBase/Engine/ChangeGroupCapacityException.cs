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

    /// <summary>Creates a change-group capacity error naming the document whose encoded state crossed the budget.</summary>
    public ChangeGroupCapacityException(string message, string collection, string id)
        : base(message)
    {
        CapacityKey = (collection, id);
    }

    /// <summary>The document whose encoded state crossed the budget, when one write's own state crossed it.</summary>
    public (string Collection, string Id)? CapacityKey { get; }
}
