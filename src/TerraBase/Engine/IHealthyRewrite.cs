using TerraBase.Tenancy;

namespace TerraBase.Engine;

internal interface IHealthyRewrite
{
    /// <summary>
    /// Rewrites <paramref name="database"/>, or SYSTEM when null. Copy and catch-up run beside normal operations; only the
    /// final catch-up and publication quiesce readers, writers, backup taps and their native handles.
    /// Returns only after the new file durably replaced the old one, the old log is gone and every native handle of the old
    /// file closed. Otherwise throws <see cref="RewriteRefusedException"/> (or <see cref="OperationCanceledException"/> while
    /// copying), and the old file stays in place and serves again: the database is missing, not healthy, does not drain
    /// before cancellation, the copy or a file step before the rename fails, or the copy does not reach
    /// <paramref name="minimumOffset"/>. <see cref="RewriteUncertainException"/> when the new file was renamed into place but
    /// that rename could not be made durable: the node fails closed (no writes) until a restart finds one whole file.
    /// </summary>
    /// <param name="database">The tenant database, or null for SYSTEM.</param>
    /// <param name="minimumOffset">The commit offset (<see cref="CommitResult.Offset"/>) the copied cut must include.</param>
    /// <param name="cancellationToken">Bounds the drain and the copy.</param>
    ValueTask<HealthyRewrite> RewriteAsync(TenantContext? database, long minimumOffset, CancellationToken cancellationToken);
}

/// <summary>A completed rewrite: what a per-holder receipt binds.</summary>
/// <param name="SourceIncarnation">The replaced file.</param>
/// <param name="TargetIncarnation">The file now in place.</param>
/// <param name="Offset">The commit offset of the copied cut; every commit at or below it is in the new file.</param>
internal readonly record struct HealthyRewrite(Ulid SourceIncarnation, Ulid TargetIncarnation, long Offset);

/// <summary>A rewrite did not happen; the old file stays in place and serves.</summary>
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

/// <summary>
/// The new file is in place but a power loss may bring the old one back: storage failed closed, and no receipt may be
/// recorded. After a restart the file in place is whole, of either incarnation.
/// </summary>
internal sealed class RewriteUncertainException(Exception innerException)
    : IOException("the rewritten file is in place but its rename may not be durable; storage failed closed", innerException);
