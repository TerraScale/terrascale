using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TerraScale.EntityFrameworkCore;

/// <summary>
/// Entity model configuration helpers for TerraScale repositories.
/// </summary>
public static class TerraScaleEntityTypeBuilderExtensions
{
    internal const string RepositoryAnnotationName = "TerraScale:RepositoryName";

    /// <summary>
    /// Maps an EF Core entity type to a TerraScale repository.
    /// </summary>
    public static EntityTypeBuilder<TEntity> ToTerraScaleRepository<TEntity>(
        this EntityTypeBuilder<TEntity> builder,
        string repositoryName)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryName);

        builder.HasAnnotation(RepositoryAnnotationName, repositoryName.Trim());
        return builder;
    }
}
