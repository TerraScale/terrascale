using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Features.Indexing;

/// <summary>A suggested secondary index over JSON paths.</summary>
/// <param name="Collection">Collection.</param>
/// <param name="Paths">JSON paths in key order, for example <c>$.email</c>.</param>
/// <param name="Reason">Why the advisor suggests it.</param>
public sealed record IndexRecommendation(string Collection, IReadOnlyList<string> Paths, string Reason);

/// <summary>Deterministic index decisions from the observed workload (see terrabase-rust docs/auto-index.md). Not implemented yet.</summary>
public interface IIndexAdvisor
{
    /// <summary>Returns recommendations for a database, most valuable first.</summary>
    ValueTask<IReadOnlyList<IndexRecommendation>> RecommendAsync(TenantContext database, CancellationToken cancellationToken);
}
