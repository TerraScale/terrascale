namespace TerraScale.TerraBase.Engine;

/// <summary>A query exceeded the engine's execution or sort-buffer budget.</summary>
public sealed class QueryCapacityException : Exception
{
    /// <summary>Whether candidate sort admission, rather than execution, exceeded its budget.</summary>
    public bool SortBudget { get; init; }

    /// <summary>Creates a query capacity error.</summary>
    public QueryCapacityException()
    {
    }

    /// <summary>Creates a query capacity error with a message.</summary>
    public QueryCapacityException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a query capacity error with a cause.</summary>
    public QueryCapacityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
