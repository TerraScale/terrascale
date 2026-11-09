namespace TerraBase.Engine;

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

    public bool SortBudget { get; init; }

    public bool Deterministic { get; init; }
}
