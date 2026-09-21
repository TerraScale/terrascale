namespace TerraScale.Dashboard.Features.Onboarding.ViewModels;

/// <summary>One selectable region pin on the project-step map. Codes are the
/// dashboard's gru|sfo|fra hubs; Country is the ISO country whose flag labels
/// the pin, LatencyMs is the demo round-trip shown on hover, and X/Y are
/// dotted-map units (136x80, tools/generate-region-map.mjs projection).</summary>
public sealed record OnboardingRegionView(
    string Code,
    string Name,
    string Country,
    int LatencyMs,
    bool Live,
    double X,
    double Y);
