namespace TerraBase.Features.Management.Indexes;

/// <summary>Registers the index part of the Management slice.</summary>
public static class IndexesExtensions
{
    /// <summary>Registers <see cref="IIndexService"/>.</summary>
    public static IServiceCollection AddTerraBaseManagementIndexes(this IServiceCollection services) =>
        services.AddSingleton<IIndexService, IndexService>();
}
