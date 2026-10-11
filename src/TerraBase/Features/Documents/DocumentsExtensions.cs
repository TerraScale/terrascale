namespace TerraBase.Features.Documents;

/// <summary>Registers the Documents slice.</summary>
public static class DocumentsExtensions
{
    /// <summary>Registers <see cref="IDocumentService"/>, its read cache and <see cref="IShapeService"/>.</summary>
    public static IServiceCollection AddTerraBaseDocuments(this IServiceCollection services) =>
        services.AddMetrics().AddSingleton<DocumentCache>().AddSingleton<DocumentService>()
            .AddSingleton<IDocumentCacheQuota>(provider => provider.GetRequiredService<DocumentCache>())
            .AddSingleton<IDocumentService>(provider => provider.GetRequiredService<DocumentService>())
            .AddSingleton<ISystemDocumentReader>(provider => provider.GetRequiredService<DocumentService>())
            .AddSingleton<IShapeService, ShapeService>()
            .AddSingleton<IShapeSubscriptionService, ShapeSubscriptionService>()
            .AddSingleton<IChangeFeedService, ChangeFeedService>();
}
