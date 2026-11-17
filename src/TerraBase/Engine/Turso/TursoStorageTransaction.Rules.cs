using System.Text;
using TerraBase.Features.Rules;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageTransaction : IRulesWriter
{
    private const int KeptRulesVersions = 100;

    public ValueTask<RulesTestBody> TestRulesAsync(RulesTestRequest request, RulesSet rules, CancellationToken cancellationToken) =>
        RunAsync(() => RulesSimulator.Test(request, rules, ShapeReader), cancellationToken);

    public ValueTask<StoredRules?> ReadRulesAsync(string? version, CancellationToken cancellationToken) =>
        RunAsync(() => ShapeReader.Rules(version), cancellationToken);

    public ValueTask<RulesVersionPage?> ListRulesVersionsAsync(string? after, int limit, CancellationToken cancellationToken) =>
        RunAsync(() => ShapeReader.RulesVersions(after, limit), cancellationToken);

    public ValueTask<IReadOnlyList<RulesProblem>> CheckRulesAsync(RulesSet rules, CancellationToken cancellationToken) =>
        RunAsync(() => RulesChecks.Problems(rules, ShapeReader), cancellationToken);

    public async ValueTask WriteRulesAsync(StoredRules rules, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var body = TursoShapeReader.RulesBody(rules);
        await RunAsync(() =>
        {
            var next = Command("SELECT coalesce(max(end_offset), 0) + 1 FROM _tb_shape_chunk WHERE handle >= '~rules:v:' AND handle < '~rules:v;' AND offset = 0;");
            var sequence = (long)next.ExecuteScalar()!;
            var version = Command("INSERT INTO _tb_shape_chunk (handle, offset, end_offset, body, sealed_at) VALUES ($key, 0, $sequence, $body, $updated);");
            version.Parameters.AddWithValue("$key", TursoShapeReader.RulesVersionPrefix + rules.Version);
            version.Parameters.AddWithValue("$sequence", sequence);
            version.Parameters.AddWithValue("$body", body);
            version.Parameters.AddWithValue("$updated", rules.UpdatedAt.UtcTicks);
            version.ExecuteNonQuery();
            version.Parameters.Clear();
            var current = Command("""
                INSERT INTO _tb_shape_chunk (handle, offset, end_offset, body, sealed_at) VALUES ($key, 0, $sequence, $body, $updated)
                ON CONFLICT(handle, offset) DO UPDATE SET end_offset = excluded.end_offset, body = excluded.body, sealed_at = excluded.sealed_at;
                """);
            current.Parameters.AddWithValue("$key", TursoShapeReader.RulesCurrent);
            current.Parameters.AddWithValue("$sequence", sequence);
            current.Parameters.AddWithValue("$body", Encoding.ASCII.GetBytes(rules.Version));
            current.Parameters.AddWithValue("$updated", rules.UpdatedAt.UtcTicks);
            current.ExecuteNonQuery();
            current.Parameters.Clear();
            var prune = Command("DELETE FROM _tb_shape_chunk WHERE handle >= '~rules:v:' AND handle < '~rules:v;' AND offset = 0 AND end_offset <= $oldest;");
            prune.Parameters.AddWithValue("$oldest", sequence - KeptRulesVersions);
            prune.ExecuteNonQuery();
            prune.Parameters.Clear();
            return true;
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }
}
