using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;
using TerraScale.EntityFrameworkCore.Tests;

namespace TerraScale.EntityFrameworkCore.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(launchCount: 3, warmupCount: 2, iterationCount: 10)]
public class SaveChangesBenchmarks : IAsyncDisposable
{
    private readonly TerraBaseServer server = new();
    private HttpClient? http;
    private DbContextOptions<AppDbContext>? options;

    [Params(1, 256)]
    public int MaxBatchSize { get; set; }

    [Params(10, 100)]
    public int Entities { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        await server.StartAsync();
        http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
        var database = await server.CreateDatabaseAsync(http, CancellationToken.None);
        options = new DbContextOptionsBuilder<AppDbContext>().UseTerraBase(database, terraBase => terraBase.MaxBatchSize(MaxBatchSize)).Options;
        await using var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();
    }

    [GlobalCleanup]
    public async ValueTask DisposeAsync()
    {
        http?.Dispose();
        await server.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Benchmark]
    public async Task<int> Insert()
    {
        await using var context = new AppDbContext(options!);
        for (var i = 0; i < Entities; i++)
        {
            var id = server.NewId();
            context.Accounts.Add(new Account
            {
                Id = id,
                Email = id + "@example.com",
                Version = server.NewId(),
                CreatedAt = DateTimeOffset.UtcNow,
                Preferences = new("dark", 50),
            });
        }

        return await context.SaveChangesAsync();
    }
}
