using TerraBase.Tenancy;

namespace TerraBase.Engine;

internal interface IHealthyRewrite
{
    ValueTask<HealthyRewrite> RewriteAsync(TenantContext? database, long minimumOffset, CancellationToken cancellationToken);
}

internal readonly record struct HealthyRewrite(Ulid SourceIncarnation, Ulid TargetIncarnation, long Offset);

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

internal sealed class RewriteRefusedException(string message, Exception? innerException = null, RewriteRefusal? reason = null)
    : IOException(message, innerException)
{
    internal RewriteRefusal Reason { get; } = reason ?? new Safety();
}

internal sealed class RewriteUncertainException(Exception innerException)
    : IOException("the rewritten file is in place but its rename may not be durable; storage failed closed", innerException);
