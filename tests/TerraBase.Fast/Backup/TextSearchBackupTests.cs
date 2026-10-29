using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Backup;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class TextSearchBackupTests
{
    private static readonly string[] BodyFields = ["body"];
    private static readonly string[] TitleFields = ["title"];
    private static readonly string[] NativeSearch = ["/body", "native"];
    private static readonly string[] HeadingSearch = ["/title", "heading"];
    private static readonly string[] RemovedSearch = ["/body", "original OR doomed"];
    private static readonly string[] DatabaseNames = ["main", "main-copy", "main-restored"];

    [Test]
    public async Task Copies_and_continuous_restore_rebuild_text_indexes_across_creation_updates_and_deletes()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var s3 = new FakeS3();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(s3.Configure, services =>
        {
            s3.Route(services);
            ManualBackupHosting.Configure(services);
        }, time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        var database = await api.CreateManagedDatabaseAsync("main");
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        var (updatedId, deletedId, keptId) = (Ulid.NewUlid().ToString(), Ulid.NewUlid().ToString(), Ulid.NewUlid().ToString());
        foreach (var (id, body) in new[] { (updatedId, "original"), (deletedId, "doomed"), (keptId, "native") })
        {
            using var put = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", id)), new { body });
            put.EnsureSuccessStatusCode();
        }

        using var bodyIndex = await api.Client.PostAsJsonAsync(ManagementApi.Uri($"{database.Path}/indexes"),
            new { name = "body_text", collection = "people", kind = "text", fields = BodyFields });
        bodyIndex.EnsureSuccessStatusCode();
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(token);

        // The snapshot copies an existing native index; the log creates another and changes both tables' contents.
        time.Advance(TimeSpan.FromSeconds(1));
        using var titleIndex = await api.Client.PostAsJsonAsync(ManagementApi.Uri($"{database.Path}/indexes"),
            new { name = "title_text", collection = "people", kind = "text", fields = TitleFields });
        titleIndex.EnsureSuccessStatusCode();
        using var updated = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", updatedId)),
            new { body = "native unrelated unrelated unrelated", title = "heading" });
        updated.EnsureSuccessStatusCode();
        using var deleted = await data.DeleteAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", deletedId)));
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var deletion = await deleted.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(deletion.GetProperty("id").GetString()).IsEqualTo(deletedId);
        var tombstoneVersion = deletion.GetProperty("version").GetString()!;
        await Assert.That(tombstoneVersion).Matches("^tbv1-[0-9a-f]{32}$");
        await Assert.That(deleted.Headers.ETag?.Tag).IsEqualTo($"\"{tombstoneVersion}\"");
        using var gone = await data.GetAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", deletedId)));
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(gone.Headers.ETag?.Tag).IsEqualTo(deleted.Headers.ETag?.Tag);
        var missing = await gone.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(missing.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("not_found");
        time.Advance(TimeSpan.FromSeconds(1));
        await backup.RunOnceAsync(token);
        var shipped = backup.Status.Files.Single(file => file.File == $"v1/{database.Organization}/{database.Project}/{database.Id}/nodes/local").LastShippedAt!.Value;

        using var copied = await api.Client.PostAsJsonAsync(ManagementApi.Uri($"{database.Path}/branches"), new { name = "main-copy" });
        await Assert.That(copied.StatusCode).IsEqualTo(HttpStatusCode.Created);
        using var restored = await api.Client.PostAsJsonAsync(ManagementApi.Uri($"{database.Path}/point-in-time/restore"),
            new { pointInTime = shipped, name = "main-restored" });
        var restoreBody = await restored.Content.ReadFromJsonAsync<JsonElement>(token);
        await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.Created)
            .Because(restoreBody.TryGetProperty("error", out var restoreError) ? restoreError.GetRawText() : "restore must create a database");
        foreach (var name in DatabaseNames)
        {
            var query = ManagementApi.Uri($"/v1/projects/{database.Project}/databases/{name}/collections/people/query");
            using var ranked = await data.PostAsJsonAsync(query,
                new { filter = new { search = NativeSearch }, sort = new[] { new { path = "$score", order = "desc" } }, count = true });
            await Assert.That(ranked.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var page = await ranked.Content.ReadFromJsonAsync<JsonElement>();
            var items = page.GetProperty("items").EnumerateArray().ToArray();
            await Assert.That(items.Select(item => item.GetProperty("id").GetString()!).SequenceEqual([keptId, updatedId], StringComparer.Ordinal)).IsTrue();
            await Assert.That(items[0].GetProperty("score").GetDouble()).IsGreaterThan(items[1].GetProperty("score").GetDouble());
            await Assert.That(page.GetProperty("count").GetProperty("value").GetString()).IsEqualTo("2");
            using var heading = await data.PostAsJsonAsync(query, new { filter = new { search = HeadingSearch } });
            await Assert.That(heading.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var title = await heading.Content.ReadFromJsonAsync<JsonElement>();
            await Assert.That(title.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!)).IsEquivalentTo([updatedId]);
            using var removed = await data.PostAsJsonAsync(query, new { filter = new { search = RemovedSearch } });
            await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var absent = await removed.Content.ReadFromJsonAsync<JsonElement>();
            await Assert.That(absent.GetProperty("items").GetArrayLength()).IsEqualTo(0);
        }
    }
}
