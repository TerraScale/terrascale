namespace TerraScale.Dashboard2.Features.Onboarding;

/// <summary>One selectable region pin on the project-step map. X/Y are
/// dotted-map units (136x80, tools/generate-region-map.mjs projection).</summary>
public sealed record OnboardingRegionView(string Code, string Name, bool Live, double X, double Y);
