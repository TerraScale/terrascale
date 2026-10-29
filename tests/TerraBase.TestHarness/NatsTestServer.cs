using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;
using NATS.NKeys;

namespace TerraBase.Testing;

public sealed partial class NatsTestServer : IAsyncDisposable
{
    public const string Bucket = "terrabase_cdn_budget";

    private static readonly string[] RolePublish =
    [
        $"$KV.{Bucket}.>", $"$JS.API.STREAM.INFO.KV_{Bucket}", $"$JS.API.STREAM.MSG.GET.KV_{Bucket}",
        $"$JS.API.DIRECT.GET.KV_{Bucket}", $"$JS.API.DIRECT.GET.KV_{Bucket}.>",
    ];

    private static readonly string[] RoleSubscribe = ["_INBOX.>"];

    private static readonly Dictionary<string, object> AccountLimits = new(StringComparer.Ordinal)
    {
        ["subs"] = -1,
        ["data"] = -1,
        ["payload"] = -1,
        ["imports"] = -1,
        ["exports"] = -1,
        ["wildcards"] = true,
        ["conn"] = -1,
        ["leaf"] = -1,
        ["mem_storage"] = -1,
        ["disk_storage"] = -1,
        ["streams"] = -1,
        ["consumer"] = -1,
    };

    private readonly DirectoryInfo _directory;
    private readonly Process _process;
    private readonly string _admin;

    private NatsTestServer(DirectoryInfo directory, Process process, string url, string admin)
    {
        _directory = directory;
        _process = process;
        Url = url;
        _admin = admin;
    }

    public string Url { get; }

    public static async Task<NatsTestServer> StartAsync(CancellationToken cancellationToken)
    {
        var executable = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory, "nats-server")).FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("nats-server must be on PATH for the shared purge budget tests");
        var directory = Directory.CreateTempSubdirectory("terrabase-nats-");
        Process? process = null;
        try
        {
            using var operatorKey = KeyPair.CreatePair(PrefixByte.Operator);
            using var systemKey = KeyPair.CreatePair(PrefixByte.Account);
            using var accountKey = KeyPair.CreatePair(PrefixByte.Account);
            var operatorJwt = Jwt(operatorKey, operatorKey, new { type = "operator", version = 2 });
            var systemJwt = Jwt(operatorKey, systemKey, new { type = "account", version = 2 });
            var accountJwt = Jwt(operatorKey, accountKey, new { limits = AccountLimits, type = "account", version = 2 });
            Write(directory, "operator.jwt", operatorJwt);
            Write(directory, "admin.creds", Creds(accountKey, new { pub = new { }, sub = new { }, subs = -1, data = -1, payload = -1, type = "user", version = 2 }));
            Write(directory, "role.creds", Creds(accountKey, new
            {
                pub = new { allow = RolePublish },
                sub = new { allow = RoleSubscribe },
                subs = -1,
                data = -1,
                payload = -1,
                type = "user",
                version = 2,
            }));
            Certificates(directory);
            Write(directory, "server.conf", $$"""
                host: 127.0.0.1
                port: -1
                server_name: terrabase-test
                ports_file_dir: "{{directory.FullName}}"
                jetstream { store_dir: "{{Path.Combine(directory.FullName, "js")}}", max_memory_store: 64MB, max_file_store: 256MB }
                operator: "{{Path.Combine(directory.FullName, "operator.jwt")}}"
                system_account: {{systemKey.GetPublicKey()}}
                resolver: MEMORY
                resolver_preload: { {{systemKey.GetPublicKey()}}: {{systemJwt}}, {{accountKey.GetPublicKey()}}: {{accountJwt}} }
                tls {
                    cert_file: "{{Path.Combine(directory.FullName, "server.pem")}}"
                    key_file: "{{Path.Combine(directory.FullName, "server.key")}}"
                    ca_file: "{{Path.Combine(directory.FullName, "ca.pem")}}"
                    verify: true
                    handshake_first: true
                    timeout: 5
                }
                """);
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(Path.Combine(directory.FullName, "server.conf"));
            process = Process.Start(start) ?? throw new InvalidOperationException("nats-server did not start");
            process.OutputDataReceived += static (_, _) => { };
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var url = await PortAsync(directory, process, cancellationToken);
            var server = new NatsTestServer(directory, process, url, Path.Combine(directory.FullName, "admin.creds"));
            await using var admin = server.Connect(server._admin);
            _ = await new NatsKVContext(new NatsJSContext(admin)).CreateStoreAsync(
                new NatsKVConfig(Bucket) { History = 1, Storage = NatsKVStorageType.File }, cancellationToken);
            return server;
        }
        catch
        {
            if (process is not null)
            {
                await StopAsync(process);
            }

            directory.Delete(recursive: true);
            throw;
        }
    }

    public void Configure(Dictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings["nats-url"] = Url;
        settings["nats-credentials-file"] = Path.Combine(_directory.FullName, "role.creds");
        settings["nats-certificate"] = Path.Combine(_directory.FullName, "client.pem");
        settings["nats-private-key"] = Path.Combine(_directory.FullName, "client.key");
        settings["nats-ca"] = Path.Combine(_directory.FullName, "ca.pem");
    }

    public async Task<DateTimeOffset> NextPurgeAsync(CancellationToken cancellationToken)
    {
        await using var admin = Connect(_admin);
        var store = await new NatsKVContext(new NatsJSContext(admin)).GetStoreAsync(Bucket, cancellationToken);
        var entry = await store.TryGetEntryAsync<byte[]>("purge.6cd3f3639e928077593d9b470765efeb", cancellationToken: cancellationToken);
        return entry.Success
            ? DateTimeOffset.FromUnixTimeMilliseconds(System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(entry.Value.Value))
            : throw new InvalidOperationException("the shared purge budget holds no entry");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(_process);
        _process.Dispose();
        _directory.Delete(recursive: true);
    }

    private NatsConnection Connect(string creds) => new(new NatsOpts
    {
        Url = Url,
        AuthOpts = new NatsAuthOpts { CredsFile = creds },
        TlsOpts = new NatsTlsOpts
        {
            Mode = TlsMode.Implicit,
            ConfigureClientAuthentication = options =>
            {
                var authority = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(_directory.FullName, "ca.pem"));
                options.ClientCertificates = [X509Certificate2.CreateFromPemFile(Path.Combine(_directory.FullName, "client.pem"),
                    Path.Combine(_directory.FullName, "client.key"))];
                options.CertificateChainPolicy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
                options.CertificateChainPolicy.CustomTrustStore.Add(authority);
                return ValueTask.CompletedTask;
            },
        },
    });

    private static async Task<string> PortAsync(DirectoryInfo directory, Process process, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"nats-server exited with code {process.ExitCode}");
            }

            if (directory.EnumerateFiles("*.ports").FirstOrDefault() is { } ports)
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(ports.FullName, deadline.Token));
                var address = document.RootElement.GetProperty("nats")[0].GetString()!;
                return "tls://127.0.0.1:" + PortPattern().Match(address).Groups[1].Value;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
        }
    }

    private static async Task StopAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        await process.WaitForExitAsync(CancellationToken.None);
    }

    private static string Creds(KeyPair account, object claims)
    {
        using var user = KeyPair.CreatePair(PrefixByte.User);
        return $"""
            -----BEGIN NATS USER JWT-----
            {Jwt(account, user, claims)}
            ------END NATS USER JWT------

            -----BEGIN USER NKEY SEED-----
            {user.GetSeed()}
            ------END USER NKEY SEED------

            """;
    }

    private static string Jwt(KeyPair issuer, KeyPair subject, object nats)
    {
        var header = Base64Url("""{"typ":"JWT","alg":"ed25519-nkey"}"""u8.ToArray());
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["jti"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
            ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["iss"] = issuer.GetPublicKey(),
            ["sub"] = subject.GetPublicKey(),
            ["name"] = subject.GetPublicKey()[..8],
            ["nats"] = nats,
        }));
        var signature = new byte[64];
        issuer.Sign(Encoding.ASCII.GetBytes(header + "." + payload), signature);
        return header + "." + payload + "." + Base64Url(signature);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void Certificates(DirectoryInfo directory)
    {
        using var authorityKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var authorityRequest = new CertificateRequest("CN=terrabase-test-nats-ca", authorityKey, HashAlgorithmName.SHA256);
        authorityRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        authorityRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var authority = authorityRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        Write(directory, "ca.pem", authority.ExportCertificatePem());
        Issue(directory, authority, "server", "1.3.6.1.5.5.7.3.1", san: true);
        Issue(directory, authority, "client", "1.3.6.1.5.5.7.3.2", san: false);
    }

    private static void Issue(DirectoryInfo directory, X509Certificate2 authority, string name, string usage, bool san)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=terrabase-test-nats-{name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(usage)], false));
        if (san)
        {
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
        }

        using var certificate = request.Create(authority, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30), RandomNumberGenerator.GetBytes(16));
        Write(directory, name + ".pem", certificate.ExportCertificatePem());
        Write(directory, name + ".key", key.ExportPkcs8PrivateKeyPem());
    }

    private static void Write(DirectoryInfo directory, string name, string content) =>
        File.WriteAllText(Path.Combine(directory.FullName, name), content);

    [GeneratedRegex(@":(\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex PortPattern();
}
