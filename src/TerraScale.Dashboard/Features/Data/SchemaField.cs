namespace TerraScale.Dashboard.Features.Data;

public sealed record SchemaField(string Name, string Type, bool Required, bool Indexed);
