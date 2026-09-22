namespace TerraScale.Dashboard.Features.Onboarding.ViewModels;

/// <summary>The "create the organization" onboarding screen, rendered by the
/// new-organization flow's first step. The action and copy are supplied by the
/// calling flow, keeping it consistent with the other onboarding card views.</summary>
public sealed record OnboardingOrganizationCardView(
    string Action,
    string StepValue,
    string InputId,
    string Title,
    string Subtitle,
    string ContinueHref,
    bool StepDone);
