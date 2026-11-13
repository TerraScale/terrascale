using Microsoft.IdentityModel.Tokens;
using TerraBase.Errors;

namespace TerraBase.Features.UserAuth;

public readonly union RefreshIssuerKeysResult(IReadOnlyList<SecurityKey>, Unavailable);

public interface IIssuerKeys
{
    IReadOnlyList<SecurityKey>? Keys { get; }

    ValueTask<RefreshIssuerKeysResult> RefreshAsync(string? keyId, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<SecurityKey>?> JoinAsync(CancellationToken cancellationToken);
}
