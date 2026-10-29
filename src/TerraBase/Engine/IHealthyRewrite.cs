using TerraBase.Tenancy;

namespace TerraBase.Engine;

internal interface IHealthyRewrite
{
    ValueTask<HealthyRewrite> RewriteAsync(TenantContext? database, long minimumOffset, CancellationToken cancellationToken);
}

internal readonly record struct HealthyRewrite(Ulid SourceIncarnation, Ulid TargetIncarnation, long Offset);

internal enum RewriteRefusal
{
    Safety,
    Budget,
    Deadline,
    Pressure,
}

internal sealed class RewriteRefusedException(string message, Exception? innerException = null, RewriteRefusal reason = RewriteRefusal.Safety)
    : IOException(message, innerException)
{
    internal RewriteRefusal Reason { get; } = reason;
}

internal sealed class RewriteUncertainException(Exception innerException)
    : IOException("the rewritten file is in place but its rename may not be durable; storage failed closed", innerException);
