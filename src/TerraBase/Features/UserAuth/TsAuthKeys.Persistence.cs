using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.IdentityModel.Tokens;
using ZLogger;

namespace TerraBase.Features.UserAuth;

internal sealed record StoredIssuerKeys(int Version, string Issuer, DateTimeOffset FetchedAt, PublicIssuerKeys Jwks);
internal sealed record PublicIssuerKeys([property: JsonPropertyName("keys")] IReadOnlyList<PublicIssuerKey> Keys);
internal sealed record PublicIssuerKey(
    [property: JsonPropertyName("kty")] string Kty,
    [property: JsonPropertyName("kid")] string? Kid,
    [property: JsonPropertyName("alg")] string? Alg,
    [property: JsonPropertyName("use")] string? Use,
    [property: JsonPropertyName("crv")] string? Crv,
    [property: JsonPropertyName("x")] string? X,
    [property: JsonPropertyName("y")] string? Y,
    [property: JsonPropertyName("n")] string? N,
    [property: JsonPropertyName("e")] string? E,
    [property: JsonPropertyName("x5c")] IReadOnlyList<string> X5c);

internal sealed partial class TsAuthKeys
{
    private async Task LoadStoredAsync(CancellationToken cancellationToken)
    {
        if (_issuer is null || !File.Exists(_storedPath))
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

            var stored = await JsonSerializer.DeserializeAsync(file, UserAuthJsonContext.Default.StoredIssuerKeys, cancellationToken).ConfigureAwait(false);
            if (stored is not { Version: 1 } || stored.Issuer != _issuer || stored.Jwks?.Keys is not { Count: > 0 }
                || stored.Jwks.Keys.Any(key => key is null || key.Kty is not ("RSA" or "EC"))
                || stored.FetchedAt > _time.GetUtcNow() || _time.GetUtcNow() - stored.FetchedAt >= MaxKeyAge)
            {
                _logger.ZLogWarning($"The stored ts-auth signing keys are expired or do not name auth-issuer; they were not used");
                return;
            }

            var set = new JsonWebKeySet(JsonSerializer.Serialize(stored.Jwks, UserAuthJsonContext.Default.PublicIssuerKeys));
            SecurityKey[] keys = [.. set.GetSigningKeys().Where(key => key is AsymmetricSecurityKey or JsonWebKey { Kty: "RSA" or "EC" })];
            if (keys.Length == 0)
            {
                throw new InvalidDataException("the stored signing key set contains no verification keys");
            }

            Volatile.Write(ref _snapshot, new Snapshot(keys, stored.FetchedAt));
            _logger.ZLogInformation($"Loaded {keys.Length:@Count} stored ts-auth public signing keys, age {(_time.GetUtcNow() - stored.FetchedAt).TotalSeconds:@AgeSeconds} seconds");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            _logger.ZLogWarning($"Could not load stored ts-auth signing keys ({error.GetType().Name:@Error}); fetching from auth-issuer");
        }
    }

    private async Task StoreAsync(JsonWebKeySet set, DateTimeOffset fetchedAt, CancellationToken cancellationToken)
    {
        if (_issuer is null)
        {
            return;
        }

        try
        {
            PublicIssuerKey[] keys = [.. set.Keys.Where(key => key.Kty is "RSA" or "EC")
                .Select(key => new PublicIssuerKey(key.Kty, key.Kid, key.Alg, key.Use, key.Crv, key.X, key.Y, key.N, key.E, [.. key.X5c]))];
            var stored = new StoredIssuerKeys(1, _issuer, fetchedAt, new PublicIssuerKeys(keys));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_storedPath))!);
            var staged = _storedPath + ".new";
            await using (var file = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(file, stored, UserAuthJsonContext.Default.StoredIssuerKeys, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }

            File.Move(staged, _storedPath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _logger.ZLogWarning($"Could not persist ts-auth public signing keys ({error.GetType().Name:@Error}); the fetched keys remain usable in memory");
        }
    }
}
