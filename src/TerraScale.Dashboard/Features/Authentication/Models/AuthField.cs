namespace TerraScale.Dashboard.Features.Authentication.Models;

public sealed record AuthField(
    string Key,
    string Label,
    string Hint,
    string Type = "text",
    bool Required = true,
    string Default = "",
    string[]? Options = null);
