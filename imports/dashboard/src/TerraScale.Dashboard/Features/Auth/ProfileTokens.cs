namespace TerraScale.Dashboard.Features.Auth;

public interface IProfileTokens
{
    Task<IReadOnlyList<ProfileToken>> ListAsync(AccountContext context, CancellationToken cancellationToken);
    Task<CreatedProfileToken> CreateAsync(AccountContext context, string name, int expiresInDays, SessionRecord fresh, StepUpIntent stepUp, CancellationToken cancellationToken);
    Task RevokeAsync(AccountContext context, string id, CancellationToken cancellationToken);
}

public sealed record ProfileToken(string Id, string Name, string LastFour, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
public sealed record CreatedProfileToken(ProfileToken Token, string Secret)
{
    public override string ToString() => "Created personal access token [REDACTED]";
}
