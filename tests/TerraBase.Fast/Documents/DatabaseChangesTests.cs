using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Documents;
using TerraBase.Testing;

namespace TerraBase.Fast.Documents;

public sealed class DatabaseChangesTests
{
    private static readonly Faker Fake = new();

    [Test]
    public async Task A_write_after_cursor_now_streams_with_its_document_and_its_id_resumes_after_it()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var before = DocumentApi.NewId();
        (await api.PutAsync(before, new { name = Fake.Name.FirstName() })).Status.ShouldBe(HttpStatusCode.Created);
        var name = Fake.Name.FirstName();
        var id = DocumentApi.NewId();
        string resume;
        using (var stream = await Sse.OpenAsync(api.Client, $"{api.DatabasePath()}/changes?cursor=now"))
        {
            var head = await stream.NextAsync();
            await Assert.That(head.Event).IsEqualTo("cursor");
            await Assert.That(head.Data.GetProperty("cursor").GetString()).IsEqualTo(head.Id);
            (await api.PutAsync(id, new { name })).Status.ShouldBe(HttpStatusCode.Created);
            var change = await stream.NextAsync();
            await Assert.That(change.Event).IsEqualTo("change");
            await Assert.That(change.Data.GetProperty("collection").GetString()).IsEqualTo(DocumentApi.Collection);
            await Assert.That(change.Data.GetProperty("id").GetString()).IsEqualTo(id);
            await Assert.That(change.Data.GetProperty("op").GetString()).IsEqualTo("upsert");
            await Assert.That(change.Data.GetProperty("document").GetProperty("name").GetString()).IsEqualTo(name);
            await Assert.That(change.Data.GetProperty("origin").GetString()).Matches("^[0-9a-f]{16}$");
            await Assert.That(ulong.Parse(change.Data.GetProperty("version").GetString()!, System.Globalization.CultureInfo.InvariantCulture)).IsGreaterThan(0UL);
            await Assert.That(change.Id).IsNotEqualTo(head.Id);
            resume = change.Id;
        }

        (await api.SendAsync(HttpMethod.Delete, api.Document(id))).Status.ShouldBe(HttpStatusCode.OK);
        using var resumed = await Sse.OpenAsync(api.Client, $"{api.DatabasePath()}/changes?cursor=now", resume);
        await Assert.That((await resumed.NextAsync()).Event).IsEqualTo("cursor");
        var deleted = await resumed.NextAsync();
        await Assert.That(deleted.Data.GetProperty("id").GetString()).IsEqualTo(id);
        await Assert.That(deleted.Data.GetProperty("op").GetString()).IsEqualTo("delete");
        await Assert.That(deleted.Data.GetProperty("document").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task Without_a_cursor_the_feed_replays_retention_in_order_and_the_filter_keeps_only_its_collections()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        (await api.SendAsync(HttpMethod.Post, api.Collections, new { name = "other" })).Status.ShouldBe(HttpStatusCode.Created);
        var ids = Enumerable.Range(0, 5).Select(_ => DocumentApi.NewId()).ToArray();
        foreach (var id in ids)
        {
            (await api.PutAsync(id, new { name = Fake.Name.FirstName() })).Status.ShouldBe(HttpStatusCode.Created);
            (await api.SendAsync(HttpMethod.Put, $"{api.Collections}/other/documents/by-key?id={id}", new { other = true })).Status.ShouldBe(HttpStatusCode.Created);
        }

        using var all = await Sse.OpenAsync(api.Client, $"{api.DatabasePath()}/changes");
        _ = await all.NextAsync();
        var every = await all.ChangesAsync(10);
        await Assert.That(string.Join(' ', every.Select(change => $"{change.Data.GetProperty("collection").GetString()}/{change.Data.GetProperty("id").GetString()}")))
            .IsEqualTo(string.Join(' ', ids.SelectMany(id => new[] { $"{DocumentApi.Collection}/{id}", $"other/{id}" })));

        using var filtered = await Sse.OpenAsync(api.Client, $"{api.DatabasePath()}/changes?collections={DocumentApi.Collection}");
        _ = await filtered.NextAsync();
        var items = await filtered.ChangesAsync(5);
        await Assert.That(string.Join(' ', items.Select(change => change.Data.GetProperty("id").GetString()))).IsEqualTo(string.Join(' ', ids));
        var last = DocumentApi.NewId();
        (await api.PutAsync(last, new { name = "last" })).Status.ShouldBe(HttpStatusCode.Created);
        var next = await filtered.NextAsync();
        await Assert.That(next.Data.GetProperty("id").GetString()).IsEqualTo(last);
        using var resumed = await Sse.OpenAsync(api.Client, $"{api.DatabasePath()}/changes?collections={DocumentApi.Collection}&cursor={Uri.EscapeDataString(items[^1].Id)}");
        _ = await resumed.NextAsync();
        await Assert.That((await resumed.NextAsync()).Data.GetProperty("id").GetString()).IsEqualTo(last);
    }

    private sealed record SseEvent(string Event, string Id, JsonElement Data);
    private sealed class Sse : IDisposable
    {
        private readonly HttpResponseMessage _response;
        private readonly StreamReader _reader;

        private Sse(HttpResponseMessage response, StreamReader reader)
        {
            _response = response;
            _reader = reader;
        }

        public static async Task<Sse> OpenAsync(HttpClient client, string uri, string? lastEventId = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, UriKind.Relative));
            request.Headers.Accept.ParseAdd("text/event-stream");
            if (lastEventId is not null)
            {
                request.Headers.Add("Last-Event-ID", lastEventId);
            }

            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            {
                var text = await response.Content.ReadAsStringAsync();
                response.Dispose();
                throw new InvalidOperationException($"the stream answered {response.StatusCode}: {text}");
            }

            return new Sse(response, new StreamReader(await response.Content.ReadAsStreamAsync()));
        }

        public async Task<SseEvent> NextAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            string? name = null, id = null, data = null;
            while (await _reader.ReadLineAsync(timeout.Token) is { } line)
            {
                if (line.Length == 0)
                {
                    using var json = JsonDocument.Parse(data ?? throw new InvalidDataException("an event without data"));
                    return new SseEvent(name ?? "message", id ?? "", json.RootElement.Clone());
                }

                var colon = line.IndexOf(':', StringComparison.Ordinal);
                var (field, value) = (line[..colon], line[(colon + 1)..].TrimStart(' '));
                (name, id, data) = field switch
                {
                    "event" => (value, id, data),
                    "id" => (name, value, data),
                    "data" => (name, id, data + value),
                    _ => (name, id, data),
                };
            }

            throw new EndOfStreamException("the stream ended");
        }
        public async Task<List<SseEvent>> ChangesAsync(int count)
        {
            var changes = new List<SseEvent>();
            while (changes.Count < count)
            {
                if (await NextAsync() is { Event: "change" } change)
                {
                    changes.Add(change);
                }
            }

            return changes;
        }

        public void Dispose()
        {
            _reader.Dispose();
            _response.Dispose();
        }
    }
}
