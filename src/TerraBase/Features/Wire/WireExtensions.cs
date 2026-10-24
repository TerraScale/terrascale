namespace TerraBase.Features.Wire;

internal static class WireExtensions
{
    /// <summary>The wire protocol and its listener (docs/terrabase/wire-protocol.md).</summary>
    public static IServiceCollection AddTerraBaseWire(this IServiceCollection services) =>
        services
            .AddSingleton<WireConnectionHandler>()
            .AddSingleton<WireListener>()
            .AddHostedService(provider => provider.GetRequiredService<WireListener>());
}
