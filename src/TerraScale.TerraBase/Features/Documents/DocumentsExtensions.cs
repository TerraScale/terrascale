namespace TerraScale.TerraBase.Features.Documents;

/// <summary>Registers the Documents slice.</summary>
public static class DocumentsExtensions
{
    /// <summary>Registers <see cref="IDocumentService"/> and <see cref="IShapeService"/>.</summary>
    public static IServiceCollection AddTerraBaseDocuments(this IServiceCollection services) =>
        services.AddSingleton<IDocumentService, DocumentService>()
            .AddSingleton<IShapeService, ShapeService>();
}
