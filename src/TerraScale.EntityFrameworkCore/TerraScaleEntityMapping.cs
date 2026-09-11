using System.Globalization;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace TerraScale.EntityFrameworkCore;

internal sealed class TerraScaleEntityMapping
{
    private TerraScaleEntityMapping(IEntityType type, string database, string collection, IProperty key, PropertyInfo info) { EntityType = type; DatabaseName = database; CollectionName = collection; Key = key; KeyInfo = info; }
    public IEntityType EntityType { get; }
    public string DatabaseName { get; }
    public string CollectionName { get; }
    private IProperty Key { get; }
    private PropertyInfo KeyInfo { get; }
    public static TerraScaleEntityMapping Create(DbContext context, Type clrType, TerraScaleEntityFrameworkCoreOptions options)
    {
        var type = context.Model.FindEntityType(clrType) ?? throw new InvalidOperationException($"Entity type '{clrType.Name}' is not part of DbContext '{context.GetType().Name}'.");
        var primaryKey = type.FindPrimaryKey();
        if (primaryKey is null || primaryKey.Properties.Count != 1 || primaryKey.Properties[0].PropertyInfo is null) throw new InvalidOperationException($"Entity type '{type.DisplayName()}' must define exactly one CLR primary key property.");
        var database = options.DatabaseName;
        if (string.IsNullOrWhiteSpace(database)) throw new InvalidOperationException("TerraScale DatabaseName must be configured.");
        var annotation = type.FindAnnotation(TerraScaleEntityTypeBuilderExtensions.CollectionAnnotationName)?.Value as string;
        var collection = annotation ?? options.CollectionNameResolver?.Invoke(type) ?? type.ClrType.Name;
        if (string.IsNullOrWhiteSpace(collection)) throw new InvalidOperationException($"No TerraScale collection is configured for '{type.DisplayName()}'.");
        var key = primaryKey.Properties[0];
        return new(type, database.Trim(), collection.Trim(), key, key.PropertyInfo!);
    }
    public string GetDocumentId(object entity) { ArgumentNullException.ThrowIfNull(entity); return FormatKey(KeyInfo.GetValue(entity)); }
    public string FormatDocumentId(object value) { ArgumentNullException.ThrowIfNull(value); return FormatKey(value); }
    private string FormatKey(object? value)
    {
        if (value is null) throw new InvalidOperationException($"Entity type '{EntityType.DisplayName()}' key '{Key.Name}' cannot be null.");
        var result = value switch { string text => text.Trim(), Guid guid => guid.ToString("D"), IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture), _ => value.ToString() };
        if (string.IsNullOrWhiteSpace(result)) throw new InvalidOperationException($"Entity type '{EntityType.DisplayName()}' key '{Key.Name}' cannot be empty.");
        return result;
    }
}
