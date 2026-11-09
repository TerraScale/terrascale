namespace TerraBase.Features.Qos;

public sealed class QosMemoryException : IOException
{
    public QosMemoryException() : base("organization memory budget exceeded") { }

    public QosMemoryException(string message) : base(message) { }

    public QosMemoryException(string message, Exception innerException) : base(message, innerException) { }
}
