using TerraBase.Features.Sql.Routing;

namespace TerraBase.Features.HomePush;

public static class HomePushExtensions
{
    public static IServiceCollection AddTerraBaseHomePush(this IServiceCollection services)
    {
        services.AddSingleton<ITransactionCrypto>(provider => new TransactionCrypto(provider.GetService<Backup.DataKeys>()));
        services.AddSingleton<HomePush>();
        services.AddSingleton<ILocalWrites>(provider => provider.GetRequiredService<HomePush>());
        services.AddSingleton<ILocalCommitPath>(provider => provider.GetRequiredService<HomePush>());
        return services.AddHostedService(provider => provider.GetRequiredService<HomePush>());
    }
}
