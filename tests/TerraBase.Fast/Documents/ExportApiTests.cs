using System.Net;
using System.Text.Json;
using Bogus;
using TerraBase.Testing;

namespace TerraBase.Fast.Documents;

public sealed class ExportApiTests
{
    [Test]
    public async Task Export_streams_every_document_resumes_by_cursor_and_round_trips_into_another_database()
    {
        await using var api = await DocumentApi.StartAsync();
        var faker = new Faker();
        (await api.SendAsync(HttpMethod.Post, api.Collections, new { name = "notes" })).Status.ShouldBe(HttpStatusCode.Created);
        foreach (var collection in new[] { DocumentApi.Collection, "notes" })
        {
            for (var i = 0; i < 30; i++)
            {
                var path = $"{api.Collections}/{collection}/documents/by-key?id={DocumentApi.NewId()}";
                (await api.SendAsync(HttpMethod.Put, path, new { name = faker.Name.FullName(), n = i, tags = faker.Lorem.Words(2) })).Status.ShouldBe(HttpStatusCode.Created);
            }
        }

        var deleted = DocumentApi.NewId();
        (await api.PutAsync(deleted, new { gone = true })).Status.ShouldBe(HttpStatusCode.Created);
        (await api.SendAsync(HttpMethod.Delete, api.Document(deleted))).Status.ShouldBe(HttpStatusCode.OK);

        var lines = await ExportAsync(api.Client, api.DatabasePath());
        await Assert.That(lines.Count).IsEqualTo(60);
        await Assert.That(lines.Any(line => line.Id == deleted)).IsFalse();
        var ordered = lines.OrderBy(line => line.Collection, StringComparer.Ordinal).ThenBy(line => line.Id, StringComparer.Ordinal);
        await Assert.That(lines.SequenceEqual(ordered)).IsTrue();

        // Resuming after any line yields exactly the lines that followed it, across the collection boundary.
        var middle = lines[17];
        var resumed = await ExportAsync(api.Client, $"{api.DatabasePath()}?after_collection={middle.Collection}&after_id={middle.Id}");
        await Assert.That(resumed.SequenceEqual(lines.Skip(18))).IsTrue();

        // Writing every line into a new database reproduces the same export.
        (await api.SendAsync(HttpMethod.Put, api.DatabasePath("copy"))).Status.ShouldBe(HttpStatusCode.Created);
        foreach (var collection in lines.Select(line => line.Collection).Distinct())
        {
            (await api.SendAsync(HttpMethod.Post, $"{api.DatabasePath("copy")}/collections", new { name = collection })).Status.ShouldBe(HttpStatusCode.Created);
        }

        foreach (var line in lines)
        {
            var path = $"{api.DatabasePath("copy")}/collections/{line.Collection}/documents/by-key?id={line.Id}";
            (await api.SendRawAsync(HttpMethod.Put, path, line.Document, "application/json")).Status.ShouldBe(HttpStatusCode.Created);
        }

        var copied = await ExportAsync(api.Client, api.DatabasePath("copy"));
        await Assert.That(copied.SequenceEqual(lines)).IsTrue();
    }

    [Test]
    public async Task Export_requires_documents_read_and_a_paired_cursor()
    {
        await using var api = await DocumentApi.StartAsync();
        using var writer = await api.ClientWithScopesAsync("documents:write");
        var denied = await DocumentApi.SendAsync(writer, HttpMethod.Get, api.DatabasePath() + "/export");
        var unpaired = await api.SendAsync(HttpMethod.Get, api.DatabasePath() + "/export?after_collection=items");
        var unknown = await api.SendAsync(HttpMethod.Get, api.DatabasePath("missing") + "/export");
        await Assert.That((denied.Status, denied.Code)).IsEqualTo((HttpStatusCode.Forbidden, "forbidden_scope"));
        await Assert.That((unpaired.Status, unpaired.Message)).IsEqualTo((HttpStatusCode.BadRequest, "after_collection and after_id go together"));
        await Assert.That((unknown.Status, unknown.Code)).IsEqualTo((HttpStatusCode.NotFound, "unknown_database"));
    }

    private static async Task<List<ExportLine>> ExportAsync(HttpClient client, string database)
    {
        var uri = database.Contains('?', StringComparison.Ordinal) ? database.Replace("?", "/export?", StringComparison.Ordinal) : database + "/export";
        using var response = await client.GetAsync(new Uri(uri, UriKind.Relative));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/x-ndjson");
        var text = await response.Content.ReadAsStringAsync();
        return [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            return new ExportLine(root.GetProperty("collection").GetString()!, root.GetProperty("id").GetString()!, root.GetProperty("document").GetRawText());
        })];
    }

    private sealed record ExportLine(string Collection, string Id, string Document);
}
