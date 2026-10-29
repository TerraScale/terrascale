using System.Net;

namespace TerraScale.DotNet.TerraBase;

/// <summary>An all-holder account intent cancellation; pending and committed never prove cancellation.</summary>
public sealed record TerraBaseClosureCancellation(string Generation, string Status, int PendingHolders);

internal sealed record ClosureCancellationBody(int Version, string TargetId);

public sealed partial class TerraBaseDatabase
{
    /// <summary>
    /// Installs a permanent generation fence on every catalog holder, over the internal peer listener. Returns cancelled
    /// only after every holder acknowledged and none reported irreversible work. Retry the same generation and target.
    /// </summary>
    public async Task<TerraBaseClosureCancellation> CancelClosureAsync(string generation, string targetId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetId);
        var body = TerraBaseConnection.Json(new ClosureCancellationBody(1, targetId), Json.ClosureCancellationBody);
        var (answer, status) = await connection.SendAsync(HttpMethod.Put, "/admin" + path[3..] + "/closure-cancellations/"
            + TerraBaseConnection.Segment(generation, nameof(generation)), body, Json.TerraBaseClosureCancellation, null, cancellationToken).ConfigureAwait(false);
        return answer.Generation == generation && answer.PendingHolders >= 0
            && ((status == HttpStatusCode.Accepted && answer.Status == "pending" && answer.PendingHolders > 0)
                || (status == HttpStatusCode.OK && answer.Status is "cancelled" or "committed" && answer.PendingHolders == 0))
            ? answer
            : throw new TerraBaseException(status, "TerraBase answered a cancellation with a mismatched generation or status.", "invalid_response", null);
    }
}
