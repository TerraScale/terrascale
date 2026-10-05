using Microsoft.IdentityModel.Tokens;

namespace TerraScale.TerraBase.Features.UserAuth;

/// <summary>The signing keys of the configured token issuer (ts-auth), cached in memory.</summary>
public interface IIssuerKeys
{
    /// <summary>
    /// Whether user tokens can be validated: no issuer is configured, or its keys were fetched at least once.
    /// <c>/readyz</c> answers 503 until then.
    /// </summary>
    bool IsReady { get; }

    /// <summary>The cached keys; <see langword="null"/> until the first fetch succeeds.</summary>
    IReadOnlyList<SecurityKey>? Keys { get; }

    /// <summary>
    /// Fetches the keys again after a token named a key id they lack, unless a refresh for that reason ran within the
    /// last 30 seconds. Concurrent callers share one fetch. Answers the keys afterwards, which are the cached ones when
    /// the fetch failed or was skipped.
    /// </summary>
    ValueTask<IReadOnlyList<SecurityKey>?> RefreshAsync(CancellationToken cancellationToken);
}
