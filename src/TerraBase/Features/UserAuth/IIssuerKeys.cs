using Microsoft.IdentityModel.Tokens;
using TerraBase.Errors;

namespace TerraBase.Features.UserAuth;

/// <summary>The issuer's signing keys after a refresh, or no keys are available yet.</summary>
public readonly union RefreshIssuerKeysResult(IReadOnlyList<SecurityKey>, Unavailable);

/// <summary>The signing keys of the configured token issuer (ts-auth), cached in memory.</summary>
public interface IIssuerKeys
{
    /// <summary>The cached keys; <see langword="null"/> until the first fetch succeeds.</summary>
    IReadOnlyList<SecurityKey>? Keys { get; }

    /// <summary>
    /// Fetches the keys again after a token named a key id they lack, unless a refresh for that reason ran within the
    /// last 30 seconds. Concurrent callers share one fetch. Answers the keys afterwards, which are the cached ones when
    /// the fetch failed or was skipped; <see cref="Unavailable"/> when no keys have been loaded.
    /// </summary>
    ValueTask<RefreshIssuerKeysResult> RefreshAsync(CancellationToken cancellationToken);
}
