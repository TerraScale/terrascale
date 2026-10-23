using System.Net.Http.Headers;

namespace TerraBase.Features.UserAuth;

/// <summary>Allows an existing operator to present live ts-auth proof only on the marked recovery route.</summary>
internal sealed class OperatorAuthorityProof
{
    internal static string? Read(HttpRequest request) =>
        request.Headers.Authorization.Count == 1
        && AuthenticationHeaderValue.TryParse(request.Headers.Authorization[0], out var header)
        && string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
        && header.Parameter is { Length: > 0 and <= 8192 } token ? token : null;
}
