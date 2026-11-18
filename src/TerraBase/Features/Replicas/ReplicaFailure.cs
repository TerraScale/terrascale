using System.Globalization;
using TerraBase.Engine;

namespace TerraBase.Features.Replicas;

internal sealed record ReplicaFailure(string Database, string Home, string Code)
{
    internal static ReplicaFailure From(string database, string home, Exception error) => new(database, home, SyncRefusals.Code(error));

    internal static (string Reason, KeyValuePair<string, string>[] Details) Describe(IReadOnlyList<ReplicaFailure> failures)
    {
        var first = failures[0];
        return ($"{failures.Count} replicas cannot be pulled from their home; region {first.Home} answers {first.Code} for database {first.Database}",
        [
            new("databases", failures.Count.ToString(CultureInfo.InvariantCulture)),
            new("database", first.Database),
            new("home", first.Home),
            new("homeCode", first.Code),
        ]);
    }
}
