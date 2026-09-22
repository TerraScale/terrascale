using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.Onboarding.Pages.Onboarding;

public sealed record OnboardingPageModel(
    IReadOnlyList<OnboardingStepView> Steps,
    string ActiveStep,
    int CurrentStep,
    int TotalSteps,
    int ProgressPercent,
    bool PaymentCanceled,
    IReadOnlyList<OnboardingPlanView> Plans,
    IReadOnlyList<OnboardingRegionView> Regions,
    string DisplayName,
    string Email,
    bool Registration,
    string? RegistrationError) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}
