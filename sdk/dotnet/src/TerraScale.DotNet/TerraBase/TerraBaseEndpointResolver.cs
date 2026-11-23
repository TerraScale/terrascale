namespace TerraScale.DotNet.TerraBase;

public interface ITerraBaseEndpointResolver
{
    ValueTask<Uri> ResolveAsync(CancellationToken cancellationToken = default);
}

public sealed class TerraBaseStaticEndpointResolver : ITerraBaseEndpointResolver
{
    private readonly Uri origin;

    public TerraBaseStaticEndpointResolver(Uri baseAddress) => origin = TerraBaseConnection.Origin(baseAddress);

    public ValueTask<Uri> ResolveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(origin);
    }
}
