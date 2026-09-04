using TerraScale.DotNet;

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
}
