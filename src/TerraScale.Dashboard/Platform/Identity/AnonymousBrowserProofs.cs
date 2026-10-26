using System.Security.Cryptography;
using System.Text;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Platform.Identity;

// One encrypted, CAS-protected pool bounds discarded-cookie consent pages across all replicas.
internal sealed class AnonymousBrowserProofs(IDashboardState state, IDashboardIdentity identity, TimeProvider time)
{
    internal const int Capacity = 256;
    internal const string Id = "browser-proof-pool";
    internal const string Purpose = "anonymous-browser-proofs";
    private const string Collection = "auth-flows";
    internal static bool IsHandle(string? value) => value is { Length: 66 } && value.StartsWith("a.", StringComparison.Ordinal) && value[2..].All(char.IsAsciiHexDigitLower);

    public async Task<BrowserProof?> ReadAsync(string handle, CancellationToken cancellationToken)
    {
        var (_, proofs) = await ReadPoolAsync(cancellationToken);
        return proofs.GetValueOrDefault(Key(handle)) is { } proof && proof.ExpiresAt > time.GetUtcNow() ? proof : null;
    }

    public async Task<string> AddAsync(BrowserProof proof, CancellationToken cancellationToken)
    {
        var handle = "a." + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        await UpdateAsync(proofs =>
        {
            if (proofs.Count >= Capacity) throw new AccountAccessException("Too many outstanding consent pages. Wait for an existing page to expire before trying again.", 503);
            proofs.Add(Key(handle), proof);
            return true;
        }, cancellationToken);
        return handle;
    }

    public Task RemoveAsync(string handle, CancellationToken cancellationToken) => UpdateAsync(proofs => proofs.Remove(Key(handle)), cancellationToken);

    private async Task UpdateAsync(Func<Dictionary<string, BrowserProof>, bool> change, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var (document, proofs) = await ReadPoolAsync(cancellationToken);
            var expired = proofs.Where(pair => pair.Value.ExpiresAt <= time.GetUtcNow()).Select(pair => pair.Key).ToArray();
            foreach (var key in expired) proofs.Remove(key);
            if (!change(proofs) && expired.Length == 0) return;
            try
            {
                if (proofs.Count == 0 && document is not null)
                    await state.DeleteAsync(Collection, Id, document.Version, cancellationToken);
                else if (proofs.Count > 0)
                {
                    var credentials = await identity.ReadAsync(cancellationToken);
                    await state.PutAsync(Collection, Id, AuthFlows.Seal(proofs, credentials.SessionEncryptionKey, Purpose),
                        document is null ? TerraBasePrecondition.Absent : TerraBasePrecondition.Matches(document.Version), cancellationToken);
                }
                return;
            }
            catch (HttpRequestException error) when (error.StatusCode is System.Net.HttpStatusCode.PreconditionFailed or System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.NotFound) { }
        }
        throw new IOException("Browser consent storage is busy. Try again shortly.");
    }

    private async Task<(TerraBaseDocument? Document, Dictionary<string, BrowserProof> Proofs)> ReadPoolAsync(CancellationToken cancellationToken)
    {
        var document = await state.GetAsync(Collection, Id, cancellationToken);
        if (document is null) return (null, new(StringComparer.Ordinal));
        if (document.State != "value") throw new IOException("Browser consent storage has a causal conflict.");
        var credentials = await identity.ReadAsync(cancellationToken);
        return (document, AuthFlows.Open<Dictionary<string, BrowserProof>>(document.Document, credentials.SessionEncryptionKey, Purpose));
    }

    private static string Key(string handle) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(handle)));
}

internal sealed record BrowserProof(string Token, string Binding, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "Dashboard browser proof [REDACTED]";
}
