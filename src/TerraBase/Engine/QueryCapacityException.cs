namespace TerraBase.Engine;

/// <summary>A query exceeded the engine's execution or sort-buffer budget.</summary>
public sealed class QueryCapacityException : Exception
{
    public QueryCapacityException(string message)
        : base(message)
    {
    }

    public QueryCapacityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Whether candidate sort admission, rather than execution, exceeded its budget.</summary>
    public bool SortBudget { get; init; }

    public bool Deterministic { get; init; }
}
