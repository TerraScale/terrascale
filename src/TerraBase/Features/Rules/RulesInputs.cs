namespace TerraBase.Features.Rules;

public sealed record RulesRouteInput(string? Project, string? Database, string? Version, string? After, int? Limit);
