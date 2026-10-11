namespace TerraBase.Features.Qos;

/// <summary>Request memory admission failed before decoding or materializing further data.</summary>
public sealed class QosMemoryException : IOException
{
    /// <summary>Creates a refusal without exposing organization identities.</summary>
    public QosMemoryException() : base("organization memory budget exceeded") { }

    /// <summary>Creates a refusal with a public explanation.</summary>
    public QosMemoryException(string message) : base(message) { }

    /// <summary>Creates a refusal with its cause.</summary>
    public QosMemoryException(string message, Exception innerException) : base(message, innerException) { }
}
