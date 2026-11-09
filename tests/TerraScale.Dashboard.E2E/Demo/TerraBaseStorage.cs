using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.E2E.Demo;

public sealed class TerraBaseStorage : IAsyncDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);
    private static readonly Lock SharedGate = new();
    private static Task<TerraBaseStorage>? shared;
    private readonly string directory = Directory.CreateTempSubdirectory("ts-dashboard-terrabase-").FullName;
    private readonly ConcurrentQueue<string> output = new();
    private readonly TerraScaleIdGenerator ids = new();
    private readonly HttpClient peer = new(new PeerCredential(TestIdentity.PeerSecret) { InnerHandler = TerraBaseHttp.CreateHandler() });
    private readonly HttpClient databases = new(TerraBaseHttp.CreateHandler());
    private readonly Lock projectGate = new();
    private Task<TerraBaseDatabaseClient>? project;
    private Task<IReadOnlyList<TerraBaseSqlStatement>>? schema;
    private string? projectId;
    private Process? process;

    public Uri InternalAddress { get; private set; } = null!;

    public Uri PublicAddress { get; private set; } = null!;

    public static Task<TerraBaseStorage> SharedAsync()
    {
        lock (SharedGate)
        {
            return shared ??= StartAsync();
        }
    }

    public static async Task DisposeSharedAsync()
    {
        Task<TerraBaseStorage>? running;
        lock (SharedGate)
        {
            running = shared;
            shared = null;
        }

        if (running is { IsCompletedSuccessfully: true })
        {
            await (await running).DisposeAsync();
        }
    }

    public static async Task<TerraBaseStorage> StartAsync()
    {
        var storage = new TerraBaseStorage();
        try
        {
            await storage.LaunchAsync();
            return storage;
        }
        catch
        {
            await storage.DisposeAsync();
            throw;
        }
    }

    public async Task<string> CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        var project = await Project().WaitAsync(cancellationToken);
        var name = "dashboard-" + ids.NewId().ToLowerInvariant();
        await project.Database(projectId!, name).CreateAsync(cancellationToken);
        await Database(name).BatchAsync(await SchemaAsync(), cancellationToken);
        return name;
    }

    private TerraBaseSqlDatabase Database(string name)
    {
        return new TerraBaseSqlClient(peer, new TerraBaseSqlClientOptions { BaseAddress = InternalAddress }).Database(projectId!, name);
    }

    private Task<IReadOnlyList<TerraBaseSqlStatement>> SchemaAsync()
    {
        lock (projectGate)
        {
            return schema ??= Task.Run<IReadOnlyList<TerraBaseSqlStatement>>(() =>
            {
                using var db = new DashboardDbContext(new DbContextOptionsBuilder<DashboardDbContext>().UseTerraBase(Database("schema")).Options);
                var script = db.GetService<IMigrator>().GenerateScript();
                return [.. script.Split(";" + Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(statement => statement.TrimEnd(';'))
                    .Where(statement => statement.Length > 0 && statement is not ("BEGIN TRANSACTION" or "COMMIT"))
                    .Select(statement => new TerraBaseSqlStatement(statement))];
            });
        }
    }

    private Task<TerraBaseDatabaseClient> Project()
    {
        lock (projectGate)
        {
            return project ??= CreateProjectAsync();
        }
    }

    private async Task<TerraBaseDatabaseClient> CreateProjectAsync()
    {
        var user = ids.NewId();
        var management = new TerraBaseManagementClient(peer, new TerraBaseManagementClientOptions { BaseAddress = InternalAddress });
        var organization = await management.CreateOrganizationAsync(user, "Dashboard " + user, CancellationToken.None);
        await management.SetPlanAsync(user, organization.Id, TerraBasePlan.Enterprise, CancellationToken.None);
        var created = await management.CreateProjectAsync(user, organization.Id, "Dashboard", CancellationToken.None);
        projectId = created.Id;
        var key = new JsonObject { ["name"] = "dashboard", ["scopes"] = new JsonArray("sql:read", "sql:write"), ["organizationId"] = organization.Id };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(InternalAddress, "admin/api-keys"))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent(key.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using var response = await peer.SendAsync(request, CancellationToken.None);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        var apiKey = body.RootElement.GetProperty("secret").GetString()!;
        return new TerraBaseDatabaseClient(databases, new TerraBaseDatabaseClientOptions { BaseAddress = PublicAddress, AccessTokenProvider = _ => ValueTask.FromResult(apiKey) });
    }

    public DashboardDbContext Open(string database)
    {
        return new(new DbContextOptionsBuilder<DashboardDbContext>()
            .UseTerraBase(new TerraBaseSqlClient(peer, new TerraBaseSqlClientOptions { BaseAddress = InternalAddress }).Database(projectId!, database)).Options);
    }

    public void Use(IServiceCollection services, string database)
    {
        var project = projectId!;
        services.Replace(ServiceDescriptor.Singleton(provider => new TerraBaseSqlClient(provider.GetRequiredService<DashboardStorage.Transport>().Client,
            new TerraBaseSqlClientOptions { BaseAddress = InternalAddress }).Database(project, database)));
    }

    public async ValueTask DisposeAsync()
    {
        if (process is { } running)
        {
            if (!running.HasExited)
            {
                running.Kill(entireProcessTree: true);
            }

            await running.WaitForExitAsync();
            running.Dispose();
        }

        peer.Dispose();
        databases.Dispose();
        Directory.Delete(directory, recursive: true);
    }

    private async Task LaunchAsync()
    {
        var binary = typeof(TerraBaseStorage).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "TerraBaseBinary").Value!;
        if (!File.Exists(binary))
        {
            throw new InvalidOperationException("Build scripts/dashboard/TerraScale.Dashboard.slnx: the E2E suite runs the TerraBase built from this commit at " + binary);
        }

        var secrets = Directory.CreateDirectory(Path.Combine(directory, "secrets")).FullName;
        await File.WriteAllTextAsync(Path.Combine(secrets, "master-key"), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) + "\n");
        await File.WriteAllTextAsync(Path.Combine(secrets, "peer-secret"), TestIdentity.PeerSecret + "\n");
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
        InternalAddress = addresses[0];
        PublicAddress = addresses[1];
    }

    private void Keep(string? line, ConcurrentQueue<Uri> listening, TaskCompletionSource bound)
    {
        if (line is null)
        {
            return;
        }

        output.Enqueue(line);
        while (output.Count > 200 && output.TryDequeue(out _))
        {
        }

        var json = line.IndexOf('{', StringComparison.Ordinal);
        if (json < 0 || !line.Contains("Now listening on", StringComparison.Ordinal))
        {
            return;
        }

        using var entry = JsonDocument.Parse(line.AsMemory(json));
        listening.Enqueue(new Uri(entry.RootElement.GetProperty("address").GetString()!.Replace("[::]", "127.0.0.1", StringComparison.Ordinal)));
        if (listening.Count >= 3)
        {
            bound.TrySetResult();
        }
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
