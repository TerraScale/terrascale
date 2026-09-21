namespace TerraScale.Dashboard.Features.Onboarding;

/// <summary>The "register a payment method" onboarding screen: the plan cards,
/// the monthly/annual switch, and the bypass/checkout actions. The action is
/// supplied by the calling flow so both flows render the same card.</summary>
public sealed record OnboardingPaymentCardView(
    string Action,
    string StepValue,
    IReadOnlyList<OnboardingPlanView> Plans,
    bool StepDone);
