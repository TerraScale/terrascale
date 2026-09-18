namespace TerraScale.Dashboard2.Features.Data;

public sealed record IndexMigration(string State, int Completed, int Total, string? Watermark = null, string? Error = null);
