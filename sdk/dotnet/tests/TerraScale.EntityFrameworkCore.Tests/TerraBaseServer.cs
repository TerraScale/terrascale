using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.EntityFrameworkCore.Tests;

public sealed class TerraBaseServer : IAsyncDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private readonly string peerSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string directory = Directory.CreateTempSubdirectory("terrabase-efcore-").FullName;
    private readonly ConcurrentQueue<string> output = new();
    private readonly TerraScaleIdGenerator ids = new();
    private Process? process;
    private HttpClient? peer;
    private Uri? publicAddress;
    private Uri? internalAddress;

    public HttpClient Http { get; } = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });

    public string NewId() => ids.NewId();

    public async Task StartAsync()
    {
        var binary = Environment.GetEnvironmentVariable("TERRABASE_BINARY");
        if (string.IsNullOrEmpty(binary) || !File.Exists(binary))
        {
            throw new InvalidOperationException("TERRABASE_BINARY must name the TerraBase binary built from this commit; make sdk-dotnet-test-fast sets it.");
        }

        var secrets = Directory.CreateDirectory(Path.Combine(directory, "secrets")).FullName;
        await File.WriteAllTextAsync(Path.Combine(secrets, "master-key"), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) + "\n");
        await File.WriteAllTextAsync(Path.Combine(secrets, "peer-secret"), peerSecret + "\n");
        var start = new ProcessStartInfo(binary)
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("TS_", StringComparison.Ordinal) || name.StartsWith("AWS_", StringComparison.Ordinal)
            || name.StartsWith("TERRABASE_", StringComparison.Ordinal) || name is "BUCKET_NAME" or "PRIMARY_REGION").ToList())
        {
            start.Environment.Remove(name);
        }

        start.Environment["TERRABASE_MASTER_KEY_FILE"] = Path.Combine(secrets, "master-key");
        start.Environment["TERRABASE_PEER_SECRET_FILE"] = Path.Combine(secrets, "peer-secret");
        foreach (var argument in (string[])["--environment=Development", "--port=0", "--internal-port=0", "--metrics-port=0", "--postgres-port=0"])
        {
            start.ArgumentList.Add(argument);
        }

        start.ArgumentList.Add($"--data-directory={Path.Combine(directory, "data")}");
        var listening = new ConcurrentQueue<Uri>();
        var bound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = Process.Start(start) ?? throw new InvalidOperationException("TerraBase did not start.");
        process = running;
        running.OutputDataReceived += (_, line) => Keep(line.Data, listening, bound);
        running.ErrorDataReceived += (_, line) => Keep(line.Data, listening, bound);
        running.Exited += (_, _) => bound.TrySetException(new InvalidOperationException("TerraBase exited: " + string.Join('\n', output)));
        running.EnableRaisingEvents = true;
        running.BeginOutputReadLine();
        running.BeginErrorReadLine();
        await bound.Task.WaitAsync(StartupTimeout);
        var addresses = listening.ToArray();
        internalAddress = addresses[0];
        publicAddress = addresses[1];
        peer = new HttpClient(new PeerCredential(peerSecret) { InnerHandler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false } });
    }

    public async Task<TerraBaseSqlDatabase> CreateDatabaseAsync(HttpClient sql, CancellationToken cancellationToken)
    {
        var peerClient = peer ?? throw new InvalidOperationException("TerraBase has not started.");
        var user = NewId();
        var management = new TerraBaseManagementClient(peerClient, new TerraBaseManagementClientOptions { BaseAddress = internalAddress });
        var organization = await management.CreateOrganizationAsync(user, "EF " + user, cancellationToken);
        await management.SetPlanAsync(user, organization.Id, TerraBasePlan.Enterprise, cancellationToken);
        var project = await management.CreateProjectAsync(user, organization.Id, "EF", cancellationToken);
        var key = new JsonObject { ["name"] = "efcore", ["scopes"] = new JsonArray("sql:read", "sql:write"), ["organizationId"] = organization.Id };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(internalAddress!, "admin/api-keys"))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent(key.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using var response = await peerClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var apiKey = body.RootElement.GetProperty("secret").GetString()!;
        ValueTask<string> Token(CancellationToken _) => ValueTask.FromResult(apiKey);
        await new TerraBaseDatabaseClient(Http, new TerraBaseDatabaseClientOptions { BaseAddress = publicAddress, AccessTokenProvider = Token })
            .Database(project.Id, "app").CreateAsync(cancellationToken);
        return new TerraBaseSqlClient(sql, new TerraBaseSqlClientOptions { BaseAddress = publicAddress, AccessTokenProvider = Token })
            .Database(project.Id, "app");
    }

    public async ValueTask DisposeAsync()
    {
        if (process is { } running)
        {
            if (!running.HasExited) running.Kill(entireProcessTree: true);
            await running.WaitForExitAsync();
            running.Dispose();
        }

        peer?.Dispose();
        Http.Dispose();
        Directory.Delete(directory, recursive: true);
    }

    private void Keep(string? line, ConcurrentQueue<Uri> listening, TaskCompletionSource bound)
    {
        if (line is null) return;
        output.Enqueue(line);
        while (output.Count > 200 && output.TryDequeue(out _))
        {
        }

        var json = line.IndexOf('{', StringComparison.Ordinal);
        if (json < 0 || !line.Contains("Now listening on", StringComparison.Ordinal)) return;
        using var entry = JsonDocument.Parse(line.AsMemory(json));
        listening.Enqueue(new Uri(entry.RootElement.GetProperty("address").GetString()!.Replace("[::]", "127.0.0.1", StringComparison.Ordinal)));
        if (listening.Count >= 3) bound.TrySetResult();
    }

    private sealed class PeerCredential(string secret) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Add("X-TerraBase-Peer-Credential", secret);
            return base.SendAsync(request, cancellationToken);
        }
    }
}

public sealed class SqlRequestCounter() : DelegatingHandler(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
{
    private int executed;

    public int Executed => Volatile.Read(ref executed);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("/sql/execute", StringComparison.Ordinal)) Interlocked.Increment(ref executed);
        return base.SendAsync(request, cancellationToken);
    }
}
