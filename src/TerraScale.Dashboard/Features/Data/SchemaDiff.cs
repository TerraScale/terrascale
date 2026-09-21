namespace TerraScale.Dashboard.Features.Data;

public sealed record SchemaDiff(SchemaDiffChange[] Changes, bool Breaking);
