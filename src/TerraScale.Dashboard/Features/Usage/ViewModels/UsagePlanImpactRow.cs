namespace TerraScale.Dashboard.Features.Usage.ViewModels;

/// <summary>One row of the v1 plan-change impact table (usage page billing section).</summary>
public sealed record UsagePlanImpactRow(
    string PlanName,
    string Badge,        // Current plan | Within limits | Over current usage
    string BadgeTone,    // info | success | warning
    string Impact,
    string Effective);
