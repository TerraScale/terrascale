namespace TerraBase.Features.Management.Schema;

/// <summary>Registers the collection and schema views.</summary>
public static class SchemaExtensions
{
    /// <summary>Registers <see cref="ISchemaService"/>.</summary>
    public static IServiceCollection AddTerraBaseSchema(this IServiceCollection services) =>
        services.AddSingleton<ISchemaService, SchemaService>();
}
