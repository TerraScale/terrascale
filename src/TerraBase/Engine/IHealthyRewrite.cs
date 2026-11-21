using TerraBase.Tenancy;

namespace TerraBase.Engine;

internal interface IHealthyRewrite
{
    ValueTask<HealthyRewriteOutcome> RewriteAsync(TenantContext? database, long minimumOffset, CancellationToken cancellationToken);
}

internal readonly record struct HealthyRewrite(Ulid SourceIncarnation, Ulid TargetIncarnation, long Offset);

internal readonly union HealthyRewriteOutcome(HealthyRewrite, RewriteRefused, RewriteUncertain);

internal sealed record class RewriteUncertain(Exception Cause);

internal closed record class RewriteRefusal;
internal sealed record class Safety : RewriteRefusal;
internal sealed record class Budget : RewriteRefusal;
internal sealed record class Deadline : RewriteRefusal;
internal sealed record class Pressure : RewriteRefusal;

internal static class RewriteRefusalExtensions
{
    extension(RewriteRefusal refusal)
    {
        public string Label => refusal switch
        {
            Safety => "Safety",
            Budget => "Budget",
            Deadline => "Deadline",
            Pressure => "Pressure",
        };
    }
}

internal sealed record RewriteRefused(string Message, RewriteRefusal Reason);
