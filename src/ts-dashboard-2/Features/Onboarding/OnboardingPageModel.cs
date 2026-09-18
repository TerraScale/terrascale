using TerraScale.Dashboard2;

namespace TerraScale.Dashboard2.Features.Onboarding;

public sealed record OnboardingPageModel(
    IReadOnlyList<OnboardingStepView> Steps,
    string ActiveStep,
    int CurrentStep,
    int TotalSteps,
    int ProgressPercent,
    bool PaymentCanceled,
    IReadOnlyList<OnboardingPlanView> Plans,
    IReadOnlyList<string> Regions,
    string DisplayName,
    string Email,
    string? OrganizationName,
    bool PaymentValidated,
    bool Registration,
    string? RegistrationError) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}
