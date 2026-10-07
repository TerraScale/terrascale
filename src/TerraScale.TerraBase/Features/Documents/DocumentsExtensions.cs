using TerraBase.Configuration;

namespace TerraBase.Features.Documents;

/// <summary>Registers the Documents slice.</summary>
public static class DocumentsExtensions
{
    /// <summary>
    /// Registers <see cref="IDocumentService"/>, its read cache and <see cref="IShapeService"/>; under <c>core=anna</c>,
    /// documents go to the per-core actors and shapes answer 503 until the disk tier ships.
    /// </summary>
    public static IServiceCollection AddTerraBaseDocuments(this IServiceCollection services, TerraBaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.AddMetrics().AddSingleton<DocumentCache>().AddSingleton<DocumentService>()
            .AddSingleton<ISystemDocumentReader>(provider => provider.GetRequiredService<DocumentService>());
        return options.Core is StorageCore.Anna
            ? services.AddSingleton<IDocumentService, AnnaDocumentService>().AddSingleton<IShapeService, AnnaShapeService>()
            : services.AddSingleton<IDocumentService>(provider => provider.GetRequiredService<DocumentService>()).AddSingleton<IShapeService, ShapeService>();
    }
}
