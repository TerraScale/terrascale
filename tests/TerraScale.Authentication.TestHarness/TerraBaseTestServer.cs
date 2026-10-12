using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Tests;

/// <summary>
/// One real TerraBase process built from this same commit (<c>TERRABASE_BINARY</c>, set by the Makefile's auth test
/// targets) on free loopback ports with a fresh data directory: its public listener (plain HTTP, as behind Fly Proxy) and
/// its internal listener (h2c, loopback off Fly), where the platform operator needs no credential. Share one per test
/// session and dispose it at the end.
/// </summary>
public sealed class TerraBaseTestServer : IAsyncDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TerraScaleIdGenerator Ids = new();

    private readonly ConcurrentQueue<string> output = new();
    private readonly string directory;
    private readonly int publicPort = FreePort();
    private readonly int internalPort = FreePort();
    private readonly int metricsPort = FreePort();
    private Process? process;

    private TerraBaseTestServer(string directory) => this.directory = directory;

    /// <summary>Origin of the public listener (Documents API with API keys and ts-auth tokens).</summary>
    public Uri ClientAddress => new($"http://127.0.0.1:{this.publicPort}/");

    /// <summary>Origin of the internal listener (HTTP/2 only), where the platform operator needs no credential.</summary>
    public Uri InternalAddress => new($"http://127.0.0.1:{this.internalPort}/");

    /// <summary>A client that never follows redirects. Owned by the server.</summary>
    public HttpClient Http { get; } = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });

    /// <summary>Starts <c>TERRABASE_BINARY</c>; returns the reason instead when it names no file.</summary>
    public static async Task<(TerraBaseTestServer? Server, string? Unavailable)> StartAsync(CancellationToken cancellationToken = default)
    {
        var binary = Environment.GetEnvironmentVariable("TERRABASE_BINARY");
        if (string.IsNullOrEmpty(binary) || !File.Exists(binary))
        {
            return (null, "TERRABASE_BINARY must name the TerraBase binary built from this commit (make auth-test-fast sets it)");
        }

        var server = new TerraBaseTestServer(Directory.CreateTempSubdirectory("terrabase-test-").FullName);
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
        new(this.Http, new TerraBaseDocumentsClientOptions { BaseAddress = this.ClientAddress, ApiKey = apiKey });

    /// <summary>A Documents API client on the internal listener without a credential: how ts-auth reaches TerraBase on Fly.</summary>
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
    public Task RestartAsync(CancellationToken cancellationToken = default) =>
        this.process is null
            ? this.StartAsync(Environment.GetEnvironmentVariable("TERRABASE_BINARY")!, cancellationToken)
            : throw new InvalidOperationException("TerraBase is running");

    /// <summary>Stops the process and deletes its data directory.</summary>
    public async ValueTask DisposeAsync()
    {
        await this.StopAsync();
        this.Http.Dispose();
        Directory.Delete(this.directory, recursive: true);
    }

    private async Task StartAsync(string binary, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(binary)
        {
            WorkingDirectory = this.directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Development: no Tigris backup to attach in a test. The test process's own Fly, Tigris and TerraBase variables never reach it.
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("FLY_", StringComparison.Ordinal) || name.StartsWith("AWS_", StringComparison.Ordinal)
            || name.StartsWith("TERRABASE_", StringComparison.Ordinal) || name is "BUCKET_NAME" or "PRIMARY_REGION").ToList())
        {
            start.Environment.Remove(name);
        }

        start.ArgumentList.Add("--environment=Development");
        start.ArgumentList.Add($"--port={this.publicPort}");
        start.ArgumentList.Add($"--internal-port={this.internalPort}");
        start.ArgumentList.Add($"--metrics-port={this.metricsPort}");
        start.ArgumentList.Add($"--data-directory={Path.Combine(this.directory, "data")}");
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

            try
            {
                using var health = await this.Http.GetAsync(new Uri(this.ClientAddress, "healthz"), startup.Token);
                if (health.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
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

    private void Keep(string? line)
    {
        if (line is null)
        {
            return;
        }

        this.output.Enqueue(line);
        while (this.output.Count > 200 && this.output.TryDequeue(out _))
        {
        }
    }

    // shortcut: another process can take the port between this probe and TerraBase's bind (or a restart); rare on loopback.
    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
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
