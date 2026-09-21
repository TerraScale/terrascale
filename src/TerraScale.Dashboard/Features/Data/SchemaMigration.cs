namespace TerraScale.Dashboard.Features.Data;

public sealed record SchemaMigration(string State, int Completed, int Total, string? Watermark = null);
