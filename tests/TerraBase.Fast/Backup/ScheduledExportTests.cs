using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Management.Backups;
using TerraBase.Testing;
using static TerraBase.Testing.ManagementApi;

namespace TerraBase.Fast.Backup;

public sealed class ScheduledExportTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task A_due_schedule_writes_every_document_as_importable_ndjson_to_the_customer_bucket()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 2, 0, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(s3.Configure, s3.Route, time);
        var database = await api.CreateManagedDatabaseAsync();
        var route = Uri(database.Path + "/export-schedule");
        await ErrorAssert.ErrorAsync(api.Client.PutAsJsonAsync(route, new { cron = "0 3 * * *" }, token), HttpStatusCode.Conflict,
            "conflict", "configure the project's backup destination first");
        (await api.Client.PutAsJsonAsync(Uri(database.ProjectPath + "/backup-destination"), Destination(), token)).EnsureSuccessStatusCode();
        await ErrorAssert.ErrorAsync(api.Client.PutAsJsonAsync(route, new { cron = "daily" }, token), HttpStatusCode.BadRequest,
            "invalid_request", "cron must be a standard 5-field cron expression");

        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        var names = Enumerable.Range(0, 7).Select(index => $"person-{index}").ToArray();
        foreach (var collection in new[] { "people", "teams" })
        {
            await CreateCollectionAsync(data, database, collection);
            foreach (var name in names)
            {
                (await data.PutAsJsonAsync(Uri(DocumentPath(database, collection, name)), new { name = Faker.Name.FullName() }, token)).EnsureSuccessStatusCode();
            }
        }

        var saved = await JsonAsync(await api.Client.PutAsJsonAsync(route, new { cron = "0 3 * * *" }, token));
        await Assert.That(saved.GetProperty("cron").GetString()).IsEqualTo("0 3 * * *");
        await Assert.That(saved.GetProperty("lastRun").ValueKind).IsEqualTo(JsonValueKind.Null);
        var exports = api.Server.Services.GetRequiredService<IScheduledExports>();
        await exports.RunOnceAsync(token);
        await Assert.That(s3.CustomerObjects.Keys.Any(IsExport)).IsFalse();

        time.Advance(TimeSpan.FromHours(1));
        await exports.RunOnceAsync(token);
        var run = (await JsonAsync(await api.Client.GetAsync(route, token))).GetProperty("lastRun");
        await Assert.That(run.GetProperty("status").GetString()).IsEqualTo("succeeded");
        await Assert.That(run.GetProperty("documents").GetInt64()).IsEqualTo(14);
        var key = run.GetProperty("key").GetString()!;
        await Assert.That(key).IsEqualTo($"terrabase-exports/{database.Project}/{database.Id}/20261024T030000Z.ndjson");
        var lines = Encoding.UTF8.GetString(s3.CustomerObjects[key]).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonElement.Parse(line)).ToArray();
        await Assert.That(lines.Length).IsEqualTo(14);
        await Assert.That(run.GetProperty("bytes").GetInt64()).IsEqualTo(s3.CustomerObjects[key].Length);
        await Assert.That(string.Join(',', lines.Select(line => line.GetProperty("collection").GetString()).Distinct().Order())).IsEqualTo("people,teams");
        await Assert.That(lines.All(line => line.GetProperty("document").GetProperty("name").GetString() is { Length: > 0 })).IsTrue();

        // The next run waits for the next occurrence; a failed upload is recorded without bucket details.
        await exports.RunOnceAsync(token);
        await Assert.That(s3.CustomerObjects.Keys.Count(IsExport)).IsEqualTo(1);
        s3.RejectCustomerWrites = IsExport;
        time.Advance(TimeSpan.FromDays(1));
        await exports.RunOnceAsync(token);
        var failed = (await JsonAsync(await api.Client.GetAsync(route, token))).GetProperty("lastRun");
        await Assert.That(failed.GetProperty("status").GetString()).IsEqualTo("failed");
        await Assert.That(failed.GetProperty("error").GetString()).IsEqualTo("the export could not be written to the customer bucket");
        await Assert.That(failed.GetProperty("key").ValueKind).IsEqualTo(JsonValueKind.Null);

        var stopped = await JsonAsync(await api.Client.PutAsJsonAsync(route, new { cron = (string?)null }, token));
        await Assert.That(stopped.GetProperty("cron").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(stopped.GetProperty("lastRun").GetProperty("status").GetString()).IsEqualTo("failed");
    }

    private static bool IsExport(string key) => key.Contains("terrabase-exports/", StringComparison.Ordinal);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using (response)
        {
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
    }

    private static object Destination() => new
    {
        endpoint = FakeS3.CustomerEndpoint,
        region = "test-region-1",
        bucket = FakeS3.CustomerBucket,
        accessKeyId = FakeS3.CustomerAccessKey,
        secretKey = FakeS3.CustomerSecretKey,
        mode = "both",
    };
}
