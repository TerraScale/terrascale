namespace TerraScale.Dashboard2.Features.Onboarding;

public sealed record OnboardingPlanView(string PlanId, string Name, decimal MonthlyUsd, bool IsFree, long RequestLimit);
