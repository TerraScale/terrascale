using TerraBase.Tenancy;

namespace TerraBase.Engine;

internal interface IHealthyRewrite
{
    ValueTask<HealthyRewrite> RewriteAsync(TenantContext? database, long minimumOffset, CancellationToken cancellationToken);
}

internal readonly record struct HealthyRewrite(Ulid SourceIncarnation, Ulid TargetIncarnation, long Offset);

internal closed record class RewriteRefusal
{
    private sealed record class SafetyCase : RewriteRefusal;
    private sealed record class BudgetCase : RewriteRefusal;
    private sealed record class DeadlineCase : RewriteRefusal;
    private sealed record class PressureCase : RewriteRefusal;

    internal static readonly RewriteRefusal Safety = new SafetyCase();
    internal static readonly RewriteRefusal Budget = new BudgetCase();
    internal static readonly RewriteRefusal Deadline = new DeadlineCase();
    internal static readonly RewriteRefusal Pressure = new PressureCase();

    public override string ToString() => GetType().Name[..^4];
}

internal sealed class RewriteRefusedException(string message, Exception? innerException = null, RewriteRefusal? reason = null)
    : IOException(message, innerException)
{
    internal RewriteRefusal Reason { get; } = reason ?? RewriteRefusal.Safety;
}

internal sealed class RewriteUncertainException(Exception innerException)
    : IOException("the rewritten file is in place but its rename may not be durable; storage failed closed", innerException);
