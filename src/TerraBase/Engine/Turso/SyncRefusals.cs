using System.Globalization;
using System.Text.RegularExpressions;
using Turso;

namespace TerraBase.Engine;

internal static partial class SyncRefusals
{
    internal static string Code(Exception error) => error switch
    {
        TursoSyncException sync when PeerCode().Match(sync.Message) is { Success: true } answer => answer.Groups["code"].Value,
        TursoSyncException { HttpStatusCode: { } status } => "http_" + ((int)status).ToString(CultureInfo.InvariantCulture),
        HttpRequestException or { InnerException: HttpRequestException } => "unreachable",
        _ => error.GetType().Name,
    };

    [GeneratedRegex("""
        "code"\s*:\s*"(?<code>[a-z_]+)"
        """)]
    private static partial Regex PeerCode();
}
