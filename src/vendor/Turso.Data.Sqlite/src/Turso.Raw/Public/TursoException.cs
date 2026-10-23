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

    // Native 0.8.2 reports authenticated-page failures as generic status 127.
    public bool IsPageAuthenticationFailure { get; internal init; }
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