using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Testing;

/// <summary>
/// One real TerraBase process for integration tests: a <see cref="TerraBaseRelease"/> binary on free loopback ports,
/// with a fresh data directory, a generated self-signed TLS certificate for <c>localhost</c> and a generated dashboard
/// service credential. Test-framework neutral: share one instance per test session and dispose it at the end.
/// </summary>
public sealed class TerraBaseTestServer : IAsyncDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentQueue<string> output = new();
    private readonly string directory;
    private readonly X509Certificate2 certificate;
    private readonly HttpClient admin;
    private readonly object disposalGate = new();
    private Task? disposal;
    private bool resourcesDisposed;
    private Process? process;
    private int assignedAdminPort;
    private int assignedClientPort;

    internal Func<Task>? BeforeKillAsync { get; set; }
    internal Func<Task>? BeforeDeleteDirectoryAsync { get; set; }
    internal TimeSpan ProcessExitTimeout { get; set; } = TimeSpan.FromSeconds(5);

    internal TerraBaseTestServer(string directory, X509Certificate2 certificate, int adminPort, int clientPort, string credential)
    {
        this.directory = directory;
        this.certificate = certificate;
        ServiceCredential = credential;
        ClientAddress = new Uri($"https://localhost:{clientPort}/");
        var adminAddress = new Uri($"http://127.0.0.1:{adminPort}/");
        var trusted = certificate.RawData;
        admin = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            BaseAddress = adminAddress,
            Timeout = TimeSpan.FromSeconds(10),
        };
        try
        {
            Http = new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                SslOptions = { RemoteCertificateValidationCallback = (_, presented, _, _) => presented is not null && presented.GetRawCertData().AsSpan().SequenceEqual(trusted) },
            });
        }
        catch
        {
            admin.Dispose();
            throw;
        }
    }

    /// <summary>HTTPS origin of the client listener (Documents and Management APIs).</summary>
    public Uri ClientAddress { get; private set; }

    /// <summary>The dashboard service credential the server accepts on the Management API.</summary>
    public string ServiceCredential { get; }

    /// <summary>A client that trusts only this server's certificate and never follows redirects. Owned by the server.</summary>
    public HttpClient Http { get; }

    /// <summary>The server's self-signed certificate, for callers that build their own <see cref="HttpClient"/>.</summary>
    public X509Certificate2 Certificate => certificate;

    /// <summary>
    /// Resolves <paramref name="release"/> (default <see cref="TerraBaseRelease.Pinned"/>) and starts it. Returns
    /// <see langword="null"/> with the reason when the binary is unavailable here (see <see cref="TerraBaseRelease.ResolveAsync(CancellationToken)"/>);
    /// callers usually skip their tests then, and fail them in CI.
    /// </summary>
    /// <remarks>
    /// If startup cleanup also fails, the original exception contains the cleanup failure in
    /// <c>Data["TerraBaseCleanupException"]</c> and the server, when allocated, in
    /// <c>Data[nameof(TerraBaseTestServer)]</c>. Call its <see cref="DisposeAsync"/> to retry cleanup.
    /// </remarks>
    public static async Task<(TerraBaseTestServer? Server, string? Unavailable)> StartAsync(
        TerraBaseRelease? release = null, CancellationToken cancellationToken = default)
    {
        var (binary, unavailable) = await (release ?? TerraBaseRelease.Pinned).ResolveAsync(cancellationToken).ConfigureAwait(false);
        if (binary is null)
        {
            return (null, unavailable);
        }

        var directory = Directory.CreateTempSubdirectory("terrabase-test-").FullName;
        return (await StartAsync(binary, directory, CreateCertificate, static () => (0, 0), cancellationToken).ConfigureAwait(false), null);
    }

    internal static async Task<TerraBaseTestServer> StartAsync(
        string binary, string directory, Func<X509Certificate2> certificateFactory,
        Func<(int AdminPort, int ClientPort)> portFactory, CancellationToken cancellationToken,
        Action<TerraBaseTestServer>? afterProcessStarted = null)
    {
        X509Certificate2? certificate = null;
        TerraBaseTestServer? server = null;
        try
        {
            certificate = certificateFactory();
            var (adminPort, clientPort) = portFactory();
            server = new TerraBaseTestServer(
                directory, certificate, adminPort, clientPort, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
            await server.StartAsync(binary, cancellationToken, afterProcessStarted).ConfigureAwait(false);
            return server;
        }
        catch (Exception startupError)
        {
            try
            {
                if (server is not null)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    try
                    {
                        certificate?.Dispose();
                    }
                    finally
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                }
            }
            catch (Exception cleanupError)
            {
                startupError.Data["TerraBaseCleanupException"] = cleanupError;
                if (server is not null)
                {
                    // Preserve the owner so the caller can retry termination or directory cleanup.
                    startupError.Data[nameof(TerraBaseTestServer)] = server;
                }
            }

            throw;
        }
    }

    /// <summary>A Management API client authenticated with <paramref name="credential"/> (default <see cref="ServiceCredential"/>).</summary>
    public TerraBaseManagementClient Management(string? credential = null) =>
        new(Http, new TerraBaseManagementClientOptions { BaseAddress = ClientAddress, ServiceCredential = credential ?? ServiceCredential });

    /// <summary>A Documents API client authenticated with <paramref name="apiKey"/>.</summary>
    public TerraBaseDocumentsClient Documents(string apiKey) =>
        new(Http, new TerraBaseDocumentsClientOptions { BaseAddress = ClientAddress, ApiKey = apiKey });

    /// <summary>
    /// Issues an API key for <paramref name="organizationId"/> on the loopback admin listener
    /// (<c>POST /admin/api-keys</c>), the only way TerraBase issues them. Scopes default to read and write.
    /// </summary>
    public async Task<string> CreateApiKeyAsync(
        string organizationId, IReadOnlyList<string>? scopes = null, string? projectId = null, CancellationToken cancellationToken = default)
    {
        using var response = await admin.PostAsJsonAsync("admin/api-keys", new
        {
            name = "integration test",
            scopes = scopes ?? ["documents:read", "documents:write"],
            organizationId,
            projectId,
        }, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return body.RootElement.GetProperty("secret").GetString()!;
    }

    /// <summary>A new organization and project owned by a fresh user id, and an API key for the organization.</summary>
    public async Task<TerraBaseTestTenant> CreateTenantAsync(CancellationToken cancellationToken = default)
    {
        var user = "user-" + Guid.NewGuid().ToString("N");
        var management = Management();
        var organization = await management.CreateOrganizationAsync(user, "Org " + Guid.NewGuid().ToString("N")[..8], cancellationToken).ConfigureAwait(false);
        var project = await management.CreateProjectAsync(user, organization.Id, "Project", cancellationToken).ConfigureAwait(false);
        return new(user, organization, project, await CreateApiKeyAsync(organization.Id, cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Stops the process and deletes its data directory after exit is confirmed. Concurrent calls share an attempt;
    /// a termination failure or a five-second exit/output timeout retains resources for a later retry.
    /// A directory deletion failure retries only deletion.
    /// Completed cleanup is shared by repeated calls.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (disposalGate)
        {
            if (disposal is { IsFaulted: true })
            {
                disposal = null;
            }

            return new ValueTask(disposal ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        if (!resourcesDisposed)
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        if (BeforeKillAsync is { } beforeKill)
                        {
                            await beforeKill().ConfigureAwait(false);
                        }

                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    // The process can exit between HasExited and Kill.
                }

                using var exit = new CancellationTokenSource(ProcessExitTimeout);
                try
                {
                    await process.WaitForExitAsync(exit.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException exception) when (exit.IsCancellationRequested)
                {
                    throw new TimeoutException($"TerraBase process exit and output did not complete within {ProcessExitTimeout.TotalSeconds} seconds.", exception);
                }
            }

            process?.Dispose();
            Http.Dispose();
            admin.Dispose();
            certificate.Dispose();
            resourcesDisposed = true;
        }

        if (BeforeDeleteDirectoryAsync is { } beforeDelete)
        {
            await beforeDelete().ConfigureAwait(false);
        }

        Directory.Delete(directory, recursive: true);
    }

    private async Task StartAsync(
        string binary, CancellationToken cancellationToken, Action<TerraBaseTestServer>? afterProcessStarted)
    {
        var certificatePath = Path.Combine(directory, "client.crt");
        var keyPath = Path.Combine(directory, "client.key");
        var credentialPath = Path.Combine(directory, "dashboard-credential");
        await File.WriteAllTextAsync(certificatePath, certificate.ExportCertificatePem(), cancellationToken).ConfigureAwait(false);
        using (var key = certificate.GetECDsaPrivateKey()!)
        {
            await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem(), cancellationToken).ConfigureAwait(false);
        }

        await File.WriteAllTextAsync(credentialPath, ServiceCredential, cancellationToken).ConfigureAwait(false);
        var start = new ProcessStartInfo(binary)
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add($"--admin-port={admin.BaseAddress!.Port}");
        start.ArgumentList.Add($"--client-port={ClientAddress.Port}");
        start.ArgumentList.Add($"--client-certificate={certificatePath}");
        start.ArgumentList.Add($"--client-private-key={keyPath}");
        start.ArgumentList.Add($"--dashboard-credential-file={credentialPath}");
        start.ArgumentList.Add($"--data-directory={Path.Combine(directory, "data")}");

        process = Process.Start(start) ?? throw new InvalidOperationException("TerraBase did not start.");
        process.OutputDataReceived += (_, line) => Keep(line.Data);
        process.ErrorDataReceived += (_, line) => Keep(line.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        afterProcessStarted?.Invoke(this);

        try
        {
            await WaitForHealthAsync(async token =>
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException($"TerraBase exited with {process.ExitCode}: {string.Join('\n', output)}");
                }

                if (!DiscoverListenerAddresses())
                {
                    return false;
                }

                using var response = await admin.GetAsync("healthz", token).ConfigureAwait(false);
                return response.StatusCode == HttpStatusCode.OK;
            }, StartupTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"{exception.Message}: {string.Join('\n', output)}", exception);
        }
    }

    internal static async Task WaitForHealthAsync(
        Func<CancellationToken, Task<bool>> probe, TimeSpan startupTimeout, CancellationToken cancellationToken)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(startupTimeout);
        try
        {
            while (true)
            {
                startup.Token.ThrowIfCancellationRequested();
                try
                {
                    if (await probe(startup.Token).ConfigureAwait(false))
                    {
                        startup.Token.ThrowIfCancellationRequested();
                        return;
                    }
                }
                catch (HttpRequestException)
                {
                    // Not listening yet.
                }
                catch (OperationCanceledException) when (!startup.IsCancellationRequested)
                {
                    // HttpClient timed out this attempt; the startup deadline still applies.
                }

                await Task.Delay(100, startup.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && startup.IsCancellationRequested)
        {
            throw new TimeoutException($"TerraBase was not healthy within {startupTimeout.TotalSeconds} seconds");
        }
    }

    internal void Keep(string? line)
    {
        if (line is null)
        {
            return;
        }

        // Retain startup metadata before bounded diagnostics can discard the announcement.
        const string prefix = "Now listening on: ";
        var text = line.Trim();
        if (text.StartsWith(prefix, StringComparison.Ordinal)
            && Uri.TryCreate(text[prefix.Length..], UriKind.Absolute, out var address) && address.Port > 0)
        {
            if (address.Scheme == "http" && address.IsLoopback)
            {
                Interlocked.CompareExchange(ref assignedAdminPort, address.Port, 0);
            }
            else if (address.Scheme == "https")
            {
                Interlocked.CompareExchange(ref assignedClientPort, address.Port, 0);
            }
        }

        output.Enqueue(line);
        while (output.Count > 200 && output.TryDequeue(out _))
        {
        }
    }

    internal bool DiscoverListenerAddresses()
    {
        if (admin.BaseAddress!.Port != 0 && ClientAddress.Port != 0)
        {
            return true;
        }

        var adminPort = Volatile.Read(ref assignedAdminPort);
        var clientPort = Volatile.Read(ref assignedClientPort);
        if (adminPort == 0 || clientPort == 0)
        {
            return false;
        }

        admin.BaseAddress = new Uri($"http://127.0.0.1:{adminPort}/");
        ClientAddress = new Uri($"https://localhost:{clientPort}/");
        return true;
    }

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
    public override string ToString() => $"TerraBaseTestTenant {{ UserId = {UserId}, Organization = {Organization.Id}, Project = {Project.Id}, ApiKey = [REDACTED] }}";
}
