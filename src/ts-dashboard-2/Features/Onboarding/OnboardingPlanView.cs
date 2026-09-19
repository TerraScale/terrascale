namespace TerraScale.Dashboard2.Features.Onboarding;

/// <summary>One checkout-selectable billing plan (v1 billing catalog entry:
/// monthly + annual base price, trial days, marketing copy).</summary>
public sealed record OnboardingPlanView(
    string PlanId,
    string Key,
    string Name,
    decimal MonthlyUsd,
    decimal AnnualUsd,
    int TrialDays,
    string Description,
    IReadOnlyList<string> Features);
