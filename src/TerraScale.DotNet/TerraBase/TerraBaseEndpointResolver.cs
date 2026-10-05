namespace TerraScale.DotNet.TerraBase;

/// <summary>Resolves the TerraBase HTTPS client origin. Implementations must support concurrent callers.</summary>
public interface ITerraBaseEndpointResolver
{
    /// <summary>Returns an HTTPS origin without credentials, path, query or fragment, honoring cancellation.</summary>
    ValueTask<Uri> ResolveAsync(CancellationToken cancellationToken = default);
}

/// <summary>Resolves a caller-configured HTTPS origin, without discovery or background work.</summary>
public sealed class TerraBaseStaticEndpointResolver : ITerraBaseEndpointResolver
{
    private readonly Uri origin;

    /// <summary>Creates a resolver and validates the HTTPS client origin.</summary>
    public TerraBaseStaticEndpointResolver(Uri baseAddress) => origin = TerraBaseConnection.Origin(baseAddress);

    /// <inheritdoc />
    public ValueTask<Uri> ResolveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(origin);
    }
}
