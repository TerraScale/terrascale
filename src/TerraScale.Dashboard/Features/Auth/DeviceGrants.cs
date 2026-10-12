using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Auth;

public sealed record DeviceGrant(string DeviceHash, string CodeHash, string ClientHash, DateTimeOffset ExpiresAt,
    DateTimeOffset NextPoll, int Interval = 5, bool Denied = false, bool Redeeming = false,
    string UserId = "", string SessionId = "", string CredentialId = "")
{
    public bool Pending(DateTimeOffset now) => ExpiresAt > now && !Denied && SessionId.Length == 0;
}

// One bounded CAS document provides atomic code uniqueness and both quotas
// across dashboard replicas. It contains hashes and opaque credential refs;
// provider token material stays in DashboardSessions' encrypted records.
public sealed class DeviceGrants(IDashboardState state, TimeProvider time)
{
    private const string Collection = "device_grants";
    private const string Index = "active";

    public Task<DeviceGrant?> ByCodeAsync(string code, CancellationToken cancellationToken) =>
        ReadGrantAsync(grant => grant.CodeHash == Hash(code), cancellationToken);

    public Task<DeviceGrant?> ByDeviceAsync(string device, CancellationToken cancellationToken) =>
        ReadGrantAsync(grant => grant.DeviceHash == Hash(device), cancellationToken);

    public async Task<IReadOnlyList<DeviceGrant>> ExpiredAsync(CancellationToken cancellationToken)
    {
        var document = await state.GetAsync(Collection, Index, cancellationToken);
        return document is null ? [] : Read(document).Values.Where(grant => grant.ExpiresAt <= time.GetUtcNow()).ToArray();
    }

    public Task IssueAsync(DeviceGrant grant, CancellationToken cancellationToken) => UpdateAsync(set =>
    {
        var active = set.Values.Where(item => item.ExpiresAt > time.GetUtcNow()).ToArray();
        if (active.Count(item => item.ClientHash == grant.ClientHash) >= 10 || active.Length >= 1000)
            throw new DeviceRejectedException("slow_down", 429, 5);
        if (active.Any(item => item.CodeHash == grant.CodeHash)) throw new DeviceCodeTakenException();
        // Keep recent expired grants so late polling receives expired_token.
        // Credential-bearing grants are removed only after provider cleanup.
        foreach (var expired in set.Values.Where(item => item.CredentialId.Length == 0 && item.ExpiresAt <= time.GetUtcNow().AddMinutes(-10)).Select(item => item.DeviceHash).ToArray()) set.Remove(expired);
        if (set.Count >= 2000) throw new DeviceRejectedException("slow_down", 429, 5);
        set.Add(grant.DeviceHash, grant);
    }, cancellationToken);

    public async Task<DeviceGrant?> ChangeAsync(string deviceHash, Func<DeviceGrant, DeviceGrant?> update, CancellationToken cancellationToken)
    {
        DeviceGrant? result = null;
        await UpdateAsync(set =>
        {
            result = null;
            if (!set.TryGetValue(deviceHash, out var grant)) return;
            result = update(grant);
            if (result is null) set.Remove(deviceHash); else set[deviceHash] = result;
        }, cancellationToken);
        return result;
    }

    private async Task<DeviceGrant?> ReadGrantAsync(Func<DeviceGrant, bool> matches, CancellationToken cancellationToken)
    {
        var document = await state.GetAsync(Collection, Index, cancellationToken);
        return document is null ? null : Read(document).Values.SingleOrDefault(matches);
    }

    private async Task UpdateAsync(Action<Dictionary<string, DeviceGrant>> update, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var document = await state.GetAsync(Collection, Index, cancellationToken);
            var set = document is null ? new Dictionary<string, DeviceGrant>(StringComparer.Ordinal) : Read(document);
            update(set);
            try
            {
                await state.PutAsync(Collection, Index, JsonSerializer.SerializeToElement(set), document is null ? TerraBasePrecondition.Absent : TerraBasePrecondition.Matches(document.Version), cancellationToken);
                return;
            }
            catch (HttpRequestException error) when (attempt < 7 && error.StatusCode is System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.PreconditionFailed) { }
        }
    }

    private static Dictionary<string, DeviceGrant> Read(TerraBaseDocument document)
    {
        if (document.State != "value") throw new IOException("Device grants have a causal conflict.");
        var set = document.Document.Deserialize<Dictionary<string, DeviceGrant>>() ?? throw new IOException("Device grants are invalid.");
        if (set.Count > 2000 || set.Any(item => item.Key != item.Value.DeviceHash ||
            !Digest(item.Key) || !Digest(item.Value.CodeHash) || !Digest(item.Value.ClientHash) || item.Value.Interval < 5 ||
            item.Value.SessionId is null || item.Value.CredentialId is null || item.Value.UserId is null)) throw new IOException("Device grants are invalid.");
        return set;
    }

    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool Digest(string value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
}

public sealed class DeviceRejectedException(string code, int status = 400, int retryAfter = 0) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public int RetryAfter { get; } = retryAfter;
}
public sealed class DeviceCodeTakenException : Exception;
