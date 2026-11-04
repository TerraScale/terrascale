using System.Text.Json;
using TerraBase.Tenancy;

namespace TerraBase.Features.Qos;

internal static class PlanPricing
{
    private static readonly JsonElement Plans = Read();

    public static long Ceiling(PlanTier plan, string limit)
    {
        var id = plan switch
        {
            PlanTier.Hobby => "free",
            PlanTier.Starter => "starter",
            PlanTier.Pro => "pro",
            PlanTier.Team => "team",
            PlanTier.Enterprise => "enterprise",
            _ => throw new ArgumentOutOfRangeException(nameof(plan)),
        };
        var value = Plans.EnumerateArray().Single(entry => entry.GetProperty("id").GetString() == id).GetProperty("limits").GetProperty(limit);
        return value.ValueKind == JsonValueKind.Null ? long.MaxValue : value.GetInt64();
    }

    private static JsonElement Read()
    {
        using var stream = typeof(PlanPricing).Assembly.GetManifestResourceStream("TerraBase.pricing.json")
            ?? throw new InvalidOperationException("the embedded plan catalog is missing");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("plans").Clone();
    }
}
