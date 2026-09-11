using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TerraScale.EntityFrameworkCore;

/// <summary>EF model mapping helpers for TerraScale collections.</summary>
public static class TerraScaleEntityTypeBuilderExtensions
{
    internal const string CollectionAnnotationName = "TerraScale:CollectionName";
    /// <summary>Maps an entity type to a TerraScale collection.</summary>
    public static EntityTypeBuilder<TEntity> ToTerraScaleCollection<TEntity>(this EntityTypeBuilder<TEntity> builder, string collectionName) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        builder.HasAnnotation(CollectionAnnotationName, collectionName.Trim());
        return builder;
    }
}
