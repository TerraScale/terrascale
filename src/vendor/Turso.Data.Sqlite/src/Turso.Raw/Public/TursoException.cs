namespace Turso.Raw.Public;

public class TursoException : Exception
{
    public TursoException(string message)
        : base(message)
    {
    }

    public TursoException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

// TerraScale: a failed local native call keeps its status code, so callers can tell corruption and I/O errors apart.
public sealed class TursoNativeException : TursoException
{
    public TursoNativeException(uint statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public uint StatusCode { get; }
}

public sealed class TursoSyncNativeException : TursoException
{
    public TursoSyncNativeException(uint statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public uint StatusCode { get; }
}