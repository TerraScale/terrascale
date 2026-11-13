namespace TerraBase.Features.Clock;

internal static class ClockExtensions
{
    public static IServiceCollection AddTerraBaseClock(this IServiceCollection services)
    {
        services.AddSingleton<ClockTrust>();
        services.AddHostedService(provider => provider.GetRequiredService<ClockTrust>());
        return services;
    }
}
