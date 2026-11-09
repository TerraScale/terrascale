namespace TerraBase.Engine;

public sealed class StorageWriteLimitException : InvalidOperationException
{
    public StorageWriteLimitException()
        : base("a storage write exceeded its admission limit")
    {
    }

    public StorageWriteLimitException(string message)
        : base(message)
    {
    }

    public StorageWriteLimitException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
