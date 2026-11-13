using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.IdentityModel.Tokens;
using TerraBase.Primitives;
using ZLogger;

namespace TerraBase.Features.UserAuth;

internal sealed record StoredIssuerKeys(int Version, string Issuer, DateTimeOffset FetchedAt, DateTimeOffset LastKnownTime, PublicIssuerKeys Jwks);
internal sealed record PublicIssuerKeys([property: JsonPropertyName("keys")] IReadOnlyList<PublicIssuerKey> Keys);
internal sealed record PublicIssuerKey(
    [property: JsonPropertyName("kty")] string Kty,
    [property: JsonPropertyName("kid")] string? Kid,
    [property: JsonPropertyName("alg")] string? Alg,
    [property: JsonPropertyName("use")] string? Use,
    [property: JsonPropertyName("key_ops"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? KeyOps,
    [property: JsonPropertyName("crv")] string? Crv,
    [property: JsonPropertyName("x")] string? X,
    [property: JsonPropertyName("y")] string? Y,
    [property: JsonPropertyName("n")] string? N,
    [property: JsonPropertyName("e")] string? E,
    [property: JsonPropertyName("x5c")] IReadOnlyList<string> X5c);

internal sealed partial class TsAuthKeys
{
    private DateTimeOffset _lastKnownTime;
    private Snapshot? _storedCandidate;

    private async Task LoadStoredAsync(CancellationToken cancellationToken)
    {
        if (_issuer is null)
        {
            return;
        }

        try
        {
            await using var file = new FileStream(_storedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            if (file.Length > 2 * MaxResponseBytes)
            {
                throw new InvalidDataException("the stored signing key set exceeds its size limit");
            }

            using var payload = await JsonDocument.ParseAsync(file,
                new JsonDocumentOptions { MaxDepth = UserAuthJsonContext.Default.Options.MaxDepth }, cancellationToken).ConfigureAwait(false);
            var stored = payload.Deserialize(UserAuthJsonContext.Default.StoredIssuerKeys);
            if (stored is not { Version: 2 } || stored.Issuer != _issuer || stored.Jwks?.Keys is not { Count: > 0 }
                || stored.Jwks.Keys.Any(key => key is null))
            {
                RejectStored("invalid");
                return;
            }

            var loadedAt = _time.GetTimestamp();
            var now = _time.GetUtcNow();
            if (now < stored.FetchedAt || now < stored.LastKnownTime || stored.LastKnownTime < stored.FetchedAt)
            {
                RejectStored("clock_backwards");
                return;
            }

            var age = now - stored.FetchedAt;
            if (age >= MaxKeyAge)
            {
                RejectStored("expired");
                return;
            }

            var set = ReadKeySet(payload.RootElement.GetProperty("jwks").GetRawText());
            var keys = SigningKeys(set);
            _storedCandidate = new Snapshot(keys, PublicKeys(set), stored.FetchedAt, stored.LastKnownTime, age, loadedAt);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            RejectStored("invalid");
        }
    }

    private async Task ActivateStoredAsync(CancellationToken cancellationToken)
    {
        var candidate = Interlocked.Exchange(ref _storedCandidate, null);
        if (candidate is null || Volatile.Read(ref _snapshot) is not null)
        {
            return;
        }

        var loadedAt = _time.GetTimestamp();
        var now = _time.GetUtcNow();
        if (now < candidate.FetchedAt || now < candidate.LastKnownTime)
        {
            RejectStored("clock_backwards");
            return;
        }

        var elapsed = candidate.AgeAtLoad + _time.GetElapsedTime(candidate.LoadedAt, loadedAt);
        var age = elapsed > now - candidate.FetchedAt ? elapsed : now - candidate.FetchedAt;
        if (age >= MaxKeyAge)
        {
            RejectStored("expired");
            return;
        }

        var snapshot = candidate with { AgeAtLoad = age, LoadedAt = loadedAt, LastKnownTime = now };
        if (Interlocked.CompareExchange(ref _snapshot, snapshot, null) is not null)
        {
            return;
        }

        await StoreAsync(snapshot, cancellationToken).ConfigureAwait(false);
        _logger.ZLogInformation($"Loaded {snapshot.Keys.Count:@Count} stored ts-auth public signing keys");
    }

    private void RejectStored(string reason)
    {
        _storedRejected.Add(1, new KeyValuePair<string, object?>("reason", reason));
        _logger.ZLogWarning($"Stored ts-auth signing keys were not used ({reason:@Reason}); fetching from auth-issuer");
    }

    private async Task StoreAsync(Snapshot snapshot, CancellationToken cancellationToken)
    {
        await _store.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_issuer is null || Volatile.Read(ref _snapshot) is { } current && !ReferenceEquals(current, snapshot))
            {
                return;
            }

            var elapsedTime = snapshot.FetchedAt + Age(snapshot);
            var wallTime = _time.GetUtcNow();
            var highWater = new[] { _lastKnownTime, snapshot.LastKnownTime, elapsedTime, wallTime }.Max();
            _lastKnownTime = highWater;
            var stored = new StoredIssuerKeys(2, _issuer, snapshot.FetchedAt, highWater, snapshot.Public);
            var directory = Path.GetDirectoryName(Path.GetFullPath(_storedPath))!;
            Directory.CreateDirectory(directory);
            var staged = _storedPath + ".new";
            await using (var file = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(file, stored, UserAuthJsonContext.Default.StoredIssuerKeys, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }

            File.Move(staged, _storedPath, overwrite: true);
            DirectorySync.Sync(directory);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.ZLogWarning($"Could not persist ts-auth public signing keys; in-memory verification continues");
        }
        finally
        {
            _store.Release();
        }
    }

    private static PublicIssuerKeys PublicKeys(JsonWebKeySet set) => new([.. set.Keys
        .Where(Verifies)
        .Select(key => new PublicIssuerKey(key.Kty, key.Kid, key.Alg, key.Use, key.KeyOps.Count > 0 ? [.. key.KeyOps] : null, key.Crv, key.X, key.Y, key.N, key.E, [.. key.X5c]))]);

    private static JsonWebKeySet ReadKeySet(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("the key set is not an object");
        var entries = root["keys"] as JsonArray ?? throw new InvalidDataException("the key set lists no keys");
        for (var index = entries.Count - 1; index >= 0; index--)
        {
            if (entries[index] is not JsonObject entry
                || entry.TryGetPropertyValue("use", out var use) && Text(use) is not (null or "" or "sig")
                || !entry.TryGetPropertyValue("key_ops", out var operations))
            {
                continue;
            }

            if (operations is not JsonArray permitted)
            {
                throw new InvalidDataException("a signing key declares malformed operations");
            }

            if (!permitted.Any(operation => Text(operation) == "verify"))
            {
                entries.RemoveAt(index);
            }
        }

        return new JsonWebKeySet(root.ToJsonString());
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool Verifies(JsonWebKey key)
    {
        if (key.Use is not (null or "" or "sig"))
        {
            return false;
        }

        if (key.Kty == "oct" || key.HasPrivateKey)
        {
            throw new InvalidDataException("the key set contains secret key material");
        }

        return key.Kty is "RSA" or "EC"
            && (string.IsNullOrEmpty(key.Alg) || UserAuthenticationHandler.AllowsAlgorithm(key.Alg))
            && (key.KeyOps.Count == 0 || key.KeyOps.Contains("verify", StringComparer.Ordinal));
    }

    private static List<SecurityKey> SigningKeys(JsonWebKeySet set)
    {
        List<SecurityKey> keys = [];
        foreach (var key in set.Keys.Where(Verifies))
        {
            if (!JsonWebKeyConverter.TryConvertToSecurityKey(key, out var converted) || converted is not AsymmetricSecurityKey { KeySize: > 0 })
            {
                throw new InvalidDataException("the key set contains an unusable public signing key");
            }

            if (converted is RsaSecurityKey { Rsa: null } rsa)
            {
                using var imported = RSA.Create(rsa.Parameters);
            }

            keys.Add(key);
        }

        return keys.Count > 0 ? keys : throw new InvalidDataException("the key set contains no public signing keys");
    }
}
