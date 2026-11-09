using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json.Serialization;

namespace TerraBase.Tenancy;

internal static class PlanTierExtensions
{
    private static readonly FrozenDictionary<PlanTier, string> JsonNames = Enum.GetValues<PlanTier>().ToFrozenDictionary(
        plan => plan,
        plan => typeof(PlanTier).GetField(plan.ToString()) is { } field
            && field.GetCustomAttribute<JsonStringEnumMemberNameAttribute>() is { Name: { } name }
            ? name
            : throw new InvalidOperationException("every plan tier needs a JSON name"));

    extension(PlanTier plan)
    {
        public string JsonName => JsonNames[plan];
    }
}
