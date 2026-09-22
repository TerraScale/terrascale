namespace TerraScale.Dashboard.Features.Onboarding.ViewModels;

/// <summary>The shared onboarding progress rail: the meta line, the progress
/// bar, and the step list. Rendered by both the activation wizard and the
/// new-organization flow so the step screens stay identical.</summary>
public sealed record OnboardingProgressView(
    IReadOnlyList<OnboardingStepView> Steps,
    string ActiveStep,
    int CurrentStep,
    int TotalSteps,
    int ProgressPercent,
    string Label,
    bool Compact,
    string AriaLabel,
    string BarAriaLabel);
