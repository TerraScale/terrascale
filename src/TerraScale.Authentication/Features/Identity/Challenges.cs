using Dapper;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Identity;

public sealed class Challenges(AuthData data, TimeProvider clock)
{
    public async Task CreateAsync(string instanceId, string purpose, string tokenHash, string subjectId, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        ValidateTokenHash(tokenHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        if (expiresAt <= clock.GetUtcNow())
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "A challenge must expire in the future.");
        }

        if (!await data.SaveAsync(Change.Insert(new ChallengeRecord(instanceId, purpose, tokenHash, subjectId, expiresAt)), cancellationToken))
        {
            throw ApiException.Unavailable();
        }
    }

    public async Task<CredentialBinding?> ConsumeAsync(string instanceId, string purpose, string tokenHash, string subjectId, CancellationToken cancellationToken)
    {
        ValidateTokenHash(tokenHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        var current = Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<ChallengeRecord>(
            "SELECT * FROM challenges WHERE instance_id = @instanceId AND purpose = @purpose AND token_hash = @tokenHash", new { instanceId, purpose, tokenHash }),
            cancellationToken));
        if (current?.Value is not { Consumed: false } challenge || challenge.ExpiresAt <= clock.GetUtcNow() || challenge.SubjectId != subjectId)
        {
            return null;
        }

        var binding = Credentials.New("challenge:" + purpose, tokenHash);
        return await data.SaveAsync([Credentials.Claim(instanceId, binding), Change.Update(current, challenge with { Consumed = true })], cancellationToken)
            && challenge.ExpiresAt > clock.GetUtcNow() ? binding : null;
    }

    public async Task<int> PruneAsync(int batchSize, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        return await data.InBatchesAsync(batchSize, connection => connection.ExecuteAsync(
            """
            DELETE FROM challenges WHERE (instance_id, purpose, token_hash) IN (
                SELECT instance_id, purpose, token_hash FROM challenges WHERE expires_at <= @now ORDER BY expires_at LIMIT @batchSize)
            """, new { now, batchSize }), cancellationToken);
    }

    private static void ValidateTokenHash(string tokenHash)
    {
        if (tokenHash is not { Length: 64 } || tokenHash.Any(character => !char.IsAsciiHexDigitLower(character)))
        {
            throw new ArgumentException("A challenge requires a lowercase SHA-256 digest.", nameof(tokenHash));
        }
    }
}
