using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.DotNet;
using TerraScale.EntityFrameworkCore;

namespace TerraScale.EntityFrameworkCore.Tests;

public sealed class TerraScaleEntitySourceTests
{
    [Test]
    public async Task UpsertAsync_UsesRepositoryAnnotationAndPrimaryKeyAsDocumentId()
    {
        using var httpClient = new HttpClient(new StubHandler(async request =>
        {
            await Assert.That(request.Method).IsEqualTo(HttpMethod.Put);
            await Assert.That(request.RequestUri?.PathAndQuery)
                .IsEqualTo("/api/v1/repositories/orders/documents/orders/1");

            var json = await request.Content!.ReadAsStringAsync();
            using var document = JsonDocument.Parse(json);
            await Assert.That(document.RootElement.GetProperty("data").GetProperty("status").GetString())
                .IsEqualTo("paid");

            return Json(HttpStatusCode.OK, """
                {
                  "repository": "orders",
                  "id": "orders/1",
                  "data": { "id": "orders/1", "status": "paid", "quantity": 2 },
                  "lastModified": "2026-06-10T00:00:00+00:00",
                  "changeVector": "A:1"
                }
                """);
        }));
        await using var context = new OrdersContext(CreateOptions<OrdersContext>());
        var set = context.TerraScaleSet<Order>(CreateClient(httpClient));

        var result = await set.UpsertAsync(new Order
        {
            Id = "orders/1",
            Status = "paid",
            Quantity = 2
        });

        await Assert.That(result.Id).IsEqualTo("orders/1");
    }

    [Test]
    public async Task ListAsync_MaterializesEntitiesFromDocumentData()
    {
        using var httpClient = new HttpClient(new StubHandler(async request =>
        {
            await Assert.That(request.Method).IsEqualTo(HttpMethod.Get);
            await Assert.That(request.RequestUri?.PathAndQuery)
                .IsEqualTo("/api/v1/repositories/orders/documents?skip=0&take=20");

            return Json(HttpStatusCode.OK, """
                {
                  "repository": "orders",
                  "skip": 0,
                  "take": 20,
                  "total": 1,
                  "items": [
                    {
                      "repository": "orders",
                      "id": "orders/1",
                      "data": { "id": "orders/1", "status": "paid", "quantity": 2 },
                      "lastModified": "2026-06-10T00:00:00+00:00",
                      "changeVector": "A:1"
                    }
                  ]
                }
                """);
        }));
        await using var context = new OrdersContext(CreateOptions<OrdersContext>());
        var set = context.TerraScaleSet<Order>(CreateClient(httpClient));

        var orders = await set.ListAsync(take: 20);

        await Assert.That(orders.Count).IsEqualTo(1);
        await Assert.That(orders.Single().Id).IsEqualTo("orders/1");
        await Assert.That(orders.Single().Status).IsEqualTo("paid");
        await Assert.That(orders.Single().Quantity).IsEqualTo(2);
    }

    [Test]
    public async Task QueryAsync_SendsTqlAndMaterializesEntities()
    {
        using var httpClient = new HttpClient(new StubHandler(async request =>
        {
            await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
            await Assert.That(request.RequestUri?.PathAndQuery)
                .IsEqualTo("/api/v1/repositories/orders/tql");

            var json = await request.Content!.ReadAsStringAsync();
            using var requestDocument = JsonDocument.Parse(json);
            await Assert.That(requestDocument.RootElement.GetProperty("query").GetString())
                .IsEqualTo("from 'orders'");

            return Json(HttpStatusCode.OK, """
                {
                  "repository": "orders",
                  "skip": 0,
                  "take": 10,
                  "total": 1,
                  "items": [
                    {
                      "repository": "orders",
                      "id": "orders/1",
                      "data": { "id": "orders/1", "status": "paid", "quantity": 2 },
                      "lastModified": "2026-06-10T00:00:00+00:00",
                      "changeVector": "A:1"
                    }
                  ]
                }
                """);
        }));
        await using var context = new OrdersContext(CreateOptions<OrdersContext>());
        var set = context.TerraScaleSet<Order>(CreateClient(httpClient));

        var orders = await set.QueryAsync("from 'orders'", take: 10);

        await Assert.That(orders.Count).IsEqualTo(1);
        await Assert.That(orders.Single().Status).IsEqualTo("paid");
    }

    [Test]
    public async Task DeleteAsync_UsesPrimaryKeyAsDocumentId()
    {
        using var httpClient = new HttpClient(new StubHandler(async request =>
        {
            await Assert.That(request.Method).IsEqualTo(HttpMethod.Delete);
            await Assert.That(request.RequestUri?.PathAndQuery)
                .IsEqualTo("/api/v1/repositories/orders/documents/orders/1");

            return Json(HttpStatusCode.OK, """
                {
                  "repository": "orders",
                  "id": "orders/1",
                  "deleted": true
                }
                """);
        }));
        await using var context = new OrdersContext(CreateOptions<OrdersContext>());
        var set = context.TerraScaleSet<Order>(CreateClient(httpClient));

        var result = await set.DeleteAsync("orders/1");

        await Assert.That(result.Deleted).IsTrue();
    }

    [Test]
    public async Task UseTerraScaleSource_UsesRepositoryResolverWhenNoAnnotationIsConfigured()
    {
        using var httpClient = new HttpClient(new StubHandler(async request =>
        {
            await Assert.That(request.Method).IsEqualTo(HttpMethod.Get);
            await Assert.That(request.RequestUri?.PathAndQuery)
                .IsEqualTo("/api/v1/repositories/resolved_products/documents?skip=5&take=10");

            return Json(HttpStatusCode.OK, """
                {
                  "repository": "resolved_products",
                  "skip": 5,
                  "take": 10,
                  "total": 0,
                  "items": []
                }
                """);
        }));
        var options = CreateOptions<ProductContext>(builder => builder.UseTerraScaleSource(
            terraScale => terraScale.RepositoryNameResolver = _ => "resolved_products"));
        await using var context = new ProductContext(options);
        var set = context.TerraScaleSet<Product>(CreateClient(httpClient));

        var products = await set.ListAsync(skip: 5, take: 10);

        await Assert.That(products.Count).IsEqualTo(0);
        await Assert.That(set.RepositoryName).IsEqualTo("resolved_products");
    }

    [Test]
    public async Task SaveChangesAsync_PersistsTrackedChangesToTerraScale()
    {
        var requests = new List<HttpRequestMessage>();
        using var httpClient = new HttpClient(new StubHandler(async request =>
        {
            requests.Add(CloneRequest(request));
            var json = await request.Content!.ReadAsStringAsync();
            using var document = JsonDocument.Parse(json);
            var data = document.RootElement.GetProperty("data");

            return Json(HttpStatusCode.Created, $$"""
                {
                  "repository": "orders",
                  "id": "{{data.GetProperty("id").GetString()}}",
                  "data": {
                    "id": "{{data.GetProperty("id").GetString()}}",
                    "status": "{{data.GetProperty("status").GetString()}}",
                    "quantity": {{data.GetProperty("quantity").GetInt32()}}
                  },
                  "lastModified": "2026-06-10T00:00:00+00:00",
                  "changeVector": "A:1"
                }
                """);
        }));
        await using var context = new OrdersContext(CreateOptions<OrdersContext>());
        var order = new Order
        {
            Id = "orders/2",
            Status = "queued",
            Quantity = 3
        };
        context.Add(order);
        var source = context.AsTerraScaleSource(CreateClient(httpClient));

        var result = await source.SaveChangesAsync();

        await Assert.That(result.Created).IsEqualTo(1);
        await Assert.That(result.Total).IsEqualTo(1);
        await Assert.That(context.Entry(order).State).IsEqualTo(EntityState.Unchanged);
        await Assert.That(requests.Single().Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(requests.Single().RequestUri?.PathAndQuery)
            .IsEqualTo("/api/v1/repositories/orders/documents");
    }

    [Test]
    public async Task AddTerraScaleEntityFrameworkCore_RegistersSourceForDbContext()
    {
        var services = new ServiceCollection();
        services.AddDbContext<OrdersContext>(options =>
            options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
        services.AddTerraScaleEntityFrameworkCore<OrdersContext>(options =>
        {
            options.BaseAddress = new Uri("https://api.example.test");
            options.ApiKey = "tsauth_secret";
            options.ProjectId = "project_123";
        });
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var source = scope.ServiceProvider.GetRequiredService<TerraScaleEntitySource<OrdersContext>>();

        await Assert.That(source.Set<Order>().RepositoryName).IsEqualTo("orders");
    }

    private static DbContextOptions<TContext> CreateOptions<TContext>(
        Action<DbContextOptionsBuilder<TContext>>? configure = null)
        where TContext : DbContext
    {
        var builder = new DbContextOptionsBuilder<TContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"));
        configure?.Invoke(builder);
        return builder.Options;
    }

    private static TerraScaleClient CreateClient(HttpClient httpClient) =>
        new(
            httpClient,
            new TerraScaleClientOptions
            {
                BaseAddress = new Uri("https://api.example.test"),
                ApiKey = "tsauth_secret",
                ProjectId = "project_123"
            });

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string json) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request) =>
        new(request.Method, request.RequestUri);

    private sealed class OrdersContext(DbContextOptions<OrdersContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>(builder =>
            {
                builder.HasKey(order => order.Id);
                builder.ToTerraScaleRepository("orders");
            });
        }
    }

    private sealed class ProductContext(DbContextOptions<ProductContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Product>(builder => builder.HasKey(product => product.Id));
        }
    }

    private sealed class Order
    {
        public string Id { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public int Quantity { get; set; }
    }

    private sealed class Product
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handle(request);
    }
}
