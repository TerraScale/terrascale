namespace TerraScale.Dashboard.Features.Onboarding;

/// <summary>The "create the first project" onboarding screen: the project name
/// and the primary-region map. The action is supplied by the calling flow so
/// both flows render the same card.</summary>
public sealed record OnboardingProjectCardView(
    string Action,
    string StepValue,
    IReadOnlyList<OnboardingRegionView> Regions,
    string DefaultRegion);
