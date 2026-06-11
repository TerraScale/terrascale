using System.Globalization;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace TerraScale.EntityFrameworkCore;

internal sealed class TerraScaleEntityMapping
{
    private TerraScaleEntityMapping(
        IEntityType entityType,
        string repositoryName,
        IProperty keyProperty,
        PropertyInfo keyPropertyInfo)
    {
        EntityType = entityType;
        RepositoryName = repositoryName;
        KeyProperty = keyProperty;
        KeyPropertyInfo = keyPropertyInfo;
    }

    public IEntityType EntityType { get; }

    public string RepositoryName { get; }

    private IProperty KeyProperty { get; }

    private PropertyInfo KeyPropertyInfo { get; }

    public static TerraScaleEntityMapping Create(
        DbContext context,
        Type entityClrType,
        TerraScaleEntityFrameworkCoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entityClrType);
        ArgumentNullException.ThrowIfNull(options);

        var entityType = context.Model.FindEntityType(entityClrType)
            ?? throw new InvalidOperationException(
                $"Entity type '{entityClrType.Name}' is not part of DbContext '{context.GetType().Name}'.");

        var key = entityType.FindPrimaryKey();
        if (key is null || key.Properties.Count != 1)
        {
            throw new InvalidOperationException(
                $"Entity type '{entityType.DisplayName()}' must define exactly one primary key property for TerraScale document ids.");
        }

        var keyProperty = key.Properties[0];
        if (keyProperty.PropertyInfo is null)
        {
            throw new InvalidOperationException(
                $"Entity type '{entityType.DisplayName()}' key '{keyProperty.Name}' must be a CLR property.");
        }

        return new TerraScaleEntityMapping(
            entityType,
            ResolveRepositoryName(entityType, options),
            keyProperty,
            keyProperty.PropertyInfo);
    }

    public string GetDocumentId(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return FormatDocumentIdCore(KeyPropertyInfo.GetValue(entity));
    }

    public string FormatDocumentId(object key) => FormatDocumentIdCore(key);

    private static string ResolveRepositoryName(
        IEntityType entityType,
        TerraScaleEntityFrameworkCoreOptions options)
    {
        if (entityType.FindAnnotation(TerraScaleEntityTypeBuilderExtensions.RepositoryAnnotationName)?.Value is string annotation &&
            !string.IsNullOrWhiteSpace(annotation))
        {
            return annotation.Trim();
        }

        var resolved = options.RepositoryNameResolver?.Invoke(entityType);
        return string.IsNullOrWhiteSpace(resolved) ? entityType.ClrType.Name : resolved.Trim();
    }

    private string FormatDocumentIdCore(object? key)
    {
        if (key is null)
        {
            throw new InvalidOperationException(
                $"Entity type '{EntityType.DisplayName()}' key '{KeyProperty.Name}' cannot be null when used as a TerraScale document id.");
        }

        var documentId = key switch
        {
            string value => value.Trim(),
            Guid value => value.ToString("D"),
            IFormattable value => value.ToString(format: null, CultureInfo.InvariantCulture),
            _ => key.ToString()
        };

        if (string.IsNullOrWhiteSpace(documentId))
        {
            throw new InvalidOperationException(
                $"Entity type '{EntityType.DisplayName()}' key '{KeyProperty.Name}' cannot be empty when used as a TerraScale document id.");
        }

        return documentId;
    }
}
