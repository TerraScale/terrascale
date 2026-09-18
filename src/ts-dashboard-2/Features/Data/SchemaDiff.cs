namespace TerraScale.Dashboard2.Features.Data;

public sealed record SchemaDiff(SchemaDiffChange[] Changes, bool Breaking);
