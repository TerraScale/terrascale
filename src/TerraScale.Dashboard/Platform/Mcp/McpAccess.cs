namespace TerraScale.Dashboard.Platform.Mcp;

public enum McpAccess
{
    Read,
    Write,
    Destructive,
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class McpAccessAttribute(McpAccess access) : Attribute
{
    public McpAccess Access { get; } = access;
}
