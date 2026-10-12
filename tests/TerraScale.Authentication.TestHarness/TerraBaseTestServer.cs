using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Tests;

/// <summary>
/// One real TerraBase process built from this same commit (<c>TERRABASE_BINARY</c>, set by the Makefile's auth test
/// targets) on free loopback ports, with a fresh data directory, a generated self-signed certificate for <c>localhost</c>
/// and a generated management service credential. Share one per test session and dispose it at the end.
/// </summary>
public sealed partial class TerraBaseTestServer : IAsyncDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TerraScaleIdGenerator Ids = new();

    private readonly ConcurrentQueue<string> output = new();
    private readonly string directory;
    private readonly HttpClient admin = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(10) };
    private Process? process;
    private int adminPort;
    private int clientPort;

    // Listeners the current process announced; both are up at 2.
    private int listening;

    private TerraBaseTestServer(string directory, X509Certificate2 certificate)
    {
        this.directory = directory;
        this.Certificate = certificate;
        var trusted = certificate.RawData;
        this.Http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            SslOptions = { RemoteCertificateValidationCallback = (_, presented, _, _) => presented is not null && presented.GetRawCertData().AsSpan().SequenceEqual(trusted) },
        });
    }

    /// <summary>HTTPS origin of the client listener (Documents API).</summary>
    public Uri ClientAddress => new($"https://localhost:{this.clientPort}/");

    /// <summary>HTTP origin of the loopback administrative listener, the only one that accepts <see cref="ServiceCredential"/>.</summary>
    public Uri AdminAddress => new($"http://127.0.0.1:{this.adminPort}/");

    /// <summary>The management service credential the administrative listener accepts.</summary>
    public string ServiceCredential { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>A client that trusts only this server's certificate and never follows redirects. Owned by the server.</summary>
    public HttpClient Http { get; }

    /// <summary>The server's self-signed certificate.</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>Starts <c>TERRABASE_BINARY</c>; returns the reason instead when it names no file.</summary>
    public static async Task<(TerraBaseTestServer? Server, string? Unavailable)> StartAsync(CancellationToken cancellationToken = default)
    {
        var binary = Environment.GetEnvironmentVariable("TERRABASE_BINARY");
        if (string.IsNullOrEmpty(binary) || !File.Exists(binary))
        {
            return (null, "TERRABASE_BINARY must name the TerraBase binary built from this commit (make auth-test-fast sets it)");
        }

        var server = new TerraBaseTestServer(Directory.CreateTempSubdirectory("terrabase-test-").FullName, CreateCertificate());
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

    /// <summary>A Management API client on the administrative listener, acting with <see cref="ServiceCredential"/>.</summary>
    public TerraBaseManagementClient Management() =>
        new(this.admin, new TerraBaseManagementClientOptions { BaseAddress = this.AdminAddress, ServiceCredential = this.ServiceCredential });

    /// <summary>A Documents API client on the client listener, authenticated with <paramref name="apiKey"/>.</summary>
    public TerraBaseDocumentsClient Documents(string apiKey) =>
        new(this.Http, new TerraBaseDocumentsClientOptions { BaseAddress = this.ClientAddress, ApiKey = apiKey });

    /// <summary>Issues an API key for <paramref name="organizationId"/> (<c>POST /admin/api-keys</c>, administrative listener).</summary>
    public async Task<string> CreateApiKeyAsync(
        string organizationId, IReadOnlyList<string>? scopes = null, string? projectId = null, CancellationToken cancellationToken = default)
    {
        using var response = await this.admin.PostAsJsonAsync(new Uri(this.AdminAddress, "admin/api-keys"), new
        {
            name = "integration test",
            scopes = scopes ?? ["documents:read", "documents:write"],
            organizationId,
            projectId,
        }, cancellationToken);
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

    /// <summary>Kills the process (a crash: no graceful shutdown) and keeps its data directory and ports for <see cref="RestartAsync"/>.</summary>
    public async Task StopAsync()
    {
        if (this.process is not { } running)
        {
            return;
        }

        this.process = null;
        try
        {
            running.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (running.HasExited)
        {
            // Exited between the check and the kill.
        }

        await running.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        running.Dispose();
    }

    /// <summary>Starts the stopped server again on the same data directory, ports and credential.</summary>
    // shortcut: another process could take a released port before the restart binds it; fine for loopback test ports.
    public Task RestartAsync(CancellationToken cancellationToken = default) =>
        this.process is null
            ? this.StartAsync(Environment.GetEnvironmentVariable("TERRABASE_BINARY")!, cancellationToken)
            : throw new InvalidOperationException("TerraBase is running");

    /// <summary>Stops the process and deletes its data directory.</summary>
    public async ValueTask DisposeAsync()
    {
        await this.StopAsync();
        this.Http.Dispose();
        this.admin.Dispose();
        this.Certificate.Dispose();
        Directory.Delete(this.directory, recursive: true);
    }

    private async Task StartAsync(string binary, CancellationToken cancellationToken)
    {
        var certificatePath = Path.Combine(this.directory, "client.crt");
        var keyPath = Path.Combine(this.directory, "client.key");
        var credentialPath = Path.Combine(this.directory, "management-credential");
        await File.WriteAllTextAsync(certificatePath, this.Certificate.ExportCertificatePem(), cancellationToken);
        using (var key = this.Certificate.GetECDsaPrivateKey()!)
        {
            await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem(), cancellationToken);
        }

        await File.WriteAllTextAsync(credentialPath, this.ServiceCredential, cancellationToken);
        var (adminPort, clientPort) = (Volatile.Read(ref this.adminPort), Volatile.Read(ref this.clientPort));
        var start = new ProcessStartInfo(binary)
        {
            WorkingDirectory = this.directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add($"--admin-port={adminPort}");
        start.ArgumentList.Add($"--client-port={clientPort}");
        start.ArgumentList.Add($"--client-certificate={certificatePath}");
        start.ArgumentList.Add($"--client-private-key={keyPath}");
        start.ArgumentList.Add($"--management-credential-file={credentialPath}");
        start.ArgumentList.Add($"--data-directory={Path.Combine(this.directory, "data")}");
        Volatile.Write(ref this.listening, 0);
        this.process = Process.Start(start) ?? throw new InvalidOperationException("TerraBase did not start.");
        this.process.OutputDataReceived += (_, line) => this.Keep(line.Data);
        this.process.ErrorDataReceived += (_, line) => this.Keep(line.Data);
        this.process.BeginOutputReadLine();
        this.process.BeginErrorReadLine();

        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(StartupTimeout);
        while (true)
        {
            if (this.process.HasExited)
            {
                throw new InvalidOperationException($"TerraBase exited with {this.process.ExitCode}: {string.Join('\n', this.output)}");
            }

            if (Volatile.Read(ref this.listening) == 2)
            {
                try
                {
                    using var health = await this.admin.GetAsync(new Uri(this.AdminAddress, "healthz"), startup.Token);
                    if (health.StatusCode == HttpStatusCode.OK)
                    {
                        return;
                    }
                }
                catch (HttpRequestException)
                {
                    // Not listening yet.
                }
            }

            try
            {
                await Task.Delay(100, startup.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"TerraBase was not healthy within {StartupTimeout.TotalSeconds} seconds: {string.Join('\n', this.output)}");
            }
        }
    }

    // TerraBase logs each listener as one JSON event whose address field holds its URL: loopback http is the
    // administrative listener, https the client listener.
    private void Keep(string? line)
    {
        if (line is null)
        {
            return;
        }

        if (line.Contains("Now listening on", StringComparison.Ordinal) && ListenAddress().Match(line) is { Success: true } match
            && Uri.TryCreate(match.Value, UriKind.Absolute, out var address) && address.Port > 0)
        {
            if (address.Scheme == Uri.UriSchemeHttp && address.IsLoopback)
            {
                Volatile.Write(ref this.adminPort, address.Port);
                Interlocked.Increment(ref this.listening);
            }
            else if (address.Scheme == Uri.UriSchemeHttps)
            {
                Volatile.Write(ref this.clientPort, address.Port);
                Interlocked.Increment(ref this.listening);
            }
        }

        this.output.Enqueue(line);
        while (this.output.Count > 200 && this.output.TryDequeue(out _))
        {
        }
    }

    [GeneratedRegex(@"https?://[^\s""]+")]
    private static partial Regex ListenAddress();

    private static X509Certificate2 CreateCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(1));
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
