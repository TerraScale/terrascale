using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Auth.Features.SigningKeys;

namespace TerraScale.Auth.Tests;

/// <summary>
/// One test's OCI Vault: a controlled stand-in for the Secrets (<c>20190301</c>) and Vault (<c>20180608</c>) REST
/// operations the key ring uses, served over loopback HTTP to ts-auth's real client. Every secret name exists, as an
/// operator pre-creates it, holding <c>{}</c>. Each request's OCI signature is verified (401 otherwise), and a write
/// needs <c>If-Match</c>: a stale ETag answers 412. Like OCI, a written version is applied asynchronously: the next
/// metadata read reports <c>UPDATING</c> without a usable ETag, and a write before it answers 409. Versions carry
/// OCI's stages and its two per-secret quotas: a write beyond <see cref="MaxVersions"/> versions in active use answers
/// 400; scheduling a deprecated version's deletion moves it out of active use into the pending-deletion quota
/// (<see cref="MaxPendingDeletions"/>, 400 beyond it), where it counts until the vault clock reaches its deletion
/// time. All vaults share one process-wide server.
/// </summary>
public sealed class FakeOciVault
{
    /// <summary>The serving instance's secret name every test vault uses.</summary>
    public const string SecretName = "test-ts-auth-signing-key-ring";

    private const string Region = "sa-saopaulo-1";

    private static readonly Lazy<Task<Uri>> Server = new(StartServerAsync);
    private static readonly ConcurrentDictionary<string, FakeOciVault> Vaults = new(StringComparer.Ordinal);
    private static readonly string PrivateKeyPem = NewPrivateKeyPem();
    private static readonly string[] CurrentStages = ["CURRENT", "LATEST"];
    private static readonly string[] PreviousStages = ["PREVIOUS"];
    private static readonly string[] DeprecatedStages = ["DEPRECATED"];

    private readonly ConcurrentDictionary<string, Secret> secrets = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<Secret> replaced = new();

    private FakeOciVault(string vaultId) => this.VaultId = vaultId;

    /// <summary>The vault's OCID.</summary>
    public string VaultId { get; }

    /// <summary>Makes <c>read</c> or <c>write</c> requests fail with 500, as an outage would; <see langword="null"/> serves normally.</summary>
    public string? Failing { get; set; }

    /// <summary>The vault's clock, sent as each answer's <c>Date</c>; ts-auth times its keys by it.</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>The most versions of a secret in active use (not scheduled for deletion); OCI allows 30.</summary>
    public int MaxVersions { get; set; } = 100;

    /// <summary>The most versions of a secret pending deletion; OCI allows 30.</summary>
    public int MaxPendingDeletions { get; set; } = 30;

    /// <summary>
    /// How long after scheduling a version is deleted. The client names no deletion time, and OCI documents no
    /// default; the longest waiting period it allows, 30 days, is the cautious assumption.
    /// </summary>
    public TimeSpan DeletionDelay { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// While set, a read of a secret's current version (by name) answers the previous one, as while OCI still applies
    /// a write; metadata and reads by version number already see the latest.
    /// </summary>
    public bool PublishDelayed { get; set; }

    private IEnumerable<Secret> All => this.secrets.Values.Concat(this.replaced);

    /// <summary>The vault of one deployment, named by any stable key such as its storage tenant: replicas share it.</summary>
    public static FakeOciVault For(string deployment)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(deployment)));
        return Vaults.GetOrAdd($"ocid1.vault.oc1.{Region}.test{hash[..16]}.vault{hash[16..48]}", id => new FakeOciVault(id));
    }

    /// <summary>Points a server's signing-key settings at this vault with a synthetic API key.</summary>
    public void Bind(Dictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        values["signing-key-vault"] = this.VaultId;
        values["signing-key-secret"] = SecretName;
        values["oci-region"] = Region;
        values["oci-tenancy"] = "ocid1.tenancy.oc1..aaaatesttenancy";
        values["oci-user"] = "ocid1.user.oc1..aaaatestuser";
        values["oci-fingerprint"] = "00:11:22:33:44:55:66:77:88:99:aa:bb:cc:dd:ee:ff";
        values["oci-private-key"] = PrivateKeyPem;
        values["oci-endpoint"] = Server.Value.GetAwaiter().GetResult().ToString();
    }

    /// <summary>
    /// Valid signing-key settings whose OCI origin refuses connections (HTTPS, as production requires): the server
    /// starts but never reaches OCI, and its signing keys stay unavailable.
    /// </summary>
    public static void BindRefusing(Dictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        For(Guid.NewGuid().ToString()).Bind(values);
        values["oci-endpoint"] = "https://127.0.0.1:9";
    }

    /// <summary>The instance's current ring, or <see langword="null"/> while the secret holds none.</summary>
    public JsonElement? Read(string instanceId)
    {
        var current = this.Current(instanceId);
        return current.Document.ValueKind == JsonValueKind.Object && !current.Document.EnumerateObject().Any() ? null : current.Document;
    }

    /// <summary>The instance's current content and version number.</summary>
    public (JsonElement Document, int Version) Current(string instanceId)
    {
        var secret = this.SecretFor(instanceId);
        lock (secret)
        {
            return (JsonDocument.Parse(secret.Versions[^1]).RootElement.Clone(), secret.Versions.Count);
        }
    }

    /// <summary>Writes a new version directly, like an operator or attacker with Vault access.</summary>
    public void Put(string instanceId, JsonElement ring)
    {
        var secret = this.SecretFor(instanceId);
        lock (secret)
        {
            secret.Write(Encoding.UTF8.GetBytes(ring.GetRawText()), applying: false);
        }
    }

    /// <summary>
    /// Deletes the instance's secret and creates it again under the same name with a new OCID, its first version
    /// holding <paramref name="ring"/>. Reads by name find the new secret; the old OCID stays readable and writable,
    /// as OCI may keep a deleted secret until its deletion date.
    /// </summary>
    public void Recreate(string instanceId, JsonElement ring)
    {
        var name = OciSigningKeyVault.SecretName(SecretName, instanceId);
        var secret = new Secret(this.VaultId, name);
        secret.Versions[0] = Encoding.UTF8.GetBytes(ring.GetRawText());
        if (this.secrets.TryGetValue(name, out var old))
        {
            this.replaced.Enqueue(old);
        }

        this.secrets[name] = secret;
    }

    /// <summary>How many reads of the instance's current version (by name) this vault answered.</summary>
    public int CurrentReads(string instanceId) => this.SecretFor(instanceId).Reads;

    /// <summary>The version numbers scheduled for deletion.</summary>
    public IReadOnlyList<int> Scheduled(string instanceId)
    {
        var secret = this.SecretFor(instanceId);
        lock (secret)
        {
            return [.. secret.Scheduled.Keys.Order()];
        }
    }

    /// <summary>Schedules a version's deletion directly, like an operator, counting against the pending-deletion quota.</summary>
    public void ScheduleDeletion(string instanceId, int number)
    {
        var secret = this.SecretFor(instanceId);
        lock (secret)
        {
            secret.Scheduled[number] = this.Clock.GetUtcNow() + this.DeletionDelay;
        }
    }

    /// <summary>
    /// Holds the next read of the instance's current version after it has taken its content, until the returned
    /// release runs: the answer then reports a version that may no longer be current.
    /// </summary>
    public (Task Reached, Action Release) StallNextCurrentRead(string instanceId) => Stall.Arm(held => this.SecretFor(instanceId).ReadStall = held);

    /// <summary>
    /// Another replica's prune racing the next listing: right after a page with a deprecated version not yet
    /// scheduled is served, that version is scheduled, so the client's own request for it conflicts (409).
    /// </summary>
    public void RaceNextListing(string instanceId) => this.SecretFor(instanceId).RaceListing = true;

    /// <summary>Holds the next write of the instance's secret before it is applied, until the returned release runs.</summary>
    public (Task Reached, Action Release) StallNextWrite(string instanceId) => Stall.Arm(held => this.SecretFor(instanceId).WriteStall = held);

    private Secret SecretFor(string instanceId) => this.Named(OciSigningKeyVault.SecretName(SecretName, instanceId));

    private Secret Named(string name) => this.secrets.GetOrAdd(name, _ => new Secret(this.VaultId, name));

    private static string NewPrivateKeyPem()
    {
        using var key = RSA.Create(2048);
        return key.ExportRSAPrivateKeyPem();
    }

    private static async Task<Uri> StartServerAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (!await SignedAsync(context.Request))
            {
                await Results.Json(new { code = "NotAuthenticated", message = "The signature is invalid." }, statusCode: StatusCodes.Status401Unauthorized).ExecuteAsync(context);
                return;
            }

            await next(context);
        });
        // GetSecretBundleByName is a POST with its parameters in the query, as OCI serves it; a GET finds no route.
        app.MapPost("/20190301/secretbundles/actions/getByName", async (string secretName, string vaultId) =>
        {
            if (!Vaults.TryGetValue(vaultId, out var vault))
            {
                return NotFound();
            }

            var secret = vault.Named(secretName);
            secret.CountRead();
            var answer = Serve(vaultId, "read", _ => Bundle(secret, vault.PublishDelayed ? Math.Max(1, secret.Versions.Count - 1) : null));
            await Stall.PassAsync(secret, read: true);
            return answer;
        });
        app.MapGet("/20190301/secretbundles/{secretId}", (string secretId, int? versionNumber) =>
            ServeSecret(secretId, "read", secret => Bundle(secret, versionNumber)));
        // Pages of two versions, newest first, continued through opc-next-page like OCI's listing.
        app.MapGet("/20180608/secrets/{secretId}/versions", (string secretId, int? page) =>
            ServeSecret(secretId, "read", secret =>
            {
                var start = page ?? 0;
                var listed = Enumerable.Range(1, secret.Versions.Count).Reverse().Skip(start).Take(2).Select(number => new
                {
                    secretId = secret.Id,
                    versionNumber = number,
                    stages = number == secret.Versions.Count ? CurrentStages : number == secret.Versions.Count - 1 ? PreviousStages : DeprecatedStages,
                    timeOfDeletion = secret.Scheduled.TryGetValue(number, out var deletion) ? deletion.ToString("O", CultureInfo.InvariantCulture) : null,
                }).ToList();
                if (secret.RaceListing && listed.FirstOrDefault(version => version.stages == DeprecatedStages && version.timeOfDeletion is null) is { } raced)
                {
                    secret.RaceListing = false;
                    secret.Scheduled[raced.versionNumber] = VaultOf(secret).Clock.GetUtcNow() + VaultOf(secret).DeletionDelay;
                }

                return new Paged(start + 2 < secret.Versions.Count ? (start + 2).ToString(CultureInfo.InvariantCulture) : null, Results.Json(listed));
            }));
        app.MapPost("/20180608/secrets/{secretId}/version/{number:int}/actions/scheduleDeletion", (string secretId, int number) =>
            ServeSecret(secretId, "write", secret =>
            {
                var vault = VaultOf(secret);
                var now = vault.Clock.GetUtcNow();
                if (number < 1 || number >= secret.Versions.Count - 1 || secret.Scheduled.ContainsKey(number))
                {
                    return Results.Json(new { code = "IncorrectState", message = "Only a deprecated version can be scheduled for deletion." }, statusCode: StatusCodes.Status409Conflict);
                }

                if (secret.Scheduled.Values.Count(deletion => deletion > now) >= vault.MaxPendingDeletions)
                {
                    return Results.Json(new { code = "LimitExceeded", message = "The secret has too many versions pending deletion." }, statusCode: StatusCodes.Status400BadRequest);
                }

                secret.Scheduled[number] = now + vault.DeletionDelay;
                return Results.Json(new { secretId = secret.Id, versionNumber = number });
            }));
        app.MapGet("/20180608/secrets/{secretId}", (string secretId) =>
            ServeSecret(secretId, "read", Metadata));
        app.MapPut("/20180608/secrets/{secretId}", async (string secretId, HttpRequest request) =>
        {
            var body = await JsonNode.ParseAsync(request.Body);
            var content = Convert.FromBase64String(body!["secretContent"]!["content"]!.GetValue<string>());
            var ifMatch = request.Headers.IfMatch.ToString();
            if (Vaults.Values.SelectMany(vault => vault.All).FirstOrDefault(secret => secret.Id == secretId) is { } target)
            {
                await Stall.PassAsync(target, read: false);
            }

            return ServeSecret(secretId, "write", secret =>
            {
                if (ifMatch.Length == 0)
                {
                    return Results.Json(new { code = "InvalidParameter", message = "This stand-in requires If-Match." }, statusCode: StatusCodes.Status400BadRequest);
                }

                if (secret.Applying)
                {
                    return Results.Json(new { code = "Conflict", message = "The secret is being updated." }, statusCode: StatusCodes.Status409Conflict);
                }

                if (ifMatch != secret.ETag)
                {
                    return Results.Json(new { code = "IncorrectState", message = "The ETag does not match." }, statusCode: StatusCodes.Status412PreconditionFailed);
                }

                if (secret.Versions.Count - secret.Scheduled.Count >= VaultOf(secret).MaxVersions)
                {
                    return Results.Json(new { code = "LimitExceeded", message = "The secret has too many versions." }, statusCode: StatusCodes.Status400BadRequest);
                }

                secret.Write(content, applying: true);
                return Metadata(secret, "UPDATING");
            });
        });
        await app.StartAsync();
        return new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
    }

    private static IResult Serve(string vaultId, string operation, Func<FakeOciVault, IResult> serve) =>
        !Vaults.TryGetValue(vaultId, out var vault) ? NotFound()
        : vault.Failing == operation ? Results.Json(new { code = "InternalServerError", message = "Injected failure." }, statusCode: 500)
        : new Dated(vault.Clock, serve(vault));

    private static IResult ServeSecret(string secretId, string operation, Func<Secret, IResult> serve)
    {
        var owner = Vaults.Values.FirstOrDefault(vault => vault.All.Any(secret => secret.Id == secretId));
        return owner is null ? NotFound() : Serve(owner.VaultId, operation, vault =>
        {
            var secret = vault.All.Single(candidate => candidate.Id == secretId);
            lock (secret)
            {
                return serve(secret);
            }
        });
    }

    // OCI request signing: the documented headers for the method, RSA-SHA256 under the bound API key.
    private static async Task<bool> SignedAsync(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        var fields = authorization.StartsWith("Signature ", StringComparison.Ordinal)
            ? authorization["Signature ".Length..].Split(',').Select(part => part.Split('=', 2)).Where(pair => pair.Length == 2)
                .ToDictionary(pair => pair[0], pair => pair[1].Trim('"'), StringComparer.Ordinal)
            : [];
        string[] required = request.Method == HttpMethods.Get
            ? ["(request-target)", "date", "host"]
            : ["(request-target)", "date", "host", "content-length", "content-type", "x-content-sha256"];
        if (fields.GetValueOrDefault("version") != "1" || fields.GetValueOrDefault("algorithm") != "rsa-sha256"
            || fields.GetValueOrDefault("keyId")?.Split('/').Length != 3 || fields.GetValueOrDefault("headers") != string.Join(' ', required)
            || fields.GetValueOrDefault("signature") is not { } signature)
        {
            return false;
        }

        if (request.Method != HttpMethods.Get)
        {
            request.EnableBuffering();
            using var body = new MemoryStream();
            await request.Body.CopyToAsync(body);
            request.Body.Position = 0;
            if (request.Headers["x-content-sha256"] != Convert.ToBase64String(SHA256.HashData(body.ToArray()))
                || request.ContentLength != body.Length)
            {
                return false;
            }
        }

        string Value(string header) => header switch
        {
            "(request-target)" => $"{request.Method.ToLowerInvariant()} {request.Path}{request.QueryString}",
            "host" => request.Host.Value!,
            "content-length" => request.ContentLength?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            "content-type" => request.ContentType ?? string.Empty,
            _ => request.Headers[header].ToString(),
        };
        using var key = RSA.Create();
        key.ImportFromPem(PrivateKeyPem);
        return key.VerifyData(Encoding.UTF8.GetBytes(string.Join('\n', required.Select(header => $"{header}: {Value(header)}"))),
            Convert.FromBase64String(signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    private static FakeOciVault VaultOf(Secret secret) => Vaults[secret.VaultId];

    private static IResult NotFound() =>
        Results.Json(new { code = "NotAuthorizedOrNotFound", message = "Not found." }, statusCode: StatusCodes.Status404NotFound);

    private static IResult Bundle(Secret secret, int? version)
    {
        lock (secret)
        {
            var number = version ?? secret.Versions.Count;
            return number < 1 || number > secret.Versions.Count ? NotFound() : Results.Json(new
            {
                secretId = secret.Id,
                versionNumber = number,
                timeCreated = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                secretBundleContent = new { contentType = "BASE64", content = Convert.ToBase64String(secret.Versions[number - 1]) },
            });
        }
    }

    // While a write is being applied, the metadata reports UPDATING once; the version is applied after that read.
    private static IResult Metadata(Secret secret)
    {
        var state = secret.Applying ? "UPDATING" : "ACTIVE";
        secret.Applying = false;
        return Metadata(secret, state);
    }

    private static SecretResult Metadata(Secret secret, string state) => new(secret.ETag, new
    {
        id = secret.Id,
        compartmentId = "ocid1.compartment.oc1..aaaatestcompartment",
        vaultId = secret.VaultId,
        keyId = "ocid1.key.oc1.sa-saopaulo-1.test.key",
        secretName = secret.Name,
        lifecycleState = state,
        currentVersionNumber = secret.Versions.Count,
        timeCreated = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
    });

    private sealed class Secret(string vaultId, string name)
    {
        public string Id { get; } = $"ocid1.vaultsecret.oc1.{Region}.secret{Guid.NewGuid():N}";

        public string VaultId => vaultId;

        public string Name => name;

        public List<byte[]> Versions { get; } = ["{}"u8.ToArray()];

        public string ETag { get; private set; } = Guid.NewGuid().ToString("N");

        public bool Applying { get; set; }

        // Version number → deletion time; until then the version is pending deletion, never in active use again.
        public Dictionary<int, DateTimeOffset> Scheduled { get; } = [];

        public int Reads => Volatile.Read(ref this.reads);

        public Stall? ReadStall { get; set; }

        public Stall? WriteStall { get; set; }

        public bool RaceListing { get; set; }

        private int reads;

        public void CountRead() => Interlocked.Increment(ref this.reads);

        public void Write(byte[] content, bool applying)
        {
            this.Versions.Add(content);
            this.ETag = Guid.NewGuid().ToString("N");
            this.Applying = applying;
        }
    }

    // A request held at one point until the test releases it.
    private sealed class Stall
    {
        private readonly TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static (Task Reached, Action Release) Arm(Action<Stall> arm)
        {
            var held = new Stall();
            arm(held);
            return (held.reached.Task, () => held.release.TrySetResult());
        }

        // Holds the request if its secret has a stall armed for it, consuming the stall.
        public static async Task PassAsync(Secret secret, bool read)
        {
            var held = read ? secret.ReadStall : secret.WriteStall;
            if (held is null)
            {
                return;
            }

            if (read)
            {
                secret.ReadStall = null;
            }
            else
            {
                secret.WriteStall = null;
            }

            held.reached.TrySetResult();
            await held.release.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    private sealed class Dated(TimeProvider clock, IResult inner) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.Date = clock.GetUtcNow().ToString("r", CultureInfo.InvariantCulture);
            return inner.ExecuteAsync(httpContext);
        }
    }

    private sealed class Paged(string? next, IResult inner) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            if (next is not null)
            {
                httpContext.Response.Headers["opc-next-page"] = next;
            }

            return inner.ExecuteAsync(httpContext);
        }
    }

    private sealed class SecretResult(string etag, object body) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.ETag = etag;
            return Results.Json(body).ExecuteAsync(httpContext);
        }
    }
}
