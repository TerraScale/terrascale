using System.Net;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.DotNet;
using TerraScale.DotNet.Tests;
using TerraScale.EntityFrameworkCore;

namespace TerraScale.EntityFrameworkCore.Tests;

public sealed class TerraScaleEntitySourceE2ETests
{
    [Test]
    [RequiresTerraScaleApi]
    public async Task EntitySet_UpsertFindQueryAndDelete_RoundTripsAgainstRealRepository()
    {
        var client = TerraScaleApiE2E.CreateClient();
        var id = $"ef-e2e/{Guid.NewGuid():N}";

        await using var context = new OrdersContext(CreateOptions<OrdersContext>());
        var set = context.TerraScaleSet<Order>(client);

        var upserted = await set.UpsertAsync(new Order
        {
            Id = id,
            Status = "paid",
            Quantity = 2
        });

        try
        {
            await Assert.That(upserted.Id).IsEqualTo(id);

            var found = await set.FindAsync(id);
            await Assert.That(found).IsNotNull();
            var foundOrder = found
                ?? throw new InvalidOperationException("The upserted order could not be found in the real repository.");
            await Assert.That(foundOrder.Status).IsEqualTo("paid");

            var queried = await set.QueryAsync($"from '{TerraScaleApiE2E.Repository}'", take: 100);
            await Assert.That(queried.Any(order => order.Id == id)).IsTrue();
        }
        finally
        {
            await DeleteIfPresentAsync(client, TerraScaleApiE2E.Repository, id);
        }
    }

    [Test]
    [RequiresTerraScaleApi]
    public async Task RegisteredSource_ListsRealRepositoryThroughDependencyInjection()
    {
        var services = new ServiceCollection();
        services.AddDbContext<OrdersContext>(options => options.UseTerraScaleSource());
        services.AddTerraScaleEntityFrameworkCore<OrdersContext>(options =>
        {
            options.BaseAddress = TerraScaleApiE2E.BaseAddress
                ?? throw new InvalidOperationException(TerraScaleApiE2E.SkipReason);
            options.ApiKey = TerraScaleApiE2E.ApiKey;
            options.ProjectId = TerraScaleApiE2E.ProjectId;
            options.RepositoryNameResolver = _ => TerraScaleApiE2E.Repository;
        });
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var source = scope.ServiceProvider.GetRequiredService<TerraScaleEntitySource<OrdersContext>>();
        var orders = await source.Set<Order>().ListAsync(take: 1);

        await Assert.That(orders).IsNotNull();
    }

    [Test]
    [RequiresTerraScaleBearerToken]
    public async Task RegisteredSource_UsesBearerTokenProviderThroughDependencyInjection()
    {
        var providerCalls = 0;
        var services = new ServiceCollection();
        services.AddDbContext<OrdersContext>(options => options.UseTerraScaleSource());
        services.AddTerraScaleEntityFrameworkCore<OrdersContext>(options =>
        {
            options.BaseAddress = TerraScaleApiE2E.BaseAddress
                ?? throw new InvalidOperationException(TerraScaleApiE2E.BearerSkipReason);
            options.BearerTokenProvider = cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref providerCalls);
                return ValueTask.FromResult(TerraScaleApiE2E.BearerToken);
            };
            options.RepositoryNameResolver = _ => TerraScaleApiE2E.Repository;
        });
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var source = scope.ServiceProvider.GetRequiredService<TerraScaleEntitySource<OrdersContext>>();
        var orders = await source.Set<Order>().ListAsync(take: 1);

        await Assert.That(orders).IsNotNull();
        await Assert.That(providerCalls > 0).IsTrue();
    }

    [Test]
    [RequiresTerraScaleApi]
    public async Task EntitySet_FindMissingDocument_ReturnsNullFromRealRepository()
    {
        await using var context = new OrdersContext(CreateOptions<OrdersContext>());
        var set = context.TerraScaleSet<Order>(TerraScaleApiE2E.CreateClient());

        var found = await set.FindAsync($"ef-e2e-missing/{Guid.NewGuid():N}");

        await Assert.That(found).IsNull();
    }

    private static DbContextOptions<TContext> CreateOptions<TContext>(
        Action<DbContextOptionsBuilder<TContext>>? configure = null)
        where TContext : DbContext
    {
        var builder = new DbContextOptionsBuilder<TContext>()
            .UseTerraScaleSource(options => options.RepositoryNameResolver = _ => TerraScaleApiE2E.Repository);
        configure?.Invoke(builder);
        return builder.Options;
    }

    private static async Task DeleteIfPresentAsync(
        TerraScaleClient client,
        string repository,
        string documentId)
    {
        try
        {
            await client.Documents.DeleteAsync(repository, documentId);
        }
        catch (TerraScaleApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    private sealed class OrdersContext(DbContextOptions<OrdersContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>(builder =>
            {
                builder.HasKey(order => order.Id);
                builder.ToTerraScaleRepository(TerraScaleApiE2E.Repository);
            });
        }
    }

    private sealed class Order
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("quantity")]
        public int Quantity { get; set; }
    }
}
