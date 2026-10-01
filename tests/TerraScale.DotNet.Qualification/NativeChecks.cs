using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet;
using TerraScale.DotNet.Native;

namespace TerraScale.DotNet.Qualification;

internal static class NativeChecks
{
    private static readonly NativeScope Scope = new(
        "10000000-0000-0000-0000-000000000001", "20000000-0000-0000-0000-000000000001", ulong.MaxValue);
    private const string Collection = "33333333-3333-3333-3333-333333333333";
    private const string Document = "44444444-4444-4444-4444-444444444444";
    private static readonly string Key = "tbk1." + Base64(new byte[16]) + "." + Base64(new byte[32]);

    internal static Task DirectOriginsAsync()
    {
        foreach (var origin in new[] { "https://terrascale.tech", "https://atlas.terrascale.tech", "https://auth.terrascale.tech",
            "https://gru.terrascale.tech", "https://x.gru.api.terrascale.tech", "https://GRU.api.terrascale.tech",
            "https://gru.api.terrascale.tech.", "https://gru.api.terrascale.tech:8443", "https://gru.api.terrascale.tech:443" })
        {
            try
            {
                NativeOrigin.Validate(origin);
                throw new InvalidOperationException("An invalid TerraScale data origin was accepted.");
            }
            catch (ArgumentException) { }
        }
        Require(NativeOrigin.Validate("https://gru.api.terrascale.tech").Host == "gru.api.terrascale.tech", "Canonical region origin was rejected.");
        Require(NativeOrigin.Validate("https://database.customer.example:8443").Host == "database.customer.example", "Provisioned independent origin was rejected.");
        Require(NativeOrigin.Validate("http://127.0.0.1:12345", development: true).IsLoopback, "Explicit development origin was rejected.");
        return Task.CompletedTask;
    }

    internal static async Task AuthDiagnosticsAsync()
    {
        const string secret = "sk_package_plain_secret_canary";
        var options = new TerraScaleAuthClientOptions
        {
            BaseAddress = new Uri("https://auth.customer.example"), InstanceSlug = "acme", ApiKey = secret
        };
        Require(!options.ToString().Contains(secret, StringComparison.Ordinal), "Auth options exposed a credential.");
        using (var conflicting = new HttpClient(new Handler((_, _) => Task.FromResult(Json("{}"))))
        {
            BaseAddress = new Uri("https://" + secret + ":password@auth.customer.example")
        })
        {
            try
            {
                _ = new TerraScaleAuthClient(conflicting, options);
                throw new InvalidOperationException("Conflicting auth origin was accepted.");
            }
            catch (ArgumentException error)
            {
                Require(!error.ToString().Contains(secret, StringComparison.Ordinal) && !error.ToString().Contains("password", StringComparison.Ordinal), "Auth constructor exposed URI credentials.");
            }
        }
        foreach (var code in new[] { "unauthorized", "token=" + secret, secret })
        {
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                ReasonPhrase = secret,
                Content = new StringContent(JsonSerializer.Serialize(new { error = new { code, message = secret }, extra = secret }), Encoding.UTF8, "application/json")
            })));
            var auth = new TerraScaleAuthClient(http, options);
            try
            {
                await auth.Users.ListAsync();
                throw new InvalidOperationException("Auth failure was accepted as success.");
            }
            catch (TerraScaleAuthException error)
            {
                Require(error.StatusCode == HttpStatusCode.Unauthorized, "Auth HTTP status changed.");
                Require(error.ErrorCode == (code == "unauthorized" ? code : null), "Auth producer code was not safely classified.");
                Require(error.Message == "ts-auth returned HTTP 401." && !error.ToString().Contains(secret, StringComparison.Ordinal), "Auth diagnostics exposed producer text.");
                Require(error.InnerException is null, "Auth diagnostic retained an unsafe inner exception.");
            }
        }
    }

    internal static async Task ContractResourcesAsync()
    {
        foreach (var item in new[]
        {
            ("client-openapi.json", "563a66449d8c2eeb4306b9bb9d994cabb86c14bd4c469f785dabe5afb9f6ef3d", NativeContract.OpenApiSha256),
            ("atomic_vectors.json", "4ce4286b6e2eeae4ee15dec83f788f33cffdba7606a49021ec81afe0a4a9e816", NativeContract.AtomicVectorsSha256),
            ("atlas.native-wire-identity.json", "b789d6fb7782e34ece3b516139856f2b6d2dea0543436820eefa50ff31355538", NativeContract.AtlasWireIdentitySha256),
            ("atlas.native-discovery-success.json", "82fbc6518a5e8e825879243c45a86e6e66f44323a6dcd3ad8280c8109aa671ec", NativeContract.AtlasDiscoverySuccessSha256),
            ("atlas.native-discovery-forbidden.json", "3208d206e45cde22446e0e227c4882bdacb97f2a4926c0341a13580c23e48b9c", NativeContract.AtlasDiscoveryForbiddenSha256)
        })
        {
            Require(item.Item3 == item.Item2, "NativeContract differs from the pinned source identity and documented derivation.");
            var bytes = await ResourceAsync(item.Item1);
            Require(Convert.ToHexStringLower(SHA256.HashData(bytes)) == item.Item2, "Packaged native contract identity differs.");
        }
        using var vectors = JsonDocument.Parse(await ResourceAsync("atomic_vectors.json"));
        foreach (var vector in vectors.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var raw = vector.GetProperty("scope");
            var scope = new NativeScope(raw.GetProperty("tenant").GetString()!, raw.GetProperty("database").GetString()!,
                ulong.Parse(raw.GetProperty("shard").GetString()!, CultureInfo.InvariantCulture));
            var commit = NativeCodec.DecodeCommit(scope, vector.GetProperty("request").GetRawText());
            Require(NativeCodec.EncodeCommit(scope, commit).Identity.EffectDigest == vector.GetProperty("effect_digest").GetString(),
                "Packaged native codec differs from an actual published effect vector.");
        }
    }

    internal static async Task TransportOwnershipAsync()
    {
        var borrowedHandler = new Handler((_, _) => Task.FromResult(Json("{\"version\":1,\"rows\":[]}")));
        var http = new HttpClient(borrowedHandler) { BaseAddress = new Uri("https://caller.example/base/"), Timeout = TimeSpan.FromSeconds(7) };
        try
        {
            var originalBase = http.BaseAddress;
            var client = new NativeClient(http, Scope, "https://gru.api.terrascale.tech", static _ => ValueTask.FromResult(Key), TimeSpan.FromSeconds(1));
            var router = new NativeDiscoveryRouter(http, TimeSpan.FromSeconds(1));
            client.Dispose();
            client.Dispose();
            router.Dispose();
            router.Dispose();
            Require(borrowedHandler.Disposals == 0, "SDK disposed a caller-owned transport.");
            Require(http.BaseAddress == originalBase && http.Timeout == TimeSpan.FromSeconds(7), "SDK changed caller transport configuration.");
            using var response = await http.GetAsync("https://caller.example/probe");
            Require(response.StatusCode == HttpStatusCode.OK, "Caller transport became unusable.");
        }
        finally { http.Dispose(); }
        Require(borrowedHandler.Disposals == 1, "Caller transport was not disposed exactly once.");

        var ownedHandler = new Handler((_, _) => Task.FromResult(Json("{}")));
        var owned = new NativeClient(Scope, new NativeDirectAdmissionProvider("https://gru.api.terrascale.tech"),
            static _ => ValueTask.FromResult(Key), TimeSpan.FromSeconds(1), ownedHandler);
        owned.Dispose();
        owned.Dispose();
        Require(ownedHandler.Disposals == 1, "Owned native handler was not disposed exactly once.");

        var invalidClientHandler = new Handler((_, _) => Task.FromResult(Json("{}")));
        try
        {
            _ = new NativeClient(Scope with { Tenant = "unprovisioned" },
                new NativeDirectAdmissionProvider("https://gru.api.terrascale.tech"), static _ => ValueTask.FromResult(Key),
                TimeSpan.FromSeconds(1), invalidClientHandler);
            throw new InvalidOperationException("Invalid native scope was accepted.");
        }
        catch (InvalidDataException) { }
        Require(invalidClientHandler.Disposals == 1, "Native constructor failure leaked an owned transport.");

        var invalidHandler = new Handler((_, _) => Task.FromResult(Json("{}")));
        try
        {
            _ = new NativeDiscoveryRouter(TimeSpan.Zero, invalidHandler);
            throw new InvalidOperationException("Invalid discovery options were accepted.");
        }
        catch (ArgumentOutOfRangeException) { }
        Require(invalidHandler.Disposals == 1, "Constructor failure leaked an owned transport.");
    }

    internal static async Task PublicNativeJourneyAsync()
    {
        var discovery = Encoding.UTF8.GetString(await ResourceAsync("atlas.native-discovery-success.json"));
        var paths = new List<string>();
        NativeTransactionIdentity? identity = null;
        NativeCommitReceipt? receipt = null;
        var scopePath = $"/v1/tenants/{Scope.Tenant}/databases/{Scope.Database}/shards/{Scope.Shard.ToString(CultureInfo.InvariantCulture)}";
        var readPath = $"{scopePath}/collections/{Collection}/documents/{Document}";
        var queryPath = $"{scopePath}/collections/{Collection}/documents/query";
        var commitPath = scopePath + "/transactions";
        var statusPath = scopePath + "/transactions/status";
        var handler = new Handler(async (request, token) =>
        {
            Require(request.Headers.Authorization?.Parameter == Key, "Current scoped credential was not supplied.");
            Require(request.Headers.Contains("Idempotency-Key") == false, "An unrelated idempotency header was introduced.");
            paths.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.Host == "atlas.terrascale.tech")
            {
                Require(request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/api/v1/discovery", "Unexpected Atlas route or method.");
                var parameters = request.RequestUri.Query.TrimStart('?').Split('&').Select(pair => pair.Split('=', 2))
                    .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]), StringComparer.Ordinal);
                Require(parameters.Count == 3 && parameters.GetValueOrDefault("tenant_id") == Scope.Tenant &&
                    parameters.GetValueOrDefault("database_id") == Scope.Database &&
                    parameters.GetValueOrDefault("shard_id") == Scope.Shard.ToString(CultureInfo.InvariantCulture), "Atlas discovery addressed another native scope.");
                return Json(discovery);
            }
            Require(request.RequestUri.Host == "gru.api.terrascale.tech", "Native request used a fallback or another origin.");
            if (request.RequestUri.AbsolutePath == readPath)
            {
                Require(request.Method == HttpMethod.Get, "Native read method changed.");
                return Json("{\"version\":1,\"state\":\"absent\",\"state_version\":null,\"context\":[],\"siblings\":[]}");
            }
            Require(request.Method == HttpMethod.Post && new[] { queryPath, commitPath, statusPath }.Contains(request.RequestUri.AbsolutePath),
                "Unexpected native scope, resource, route or HTTP method.");
            Require(request.Content!.Headers.ContentType!.ToString() == "application/json", "Native request media type changed.");
            using var body = NativeCodec.Parse(await request.Content.ReadAsStringAsync(token));
            if (request.RequestUri.AbsolutePath == queryPath)
            {
                var query = body.RootElement;
                Require(query.EnumerateObject().Count() == 3 && query.GetProperty("version").GetInt32() == 1 &&
                    query.GetProperty("limit").GetInt32() == 1 && !query.TryGetProperty("cursor", out _),
                    "Prepared query version, limit or cursor changed.");
                var predicate = query.GetProperty("predicate");
                var path = predicate.GetProperty("path");
                Require(predicate.EnumerateObject().Count() == 2 && predicate.GetProperty("op").GetString() == "is_missing" &&
                    path.ValueKind == JsonValueKind.Array && path.GetArrayLength() == 1 && path[0].GetUInt32() == 1 &&
                    !predicate.TryGetProperty("value", out _), "Prepared query predicate or field path changed.");
                return Json("{\"version\":1,\"rows\":[]}");
            }
            if (request.RequestUri.AbsolutePath == statusPath)
            {
                var transaction = body.RootElement.GetProperty("transaction_id");
                Require(transaction.EnumerateObject().Count() == 2 &&
                    transaction.GetProperty("expires_at_unix_ms").GetString() == identity!.TransactionId.ExpiresAtUnixMs.ToString(CultureInfo.InvariantCulture) &&
                    transaction.GetProperty("nonce").GetString() == identity.TransactionId.Nonce,
                    "Status looked up another transaction ID.");
                var authority = body.RootElement.GetProperty("authority");
                Require(identity!.Authority is NativeAuthority.Leaderless && authority.EnumerateObject().Count() == 1 &&
                    authority.GetProperty("mode").GetString() == "leaderless", "Status looked up another authority.");
                Require(body.RootElement.GetProperty("effect_digest").GetString() == identity.EffectDigest, "Status looked up another effect.");
                return Json(NativeCodec.EncodeTransactionStatus(new NativeTransactionStatus(identity!, NativeTransactionState.Committed, receipt)));
            }
            Require(request.RequestUri.AbsolutePath == commitPath, "Public client used an undocumented route.");
            var commit = NativeCodec.DecodeCommit(Scope, body.RootElement.GetRawText());
            identity = NativeCodec.EncodeCommit(Scope, commit).Identity;
            receipt = new NativeCommitReceipt(NativeDurability.LocalDurable, identity,
                new NativeEvent(Scope.Database, Scope.Shard, "88888888-8888-8888-8888-888888888888", new string('9', 32), 1), 1);
            return Json(NativeCodec.EncodeReceipt(receipt));
        });
        var directory = Directory.CreateTempSubdirectory("terrascale-native-sdk-consumer-");
        var operationPath = Path.Combine(directory.FullName, "operation.json");
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            using var router = new NativeDiscoveryRouter(http, TimeSpan.FromSeconds(1), clock: new FixtureClock());
            using var client = new NativeClient(http, Scope, router, static _ => ValueTask.FromResult(Key), TimeSpan.FromSeconds(1));
            Require(await client.ReadAsync(Collection, Document, safety.Token) is NativeResult<NativePoint>.Success, "Public native read failed.");
            var query = client.PrepareQuery(Collection, new NativeQueryPredicate(NativeQueryOperator.IsMissing, [1]), 1);
            Require(await client.QueryAsync(query, cancellationToken: safety.Token) is NativeResult<NativeQueryPage>.Success, "Public native query failed.");
            var commit = new NativeCommit(new NativeTransactionId(ulong.MaxValue, new string('6', 32)), new NativeAuthority.Leaderless(),
                [new NativeWrite(Collection, Document, 1, new string('5', 64), new NativeCondition.None(), [],
                    new NativeWriteValue.Live([new NativeField(1, new NativeValue.Text("private consumer payload"))]))]);
            var prepared = client.PrepareCommit(commit);
            await NativeOperationStore.SaveAsync(prepared, operationPath, safety.Token);
            var retained = await NativeOperationStore.LoadAsync(operationPath, safety.Token);
            Require(client.RestoreCommit(retained).RequestBody == prepared.RequestBody, "Retained body changed during restoration.");
            Require((await File.ReadAllTextAsync(operationPath, safety.Token)).Contains(Key, StringComparison.Ordinal) == false,
                "Retained operation serialized the credential.");
            Require(await client.CommitAsync(prepared, safety.Token) is NativeResult<NativeCommitReceipt>.Success, "Public native commit failed.");
            var recovered = await NativeRecovery.ResolveAsync(client, prepared, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100), safety.Token);
            Require(recovered.IsResolved && recovered.Receipt!.Identity == prepared.Identity, "Public native status did not preserve the original receipt.");
            Require(paths.Count(path => path == commitPath) == 1, "Recovery replayed a mutation.");
            Require(paths.Count(path => path == "/api/v1/discovery") == 1, "Public requests did not reuse their matching authenticated lease.");
        }
        finally
        {
            http.Dispose();
            directory.Delete(recursive: true);
        }
        Require(handler.Disposals == 1, "Shared public transport was not disposed exactly once.");
    }

    private static async Task<byte[]> ResourceAsync(string name)
    {
        await using var stream = typeof(NativeClient).Assembly.GetManifestResourceStream("TerraScale.DotNet.Native." + name)
            ?? throw new InvalidOperationException("Packaged native contract resource is missing.");
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };
    private static string Base64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private sealed class FixtureClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(1790769601);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Disposals { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposals++;
            base.Dispose(disposing);
        }
    }
}
