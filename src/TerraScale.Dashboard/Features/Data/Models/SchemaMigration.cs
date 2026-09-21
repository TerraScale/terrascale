namespace TerraScale.Dashboard.Features.Data.Models;

public sealed record SchemaMigration(string State, int Completed, int Total, string? Watermark = null);
