namespace TerraBase.Features.Management.Indexes;

/// <summary>Registers the index part of the Management slice.</summary>
public static class IndexesExtensions
{
    /// <summary>Registers <see cref="IIndexService"/> and the background vector index builder.</summary>
    public static IServiceCollection AddTerraBaseManagementIndexes(this IServiceCollection services) =>
        services.AddSingleton<IIndexService, IndexService>()
            .AddSingleton<VectorIndexBuilder>().AddHostedService(provider => provider.GetRequiredService<VectorIndexBuilder>());
}
