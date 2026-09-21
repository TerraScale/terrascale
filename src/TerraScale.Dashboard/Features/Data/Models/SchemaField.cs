namespace TerraScale.Dashboard.Features.Data.Models;

public sealed record SchemaField(string Name, string Type, bool Required, bool Indexed);
