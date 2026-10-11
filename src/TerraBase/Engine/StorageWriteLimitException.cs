namespace TerraBase.Engine;

/// <summary>A storage write exceeded the document, identifier or transaction admission limit.</summary>
public sealed class StorageWriteLimitException : InvalidOperationException
{
    /// <summary>Creates a storage admission error.</summary>
    public StorageWriteLimitException()
        : base("a storage write exceeded its admission limit")
    {
    }

    /// <summary>Creates a storage admission error describing the exceeded limit.</summary>
    public StorageWriteLimitException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a storage admission error with its cause.</summary>
    public StorageWriteLimitException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
