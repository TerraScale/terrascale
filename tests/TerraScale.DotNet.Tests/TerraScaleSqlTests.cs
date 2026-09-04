using TerraScale.DotNet;
using System.Net;
using System.Text.Json;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleSqlTests
{
    [Test]
    public async Task SelectBuilder_BindsComparisonsMatchOrderingAndPagination()
    {
        var name = new TerraScaleFieldDefinition("name", TerraScaleFieldType.String, indexed: true);
        var age = new TerraScaleFieldDefinition("age", TerraScaleFieldType.Int, indexed: true);
        var schema = new TerraScaleCollectionSchema("users", [name, age]);

        var query = TerraScaleSql.Select(schema)
            .Column(name)
            .WhereEquals(age, 18)
            .WhereMatch(name, "alice")
            .OrderBy(age, TerraScaleSqlSortDirection.Descending)
            .Limit(10)
            .Offset(5)
            .Build();

        await Assert.That(query.Sql).IsEqualTo(
            "SELECT name FROM users WHERE age = $1 AND name MATCH $2 ORDER BY age DESC LIMIT $3 OFFSET $4");
        await Assert.That(query.Parameters).IsEquivalentTo(new object?[] { 18, "alice", 10, 5 });
        await Assert.That(query.ExecuteDescriptor().Sql).IsEqualTo(query.Sql);
        await Assert.That(query.SubscribeDescriptor().Parameters).IsEquivalentTo(query.Parameters);
    }

    [Test]
    public async Task SelectBuilder_BindsJsonPathAndRejectsUndeclaredOrNonTextFields()
    {
        var name = new TerraScaleFieldDefinition("name", TerraScaleFieldType.String);
        var age = new TerraScaleFieldDefinition("age", TerraScaleFieldType.Int);
        var schema = new TerraScaleCollectionSchema("users", [name, age]);
        var query = TerraScaleSql.Select(schema)
            .WhereJsonEquals("$.address.city", "Sao Paulo")
            .OrderByJson("$.address.city")
            .Build();

        await Assert.That(query.Sql).IsEqualTo(
            "SELECT * FROM users WHERE body ->> '$.address.city' = $1 ORDER BY body ->> '$.address.city' ASC");
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            TerraScaleSql.Select(schema).Column(new TerraScaleFieldDefinition("other", TerraScaleFieldType.String));
            await Task.CompletedTask;
        });
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            TerraScaleSql.Select(schema).WhereMatch(age, "18");
            await Task.CompletedTask;
        });
    }

    [Test]
    public async Task RawQuery_RejectsUnsafeSqlAndChecksBindings()
    {
        var accepted = TerraScaleSqlQuery.Raw("SELECT * FROM users WHERE id = $1", "doc-1");
        await Assert.That(accepted.Parameters).Count().IsEqualTo(1);
        await Assert.That(TerraScaleSqlQuery.Raw("WITH recent AS (SELECT * FROM users) SELECT * FROM recent").Sql)
            .IsEqualTo("WITH recent AS (SELECT * FROM users) SELECT * FROM recent");

        foreach (var statement in new[]
        {
            "INSERT INTO users VALUES ($1)",
            "SELECT * FROM users; SELECT * FROM users",
            "SELECT * FROM users -- DROP TABLE users",
            "SELECT random() FROM users",
            "SELECT CURRENT_TIMESTAMP FROM users",
            "WITH RECURSIVE recent AS (SELECT * FROM users) SELECT * FROM recent",
            "SELECT sqlite_master FROM users",
        })
        {
            await Assert.ThrowsAsync<ArgumentException>(async () =>
            {
                TerraScaleSqlQuery.Raw(statement, "x");
                await Task.CompletedTask;
            });
        }

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            TerraScaleSqlQuery.Raw("SELECT * FROM users WHERE id = $1", Array.Empty<object?>());
            await Task.CompletedTask;
        });
        await Assert.That(TerraScaleSqlQuery.TryRaw("DROP TABLE users", [], out var rejected)).IsFalse();
        await Assert.That(rejected).IsNull();
    }

    [Test]
    public async Task NamedBindingsAndMetadataFollowTheSqlContract()
    {
        var parameters = TerraScaleSqlParameters.Named([
            new TerraScaleSqlParameter("status", "paid"),
            new TerraScaleSqlParameter("minimum", 10),
        ]);
        var query = TerraScaleSqlQuery.Raw(
            "SELECT _id, body FROM orders WHERE status = :status AND total > :minimum",
            parameters);
        await Assert.That(query.Bindings.Keys).IsEquivalentTo(new[] { "status", "minimum" });

        var metadataQuery = TerraScaleSql.Select(new TerraScaleSqlSchema("orders"))
            .Column(TerraScaleSqlFields.Id)
            .OrderBy(TerraScaleSqlFields.Updated)
            .Build();
        await Assert.That(metadataQuery.Sql).IsEqualTo("SELECT _id FROM orders ORDER BY _updated ASC");
    }

    [Test]
    public async Task SqlBuilder_EnforcesServerBounds()
    {
        var schema = new TerraScaleSqlSchema("users");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => { TerraScaleSql.Select(schema).Limit(0); await Task.CompletedTask; });
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => { TerraScaleSql.Select(schema).Limit(1025); await Task.CompletedTask; });
        await Assert.ThrowsAsync<ArgumentException>(async () => { TerraScaleSqlQuery.Raw(new string('x', 16 * 1024 + 1)); await Task.CompletedTask; });
    }

    [Test]
    public async Task SqlClient_SendsContractRequestAndReadsRows()
    {
        using var handler = new SqlHandler();
        using var httpClient = new HttpClient(handler);
        var client = new TerraScaleClient(httpClient, new TerraScaleClientOptions
        {
            BaseAddress = new Uri("https://api.example.invalid/"),
            ApiKey = "test-key",
        });
        var result = await client.Sql.ExecuteAsync(
            "database/a", TerraScaleSqlQuery.Raw("SELECT * FROM users WHERE id = $1", "doc-1"),
            new TerraScaleSqlExecuteOptions { TimeoutMs = 5000 });
        await Assert.That(handler.RequestUri).IsEqualTo("https://api.example.invalid/api/v1/databases/database%2Fa/sql");
        await Assert.That(handler.Method).IsEqualTo(HttpMethod.Post);
        using var body = JsonDocument.Parse(handler.Body!);
        await Assert.That(body.RootElement.GetProperty("sql").GetString()).IsEqualTo("SELECT * FROM users WHERE id = $1");
        await Assert.That(body.RootElement.GetProperty("parameters").GetProperty("1").GetString()).IsEqualTo("doc-1");
        await Assert.That(body.RootElement.GetProperty("timeout_ms").GetInt32()).IsEqualTo(5000);
        await Assert.That(result.Matched).IsEqualTo(1);
        await Assert.That(result.Rows[0].Id).IsEqualTo("doc-1");
    }

    private sealed class SqlHandler : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            Method = request.Method;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"matched\":1,\"rows\":[{\"id\":\"doc-1\",\"value\":{\"name\":\"Alice\"}}]}")
            };
        }
    }
}
