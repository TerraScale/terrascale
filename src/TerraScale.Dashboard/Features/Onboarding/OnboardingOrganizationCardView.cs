namespace TerraScale.Dashboard.Features.Onboarding;

/// <summary>The "create the organization" onboarding screen. The action and
/// copy are supplied by the calling flow, so the activation wizard and the
/// new-organization flow render the exact same card.</summary>
public sealed record OnboardingOrganizationCardView(
    string Action,
    string StepValue,
    string Title,
    string Subtitle,
    string ContinueHref,
    bool StepDone);
