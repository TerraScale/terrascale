namespace TerraScale.Dashboard.Features.Data.Models;

public sealed record IndexMigration(string State, int Completed, int Total, string? Watermark = null, string? Error = null);
