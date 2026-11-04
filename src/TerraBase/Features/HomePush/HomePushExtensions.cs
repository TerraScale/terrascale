namespace TerraBase.Features.HomePush;

public static class HomePushExtensions
{
    public static IServiceCollection AddTerraBaseHomePush(this IServiceCollection services)
    {
        services.AddSingleton<HomePush>();
        services.AddSingleton<ILocalWrites>(provider => provider.GetRequiredService<HomePush>());
        return services.AddHostedService(provider => provider.GetRequiredService<HomePush>());
    }
}
