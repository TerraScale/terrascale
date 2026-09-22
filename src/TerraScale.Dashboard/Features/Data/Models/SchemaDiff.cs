namespace TerraScale.Dashboard.Features.Data.Models;

public sealed record SchemaDiff(SchemaDiffChange[] Changes, bool Breaking);
