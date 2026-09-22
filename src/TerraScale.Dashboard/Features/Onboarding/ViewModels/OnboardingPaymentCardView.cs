namespace TerraScale.Dashboard.Features.Onboarding.ViewModels;

/// <summary>The "register a payment method" onboarding screen: the plan cards,
/// the monthly/annual switch, and the bypass/checkout actions. The action and
/// copy are supplied by the calling flow so both flows render the same card;
/// the new-organization flow hides the bypass because a Business organization
/// always carries its own paid billing.</summary>
public sealed record OnboardingPaymentCardView(
    string Action,
    string StepValue,
    string Subtitle,
    string SuccessBody,
    string Notice,
    IReadOnlyList<OnboardingPlanView> Plans,
    bool StepDone,
    bool AllowBypass);
