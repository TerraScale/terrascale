using TerraBase.Hosting;

namespace TerraBase.Features.Clock;

internal static class ClockExtensions
{
    public static IServiceCollection AddTerraBaseClock(this IServiceCollection services)
    {
        services.AddGrpc().AddServiceOptions<NodeClockService>(options =>
        {
            options.CompressionProviders = [new ClockCompression()];
            options.ResponseCompressionAlgorithm = "lz4";
            options.MaxReceiveMessageSize = 4096;
            options.MaxSendMessageSize = 4096;
        });
        services.AddSingleton<ClockGuard>();
        services.AddSingleton<ILocalWriteClock>(provider => provider.GetRequiredService<ClockGuard>());
        services.AddHostedService(provider => provider.GetRequiredService<ClockGuard>());
        return services;
    }

    public static void MapTerraBaseClock(this WebApplication app) =>
        app.MapGrpcService<NodeClockService>().WithMetadata(ListenerMetadata.Internal);
}
