using System.Net;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Testing;
using Turso.Data.Sqlite;

namespace TerraBase.Fast.Documents;

public sealed class LocalHistoryRetentionTests
{
    [Test]
    public async Task Local_retention_collects_old_tombstones_and_feed_events_without_removing_recent_deletions_or_live_data(CancellationToken cancellationToken)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await DocumentApi.StartAsync(services: services =>
        {
            var timer = services.Single(descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationFactory?.Method.ReturnType == typeof(FeedRetention));
            services.Remove(timer);
        }, time: clock, cancellationToken: cancellationToken);
        var old = DocumentApi.NewId();
        var recent = DocumentApi.NewId();
        var live = DocumentApi.NewId();
        var value = new Faker().Lorem.Sentence();
        await Assert.That((await api.PutAsync(live, new { value }, cancellationToken: cancellationToken)).Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That((await api.PutAsync(old, new { value }, cancellationToken: cancellationToken)).Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That((await api.SendAsync(HttpMethod.Delete, api.Document(old), cancellationToken: cancellationToken)).Status).IsEqualTo(HttpStatusCode.OK);
        var cursor = await CursorAsync(api, cancellationToken);
        var path = await api.DatabaseFileAsync();
        var before = await StatesAsync(path, cancellationToken);
        await Assert.That(before[old]).IsEqualTo(0L);
        await Assert.That(before[live]).IsEqualTo(1L);
        clock.Advance(FeedCursor.Retention + TimeSpan.FromHours(1));
        await Assert.That((await api.PutAsync(recent, new { value }, cancellationToken: cancellationToken)).Status).IsEqualTo(HttpStatusCode.Created);
        await Assert.That((await api.SendAsync(HttpMethod.Delete, api.Document(recent), cancellationToken: cancellationToken)).Status).IsEqualTo(HttpStatusCode.OK);
        await api.Server.Services.GetRequiredService<FeedRetention>().TrimAsync(cancellationToken);
        var after = await StatesAsync(path, cancellationToken);
        await Assert.That(after.ContainsKey(old)).IsFalse();
        await Assert.That(after[recent]).IsEqualTo(0L);
        await Assert.That(after[live]).IsEqualTo(1L);
        var queried = await api.QueryAsync(new { }, cancellationToken);
        await Assert.That(queried.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(queried.Ids).IsEquivalentTo([live]);
        var document = await api.GetAsync(live, cancellationToken: cancellationToken);
        await Assert.That(document.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(document.Json.GetProperty("document").GetProperty("value").GetString()).IsEqualTo(value);
        foreach (var id in new[] { old, recent })
        {
            await Assert.That((await api.GetAsync(id, cancellationToken: cancellationToken)).Status).IsEqualTo(HttpStatusCode.NotFound);
        }

        using var expired = await api.Client.GetAsync(ManagementApi.Uri($"{api.DatabasePath()}/changes?cursor={Uri.EscapeDataString(cursor)}"), cancellationToken);
        await Assert.That(expired.StatusCode).IsEqualTo(HttpStatusCode.Gone);
        await using var connection = new SqliteConnection(DocumentApi.ConnectionString(path, readOnly: true));
        await connection.OpenAsync(cancellationToken);
        await using var retained = connection.CreateCommand();
        retained.CommandText = "SELECT id FROM _tb_feed ORDER BY seq;";
        await using var rows = await retained.ExecuteReaderAsync(cancellationToken);
        var ids = new List<string>();
        while (await rows.ReadAsync(cancellationToken))
        {
            ids.Add(rows.GetString(0));
        }

        await Assert.That(ids).IsEquivalentTo([recent, recent]);
    }

    private static async Task<Dictionary<string, long>> StatesAsync(string path, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(DocumentApi.ConnectionString(path, readOnly: true));
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, live FROM _tb_state WHERE collection = $collection;";
        command.Parameters.AddWithValue("$collection", DocumentApi.Collection);
        await using var rows = await command.ExecuteReaderAsync(cancellationToken);
        var states = new Dictionary<string, long>(StringComparer.Ordinal);
        while (await rows.ReadAsync(cancellationToken))
        {
            states.Add(rows.GetString(0), rows.GetInt64(1));
        }

        return states;
    }

    private static async Task<string> CursorAsync(DocumentApi api, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ManagementApi.Uri(api.DatabasePath() + "/changes?cursor=now"));
        using var response = await api.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.StartsWith("id: ", StringComparison.Ordinal))
            {
                return line[4..];
            }
        }

        throw new InvalidDataException("the change feed did not return its current cursor");
    }
}
