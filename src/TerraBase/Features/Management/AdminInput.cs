namespace TerraBase.Features.Management;

public sealed record AdminInput(string? Organization, string? Project, string? Plan, string? After, int? Limit);
