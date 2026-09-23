namespace TerraScale.Dashboard.Features.Onboarding.ViewModels;

/// <summary>One checkout-selectable billing plan (v1 billing catalog entry:
/// monthly + annual base price, trial days, marketing copy). The Key is what
/// the payment screen posts; the flow maps it to a billable store plan id.</summary>
public sealed record OnboardingPlanView(
    string Key,
    string Name,
    decimal MonthlyUsd,
    decimal AnnualUsd,
    int TrialDays,
    string Description,
    IReadOnlyList<string> Features);
