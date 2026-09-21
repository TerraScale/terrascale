namespace TerraScale.Dashboard.Features.Data.Models;

public sealed record SchemaDocument(SchemaCollection[] Collections, int Version, string UpdatedAt, SchemaMigration? Migration = null);
