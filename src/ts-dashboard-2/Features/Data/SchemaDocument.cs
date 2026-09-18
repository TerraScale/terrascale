namespace TerraScale.Dashboard2.Features.Data;

public sealed record SchemaDocument(SchemaCollection[] Collections, int Version, string UpdatedAt, SchemaMigration? Migration = null);
