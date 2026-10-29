using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using TerraScale.Authentication.Hosting;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Tests;

/// <summary>
/// One real TerraBase process built from this same commit (<c>TERRABASE_BINARY</c>, set by the Makefile's auth test
/// targets), with a fresh data directory and fixture-owned front ports. Each child binds fresh backend ports (port 0,
/// read back from its startup log); transparent TCP relays keep the public HTTP and internal h2c origins stable across
/// restarts. The internal listener verifies this server's generated peer credential.
/// </summary>
public sealed class TerraBaseTestServer : IAsyncDisposable
{
    private const string PeerSecretVariable = "TERRABASE_PEER_SECRET";
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TerraScaleIdGenerator Ids = new();

    /// <summary>The peer credential of every TerraBase this suite starts and of every client it builds: 32 random bytes, hex.</summary>
    internal static readonly string PeerSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private readonly ConcurrentQueue<string> output = new();
    private readonly string directory;
    private readonly byte[] masterKey;
    private TestListenerRelay? publicFront;
    private TestListenerRelay? internalFront;
    private (int Internal, int Public, int Metrics) backendPorts;
    private Process? process;

    private readonly bool production;
    private readonly RegionalNode? node;

    private TerraBaseTestServer(string directory, bool production, RegionalNode? node = null)
    {
        this.directory = directory;
        this.production = production;
        this.node = node;
        this.masterKey = node is null ? RandomNumberGenerator.GetBytes(32) : Convert.FromBase64String(node.MasterKey);
    }

    internal (int Internal, int Public, int Metrics) BackendPorts => this.backendPorts;

    // Offline fault fixtures may modify only a stopped child's own root.
    internal string StoppedDataDirectory => this.process is null
        ? Path.Combine(this.directory, "data")
        : throw new InvalidOperationException("Stop TerraBase before modifying its storage files.");

    // Only stopped fixture storage is inspected; this synthetic master never comes from the environment.
    internal string StoppedDatabaseKey(params string[] relativePath)
    {
        var path = Path.Combine([this.StoppedDataDirectory, .. relativePath]) + ".key";
        var record = File.ReadAllLines(path);
        if (record.Length != 2 || record[0] != "TERRABASE-DB-KEY-1")
        {
            throw new InvalidDataException("The fixture database key record is malformed.");
        }

        var wrapped = Convert.FromBase64String(record[1]);
        if (wrapped.Length != 60)
        {
            throw new InvalidDataException("The fixture database key envelope is malformed.");
        }

        var key = new byte[32];
        try
        {
            using var aes = new AesGcm(this.masterKey, 16);
            aes.Decrypt(wrapped.AsSpan(0, 12), wrapped.AsSpan(12, 32), wrapped.AsSpan(44, 16), key);
            return Convert.ToHexString(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Origin of the public listener (Documents API with API keys and ts-auth tokens).</summary>
    public Uri ClientAddress => this.publicFront?.Address ?? throw new InvalidOperationException("TerraBase has not started.");

    /// <summary>Origin of the internal listener (HTTP/2 only), which needs the peer credential that <see cref="Http"/> sends.</summary>
    public Uri InternalAddress => this.internalFront?.Address ?? throw new InvalidOperationException("TerraBase has not started.");

    /// <summary>Origin of the metrics listener (Prometheus scrape, no credential).</summary>
    public Uri MetricsAddress => new($"http://127.0.0.1:{this.backendPorts.Metrics}/");

    /// <summary>A client that never follows redirects and sends the peer credential. Owned by the server.</summary>
    public HttpClient Http { get; } = AsTsAuth(new(new PeerCredentialHandler(PeerSecret)
    {
        InnerHandler = new BackendErrorDiagnostics { InnerHandler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false } },
    }));

    /// <summary>A plain client for the public listener. It never carries the internal credential.</summary>
    public HttpClient PublicHttp { get; } = AsTsAuth(new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }));

    // ts-auth's storage writes wait for another region's durable ack, as its production transport asks (TerraBaseTransport).
    private static HttpClient AsTsAuth(HttpClient client)
    {
        client.DefaultRequestHeaders.Add(TerraBaseTransport.DurabilityHeader, "remote_durable");
        return client;
    }

    /// <summary>Starts <c>TERRABASE_BINARY</c>; returns the reason instead when it names no file.</summary>
    public static Task<(TerraBaseTestServer? Server, string? Unavailable)> StartAsync(CancellationToken cancellationToken = default) =>
        StartAsync(production: false, cancellationToken);

    /// <summary>
    /// <see cref="StartAsync(CancellationToken)"/> in Production on a local host, with a backup bucket nothing answers for
    /// (backup ships in the background).
    /// </summary>
    public static Task<(TerraBaseTestServer? Server, string? Unavailable)> StartProductionAsync(CancellationToken cancellationToken = default) =>
        StartAsync(production: true, cancellationToken);

    internal static Task<(TerraBaseTestServer? Server, string? Unavailable)> StartRegionalAsync(RegionalNode node, CancellationToken cancellationToken = default) =>
        StartAsync(production: false, cancellationToken, node);

    private static async Task<(TerraBaseTestServer? Server, string? Unavailable)> StartAsync(bool production, CancellationToken cancellationToken, RegionalNode? node = null)
    {
        var binary = Environment.GetEnvironmentVariable("TERRABASE_BINARY");
        if (string.IsNullOrEmpty(binary) || !File.Exists(binary))
        {
            return (null, "TERRABASE_BINARY must name the TerraBase binary built from this commit (make auth-test-fast sets it)");
        }

        var server = new TerraBaseTestServer(Directory.CreateTempSubdirectory("terrabase-test-").FullName, production, node);
        try
        {
            await server.StartAsync(binary, cancellationToken);
            return (server, null);
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    /// <summary>A Management API client on the internal listener, acting as the platform operator.</summary>
    public TerraBaseManagementClient Management() => new(this.Http, new TerraBaseManagementClientOptions { BaseAddress = this.InternalAddress });

    /// <summary>A Documents API client on the client listener, authenticated with <paramref name="apiKey"/>.</summary>
    public TerraBaseDocumentsClient Documents(string apiKey) =>
        new(this.PublicHttp, new TerraBaseDocumentsClientOptions { BaseAddress = this.ClientAddress, ApiKey = apiKey });

    /// <summary>A Documents API client on the internal listener with the peer credential: how ts-auth reaches TerraBase.</summary>
    public TerraBaseDocumentsClient InternalDocuments() => new(this.Http, new TerraBaseDocumentsClientOptions { BaseAddress = this.InternalAddress });

    /// <summary>Issues an API key for <paramref name="organizationId"/> (<c>POST /admin/api-keys</c>, internal listener).</summary>
    public async Task<string> CreateApiKeyAsync(
        string organizationId, IReadOnlyList<string>? scopes = null, string? projectId = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(this.InternalAddress, "admin/api-keys"))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = JsonContent.Create(new
            {
                name = "integration test",
                scopes = scopes ?? ["documents:read", "documents:write"],
                organizationId,
                projectId,
            }),
        };
        using var response = await this.Http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return body.RootElement.GetProperty("secret").GetString()!;
    }

    /// <summary>
    /// A new organization (on the enterprise plan, as TerraScale's own platform organization is, so the suite's parallel
    /// load is not throttled like a hobby tenant), a project and a read/write API key, owned by a fresh user id.
    /// </summary>
    public async Task<TerraBaseTestTenant> CreateTenantAsync(CancellationToken cancellationToken = default)
    {
        var user = Ids.NewId();
        var management = this.Management();
        var organization = await management.CreateOrganizationAsync(user, "Org " + Guid.NewGuid().ToString("N")[..8], cancellationToken);
        organization = await management.SetPlanAsync(user, organization.Id, TerraBasePlan.Enterprise, cancellationToken);
        var project = await management.CreateProjectAsync(user, organization.Id, "Project", cancellationToken);
        return new(user, organization, project, await this.CreateApiKeyAsync(organization.Id, cancellationToken: cancellationToken));
    }

    /// <summary>Kills the process (a crash: no graceful shutdown) and keeps its data directory and front ports for <see cref="RestartAsync"/>.</summary>
    public async Task StopAsync()
    {
        this.RefuseNewConnections();
        if (this.process is not { } running)
        {
            await this.StopBackendsAsync();
            return;
        }

        try
        {
            running.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (running.HasExited)
        {
            // Exited between the check and the kill.
        }

        await running.WaitForExitAsync(CancellationToken.None);
        await this.StopBackendsAsync();
        this.process = null;
        running.Dispose();
    }

    /// <summary>Lets TerraBase drain and mark its data directory clean before a restart.</summary>
    public async Task StopCleanlyAsync()
    {
        this.RefuseNewConnections();
        if (this.process is not { } running)
        {
            await this.StopBackendsAsync();
            return;
        }

        try
        {
            var signal = new ProcessStartInfo("kill") { UseShellExecute = false };
            signal.ArgumentList.Add("-TERM");
            signal.ArgumentList.Add(running.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var sender = Process.Start(signal) ?? throw new InvalidOperationException("The shutdown signal did not start.");
            await sender.WaitForExitAsync();
            if (sender.ExitCode != 0)
            {
                throw new InvalidOperationException("The shutdown signal failed.");
            }

            await running.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            if (running.ExitCode != 0)
            {
                throw new InvalidOperationException($"TerraBase shutdown failed with exit code {running.ExitCode}.");
            }
        }
        catch
        {
            await this.StopAsync();
            throw;
        }

        await this.StopBackendsAsync();
        this.process = null;
        running.Dispose();
    }

    /// <summary>Hashes a stopped tenant copy without opening Turso or exposing its encrypted documents.</summary>
    public async Task<IReadOnlyDictionary<string, string>> SnapshotDatabaseFilesAsync(string organization, string project, string database)
    {
        if (this.process is not null)
        {
            throw new InvalidOperationException("Stop TerraBase before capturing its database files.");
        }

        var path = Path.Combine(this.directory, "data", organization, project, database + ".db");
        var hashes = new Dictionary<string, string>();
        foreach (var suffix in (string[])["", "-wal", "-journal", ".key"])
        {
            if (suffix.Length == 0 || File.Exists(path + suffix))
            {
                hashes.Add(suffix, await HashAsync(path + suffix));
            }
        }

        return hashes;
    }

    /// <summary>Checks the exact original tenant image remains at its one retained rebuild path.</summary>
    public async Task<bool> RetainedDatabaseMatchesAsync(
        string organization, string project, string database, IReadOnlyDictionary<string, string> hashes)
    {
        var parent = Path.Combine(this.directory, "data", organization, project);
        var copies = Directory.GetFiles(parent, $".{database}.*.rebuild");
        if (copies.Length != 1)
        {
            return false;
        }

        foreach (var suffix in (string[])["", "-wal", "-journal", ".key"])
        {
            var path = copies[0] + suffix;
            if (File.Exists(path) != hashes.ContainsKey(suffix)
                || (hashes.TryGetValue(suffix, out var hash) && await HashAsync(path) != hash))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file));
    }

    /// <summary>Starts the stopped server again on the same data directory, front ports and credential.</summary>
    public Task RestartAsync(CancellationToken cancellationToken = default) =>
        this.process is null
            ? this.StartAsync(Environment.GetEnvironmentVariable("TERRABASE_BINARY")!, cancellationToken)
            : throw new InvalidOperationException("TerraBase is running");

    /// <summary>Stops the process and deletes its data directory.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await this.StopAsync();
        }
        finally
        {
            try
            {
                await Task.WhenAll(
                    this.publicFront?.DisposeAsync().AsTask() ?? Task.CompletedTask,
                    this.internalFront?.DisposeAsync().AsTask() ?? Task.CompletedTask);
            }
            finally
            {
                this.Http.Dispose();
                this.PublicHttp.Dispose();
                if (this.process is null)
                {
                    Directory.Delete(this.directory, recursive: true);
                }
            }
        }
    }

    private async Task StartAsync(string binary, CancellationToken cancellationToken)
    {
        try
        {
            this.publicFront ??= new TestListenerRelay();
            this.internalFront ??= new TestListenerRelay();
            var start = new ProcessStartInfo(binary)
            {
                WorkingDirectory = this.directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            // The test process's own ts-orch, Tigris and TerraBase variables never reach this child.
            foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("TS_", StringComparison.Ordinal) || name.StartsWith("AWS_", StringComparison.Ordinal)
                || name.StartsWith("TERRABASE_", StringComparison.Ordinal) || name is "BUCKET_NAME" or "PRIMARY_REGION").ToList())
            {
                start.Environment.Remove(name);
            }

            // This fixture credential is never inherited from the invoking environment.
            start.Environment[PeerSecretVariable] = PeerSecret;
            if (this.node is { } regional)
            {
                start.Environment["TS_REGION"] = regional.Region;
                start.Environment["TS_NODE"] = regional.Name;
                start.Environment["TS_PEERS_FILE"] = regional.PeersFile;
                start.Environment["AWS_ENDPOINT_URL_S3"] = regional.Bucket.ToString();
                start.Environment["AWS_REGION"] = "auto";
                start.Environment["BUCKET_NAME"] = "ts757";
                start.Environment["AWS_ACCESS_KEY_ID"] = "test";
                start.Environment["AWS_SECRET_ACCESS_KEY"] = "test";
                start.Environment["TERRABASE_MASTER_KEY"] = regional.MasterKey;
                start.Environment["SSL_CERT_FILE"] = regional.TrustFile;
            }

            if (this.production)
            {
                // Production admission tests require backup settings; no test contacts a production bucket.
                start.Environment["AWS_ENDPOINT_URL_S3"] = "https://127.0.0.1:1";
                start.Environment["AWS_REGION"] = "auto";
                start.Environment["BUCKET_NAME"] = "terrabase-test";
                start.Environment["AWS_ACCESS_KEY_ID"] = "test";
                start.Environment["AWS_SECRET_ACCESS_KEY"] = "test";
            }

            // Database encryption needs the same test master across restarts, independently of backups.
            // Secrets arrive as files with a trailing newline, as OCI Vault mounts them; each file wins over a malformed
            // variable, which would stop the start if it were read.
            var secrets = Directory.CreateDirectory(Path.Combine(this.directory, "secrets")).FullName;
            var masterKeyFile = Path.Combine(secrets, "master-key");
            if (!File.Exists(masterKeyFile))
            {
                File.WriteAllText(masterKeyFile, Convert.ToBase64String(this.masterKey) + "\n");
            }
            File.WriteAllText(Path.Combine(secrets, "peer-secret"), PeerSecret + "\n");
            start.Environment["TERRABASE_MASTER_KEY_FILE"] = Path.Combine(secrets, "master-key");
            start.Environment[PeerSecretVariable] = "not-a-peer-secret";
            start.Environment["TERRABASE_MASTER_KEY"] = "not-a-key";
            start.Environment[PeerSecretVariable + "_FILE"] = Path.Combine(secrets, "peer-secret");

            // All three backend ports are freshly owned by this child, including metrics on every restart.
            start.ArgumentList.Add(this.production ? "--environment=Production" : "--environment=Development");
            start.ArgumentList.Add("--port=0");
            start.ArgumentList.Add($"--internal-port={this.node?.Port ?? 0}");
            start.ArgumentList.Add("--metrics-port=0");
            start.ArgumentList.Add("--postgres-port=0");
            start.ArgumentList.Add($"--data-directory={Path.Combine(this.directory, "data")}");
            var listening = new ConcurrentQueue<Uri>();
            var bound = new TaskCompletionSource<Uri[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var running = Process.Start(start) ?? throw new InvalidOperationException("TerraBase did not start.");
            this.process = running;
            running.OutputDataReceived += (_, line) => this.Keep(line.Data, listening, bound);
            running.ErrorDataReceived += (_, line) => this.Keep(line.Data, listening, bound);
            running.Exited += (_, _) => bound.TrySetResult([]);
            running.EnableRaisingEvents = true;
            running.BeginOutputReadLine();
            running.BeginErrorReadLine();
            if (running.HasExited)
            {
                bound.TrySetResult([]);
            }

            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(StartupTimeout);
            try
            {
                var addresses = await bound.Task.WaitAsync(startup.Token);
                if (addresses.Length != 3)
                {
                    await running.WaitForExitAsync(CancellationToken.None);
                    throw new InvalidOperationException($"TerraBase exited with {running.ExitCode}: {string.Join('\n', this.output)}");
                }

                // Bound in order: internal, public, metrics. Never probe the front or a previous child's backend.
                this.backendPorts = (addresses[0].Port, addresses[1].Port, addresses[2].Port);
                using var health = await this.PublicHttp.GetAsync(new Uri(addresses[1], "healthz"), startup.Token);
                if (health.StatusCode != HttpStatusCode.OK || running.HasExited)
                {
                    throw new InvalidOperationException($"TerraBase startup health failed: {health.StatusCode}: {string.Join('\n', this.output)}");
                }

                this.publicFront.SetBackend(this.backendPorts.Public);
                this.internalFront.SetBackend(this.backendPorts.Internal, this.node?.Address);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"TerraBase was not healthy within {StartupTimeout.TotalSeconds} seconds: {string.Join('\n', this.output)}");
            }
        }
        catch
        {
            await this.StopAsync();
            throw;
        }
    }

    private void RefuseNewConnections()
    {
        this.publicFront?.RefuseNewConnections();
        this.internalFront?.RefuseNewConnections();
    }

    private async Task StopBackendsAsync()
    {
        this.backendPorts = default;
        await Task.WhenAll(
            this.publicFront?.StopBackendAsync() ?? Task.CompletedTask,
            this.internalFront?.StopBackendAsync() ?? Task.CompletedTask);
    }

    private void Keep(string? line, ConcurrentQueue<Uri> listening, TaskCompletionSource<Uri[]> bound)
    {
        if (line is null)
        {
            return;
        }

        this.output.Enqueue(line);
        if (this.node is not null && line.StartsWith('{'))
        {
            try
            {
                using var entry = JsonDocument.Parse(line);
                var fields = entry.RootElement;
                if (fields.TryGetProperty("level", out var level) && fields.TryGetProperty("msg", out var message)
                    && (level.GetString() is "WARN" or "ERROR" or "CRITICAL" || message.GetString() == "Application is shutting down..."))
                {
                    // Templates only: retain backend readiness failures without payloads or credentials.
                    Console.Error.WriteLine($"TerraBase {DateTimeOffset.UtcNow:O} {this.node.Region}: {level.GetString()} {message.GetString()}");
                }
            }
            catch (JsonException)
            {
                // Diagnostics must not interrupt the child's output reader.
            }
        }

        if (Listening(line) is { } address)
        {
            listening.Enqueue(address);
            if (listening.Count >= 3)
            {
                bound.TrySetResult(listening.Take(3).ToArray());
            }
        }

        while (this.output.Count > 200 && this.output.TryDequeue(out _))
        {
        }
    }

    // Kestrel's "Now listening on: {address}" line, one per listener in binding order, in TerraBase's JSON log format.
    private static Uri? Listening(string line)
    {
        var json = line.IndexOf('{', StringComparison.Ordinal);
        if (json < 0 || !line.Contains("Now listening on", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using var entry = JsonDocument.Parse(line.AsMemory(json));
            // Outside Development the public and metrics listeners bind every interface ([::]); reach them on loopback.
            return entry.RootElement.TryGetProperty("address", out var address)
                && Uri.TryCreate(address.GetString()?.Replace("[::]", "127.0.0.1", StringComparison.Ordinal), UriKind.Absolute, out var uri) ? uri : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal sealed class BackendErrorDiagnostics : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if ((int)response.StatusCode >= 500)
        {
            string? code = null;
            try
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                if (body.RootElement.ValueKind == JsonValueKind.Object && body.RootElement.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var value))
                {
                    code = value.GetString();
                }
            }
            catch (JsonException)
            {
                // Non-JSON failures still retain their status; the SDK receives the original buffered content.
            }

            Console.Error.WriteLine($"TerraBase {DateTimeOffset.UtcNow:O} {request.Method}: {(int)response.StatusCode}, code={code ?? "unavailable"}, retry-after={response.Headers.RetryAfter}");
        }

        return response;
    }
}

internal sealed record RegionalNode(string Region, string Name, IPAddress Address, int Port, string PeersFile, Uri Bucket, string TrustFile, string MasterKey);

/// <summary>Sends the peer credential on every request; the header name is spelled out here so a drift from production fails the tests.</summary>
internal sealed class PeerCredentialHandler(string peerSecret) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove("X-TerraBase-Peer-Credential");
        request.Headers.Add("X-TerraBase-Peer-Credential", peerSecret);
        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>A tenant created by <see cref="TerraBaseTestServer.CreateTenantAsync"/>.</summary>
/// <param name="UserId">The owner's user id.</param>
/// <param name="Organization">The organization.</param>
/// <param name="Project">A project of the organization.</param>
/// <param name="ApiKey">A read/write API key for the organization.</param>
public sealed record TerraBaseTestTenant(string UserId, TerraBaseOrganization Organization, TerraBaseProject Project, string ApiKey)
{
    /// <inheritdoc />
    public override string ToString() => $"TerraBaseTestTenant {{ UserId = {this.UserId}, Organization = {this.Organization.Id}, Project = {this.Project.Id}, ApiKey = [REDACTED] }}";
}
