using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Features.Management.Usage;
using TerraBase.Tenancy;
using TerraBase.Testing;
using static TerraBase.Testing.SqlNativeDatabase;

namespace TerraBase.Fast.Sql;

public sealed class SqlMeteringTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task Sql_writes_are_billed_sql_reads_count_toward_fair_use_and_shape_reads_are_free()
    {
        await using var db = await StartAsync();
        await db.OkAsync("CREATE TABLE notes (id TEXT PRIMARY KEY, body TEXT NOT NULL)");
        await db.OkAsync(Stmt("INSERT INTO notes VALUES (?, ?), (?, ?), (?, ?)",
            Ulid.NewUlid().ToString(), Faker.Lorem.Sentence(), Ulid.NewUlid().ToString(), Faker.Lorem.Sentence(), Ulid.NewUlid().ToString(), Faker.Lorem.Sentence()));
        var read = await db.OkAsync("SELECT id, body FROM notes");
        await Assert.That(read[0].GetProperty("rows").GetArrayLength()).IsEqualTo(3);

        var shape = new TableShapeClient(db.Reader, db.Database.Data, "notes");
        await shape.SyncAsync();
        await Assert.That(shape.State.Count).IsEqualTo(3);

        await db.Api.Server.Services.GetRequiredService<UsageMeter>().FlushAsync(CancellationToken.None);
        var (page, usage) = await AllowanceAsync(db);
        await Assert.That(page.GetProperty("operationsContract").GetString()).IsEqualTo("write-units-4kib-global-v2");
        await Assert.That((usage.GetProperty("operations").GetInt64(), usage.GetProperty("sqlReads").GetInt64())).IsEqualTo((3L, 3L))
            .Because("three inserted rows bill three write units; the three selected rows count toward fair use; the shape read counts nothing");
    }

    [Test]
    public async Task Writes_stop_at_the_plan_cap_and_sql_reads_stop_at_fair_use()
    {
        await using var db = await StartAsync(services => services.AddSingleton<IOperationAllowance>(provider =>
            new OperationAllowance(provider.GetRequiredService<UsageMeter>(), provider.GetRequiredService<ITenantDirectory>(),
                (_, operation) => operation == MeteredOperation.Write ? 2 : 1)));
        await db.OkAsync("CREATE TABLE notes (id TEXT PRIMARY KEY, body TEXT NOT NULL)");
        await db.OkAsync(Stmt("INSERT INTO notes VALUES (?, ?), (?, ?)", Ulid.NewUlid().ToString(), Faker.Lorem.Word(), Ulid.NewUlid().ToString(), Faker.Lorem.Word()));

        var (status, body) = await db.PostAsync(db.Writer, Stmt("INSERT INTO notes VALUES (?, ?)", Ulid.NewUlid().ToString(), Faker.Lorem.Word()));
        await Assert.That((status, body.GetProperty("error").GetProperty("code").GetString())).IsEqualTo((HttpStatusCode.TooManyRequests, "quota_exceeded"));
        await Assert.That(body.GetProperty("error").GetProperty("message").GetString()).Contains("write units");
        await using (var connection = await db.Postgres.OpenConnectionAsync())
        {
            await Assert.That(await StateAsync(connection, "INSERT INTO notes VALUES ($1, 'x')", Ulid.NewUlid().ToString())).IsEqualTo("53400");
        }

        await Assert.That(await db.CountAsync("notes")).IsEqualTo(2);
        var (readStatus, readBody) = await db.PostAsync(db.Reader, Stmt("SELECT id FROM notes"));
        await Assert.That((readStatus, readBody.GetProperty("error").GetProperty("code").GetString())).IsEqualTo((HttpStatusCode.TooManyRequests, "quota_exceeded"));
        await Assert.That(readBody.GetProperty("error").GetProperty("message").GetString()).Contains("fair use");
    }

    private static async Task<(JsonElement Page, JsonElement Usage)> AllowanceAsync(SqlNativeDatabase db)
    {
        string? after = null;
        do
        {
            using var response = await db.Api.Server.Client.GetAsync(ManagementApi.Uri("/v1/usage/allowances" + (after is null ? "" : "?after=" + after)));
            var page = await response.Content.ReadFromJsonAsync<JsonElement>();
            foreach (var item in page.GetProperty("items").EnumerateArray())
            {
                if (item.GetProperty("organizationId").GetString() == db.Database.Organization)
                {
                    return (page, item);
                }
            }

            after = page.GetProperty("next").GetString();
        }
        while (after is not null);

        throw new InvalidOperationException("the organization is missing from the allowance feed");
    }
}
