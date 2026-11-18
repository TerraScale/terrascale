using System.Globalization;
using System.Text.RegularExpressions;

namespace TerraBase.Features.Replicas;

internal sealed partial record ReplicaFailure(string Database, string Home, string Code)
{
    internal static ReplicaFailure From(string database, string home, Exception error) => new(database, home, error switch
    {
        _ when HomeCode().Match(error.Message) is { Success: true } answer => answer.Groups["code"].Value,
        HttpRequestException or HttpIOException or { InnerException: HttpRequestException or HttpIOException } => "unreachable",
        _ => error.GetType().Name,
    });

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

    [GeneratedRegex("""
        "code"\s*:\s*"(?<code>[a-z_]+)"
        """)]
    private static partial Regex HomeCode();
}
