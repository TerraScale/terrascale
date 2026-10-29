using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Fast.Documents;
using TerraBase.Features.Management.Usage;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

/// <summary>
/// Metered client bytes against the body bytes a real HTTP client sent and read: JSON responses written through the response
/// writer, a change feed written through the response stream while it stays open, and that feed after the client aborts it.
/// </summary>
public sealed class UsageBodyBytesTests
{
    private const string Database = "metered";
    private static readonly Faker Fake = new();

    [Test]
    public async Task Metered_bytes_equal_the_bodies_of_completed_json_requests()
    {
        var meter = new BodyMeter();
        await using var api = await DocumentApi.StartAsync(services: services =>
            services.AddSingleton<IUsageMeter>(provider => meter.Over(provider.GetRequiredService<UsageMeter>())));
        var database = await CreateAsync(api);
        var client = await Client.StartAsync(api);
        var documents = $"{api.DatabasePath(Database)}/collections/{DocumentApi.Collection}/documents/by-key?id=";
        var id = DocumentApi.NewId();

        // Large enough that the JSON serializer flushes the writer more than once.
        await client.SendAsync(HttpMethod.Put, documents + id, new { text = Fake.Random.String2(200_000), name = Fake.Name.FullName() }, HttpStatusCode.Created);
        var read = await client.SendAsync(HttpMethod.Get, documents + id, null, HttpStatusCode.OK);
        await Assert.That(read.Length).IsGreaterThan(200_000);
        await client.SendAsync(HttpMethod.Get, documents + DocumentApi.NewId(), null, HttpStatusCode.NotFound);
        await client.SendAsync(HttpMethod.Post, $"{api.DatabasePath(Database)}/collections/{DocumentApi.Collection}/query", new { limit = 10 }, HttpStatusCode.OK);

        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(meter.Of(database) == client.Totals), TimeSpan.FromSeconds(30));
        await Assert.That(meter.Of(database)).IsEqualTo(client.Totals);
    }

    [Test]
    public async Task An_open_change_feed_meters_what_it_flushed_and_nothing_more_once_the_client_aborts_it()
    {
        var meter = new BodyMeter();
        // A fixed clock: no heartbeat is written and no minute passes while the feed is open.
        await using var api = await DocumentApi.StartAsync(time: new FakeTimeProvider(DateTimeOffset.UtcNow), services: services =>
            services.AddSingleton<IUsageMeter>(provider => meter.Over(provider.GetRequiredService<UsageMeter>())));
        var database = await CreateAsync(api);
        var client = await Client.StartAsync(api);
        var usage = api.Server.Services.GetRequiredService<UsageMeter>();

        using (var feed = new HttpRequestMessage(HttpMethod.Get, new Uri($"{api.DatabasePath(Database)}/changes?cursor=now", UriKind.Relative)))
        using (var response = await api.Client.SendAsync(feed, HttpCompletionOption.ResponseHeadersRead))
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var events = await response.Content.ReadAsStreamAsync();
            var streamed = await NextEventAsync(events);
            await client.SendAsync(HttpMethod.Put,
                $"{api.DatabasePath(Database)}/collections/{DocumentApi.Collection}/documents/by-key?id={DocumentApi.NewId()}",
                new { name = Fake.Name.FullName() }, HttpStatusCode.Created);
            streamed += await NextEventAsync(events);

            // Still open: a flush tick reports the feed's bytes so far, exactly those the client read.
            var open = client.Totals with { Sent = client.Totals.Sent + streamed };
            await TerraBaseTestServer.EventuallyAsync(_ =>
            {
                usage.ReportActive();
                return Task.FromResult(meter.Of(database) == open);
            }, TimeSpan.FromSeconds(30));
            client.Read(streamed);
        }

        // The aborted feed completes as one more request with no further bytes.
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(meter.Of(database) == client.Totals), TimeSpan.FromSeconds(30));
        await Assert.That(meter.Of(database)).IsEqualTo(client.Totals);
    }

    // A fresh database whose create request is not one of its own: every metered byte of it is one the test sent or read.
    private static async Task<TenantContext> CreateAsync(DocumentApi api)
    {
        (await api.SendAsync(HttpMethod.Put, api.DatabasePath(Database))).Status.ShouldBe(HttpStatusCode.Created);
        return await api.TenantOfAsync(Database);
    }

    // Reads one whole server-sent event, returning the bytes read.
    private static async Task<long> NextEventAsync(Stream events)
    {
        var buffer = new byte[4096];
        var text = new StringBuilder();
        long read = 0;
        while (!text.ToString().EndsWith("\n\n", StringComparison.Ordinal))
        {
            var count = await events.ReadAsync(buffer);
            if (count == 0)
            {
                throw new IOException("the feed ended");
            }

            read += count;
            text.Append(Encoding.UTF8.GetString(buffer, 0, count));
        }

        return read;
    }

    private sealed record Totals(long Received, long Sent, int Requests);

    // Body bytes this test sent and read, and its requests, of the metered database.
    private sealed class Client(HttpClient http)
    {
        // Its collection is created by the first metered request.
        public static async Task<Client> StartAsync(DocumentApi api)
        {
            var client = new Client(api.Client);
            await client.SendAsync(HttpMethod.Post, $"{api.DatabasePath(Database)}/collections", new { name = DocumentApi.Collection }, HttpStatusCode.Created);
            return client;
        }

        public Totals Totals { get; private set; } = new(0, 0, 0);

        public async Task<byte[]> SendAsync(HttpMethod method, string uri, object? body, HttpStatusCode expected)
        {
            var bytes = body is null ? null : JsonSerializer.SerializeToUtf8Bytes(body);
            using var request = new HttpRequestMessage(method, new Uri(uri, UriKind.Relative));
            if (bytes is not null)
            {
                request.Content = new ByteArrayContent(bytes);
                request.Content.Headers.ContentType = new("application/json");
            }

            using var response = await http.SendAsync(request);
            var read = await response.Content.ReadAsByteArrayAsync();
            await Assert.That(response.StatusCode).IsEqualTo(expected);
            Totals = new(Totals.Received + (bytes?.Length ?? 0), Totals.Sent + read.Length, Totals.Requests + 1);
            return read;
        }

        // A streamed request the client read and ended.
        public void Read(long bytes) => Totals = new(Totals.Received, Totals.Sent + bytes, Totals.Requests + 1);
    }

    // The production meter, with each database's client bytes and completed requests also summed here.
    private sealed class BodyMeter
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, Totals> _databases = [];

        public Totals Of(TenantContext database)
        {
            lock (_gate)
            {
                return _databases.GetValueOrDefault(database.DatabaseId!) ?? new(0, 0, 0);
            }
        }

        public IUsageMeter Over(IUsageMeter inner) => new Recording(inner, this);

        private void Add(TenantContext database, long received, long sent, int requests)
        {
            lock (_gate)
            {
                var totals = _databases.GetValueOrDefault(database.DatabaseId!) ?? new(0, 0, 0);
                _databases[database.DatabaseId!] = new(totals.Received + received, totals.Sent + sent, totals.Requests + requests);
            }
        }

        private sealed class Recording(IUsageMeter inner, BodyMeter totals) : IUsageMeter
        {
            public void RecordRead(TenantContext database, long units) => inner.RecordRead(database, units);

            public void RecordWrites(TenantContext database, int writes, long units) => inner.RecordWrites(database, writes, units);

            public void RecordOperation(TenantContext database, string kind, int count) => inner.RecordOperation(database, kind, count);

            public void RecordRequest(TenantContext database, string method, int status, TimeSpan elapsed, long received, long sent)
            {
                inner.RecordRequest(database, method, status, elapsed, received, sent);
                totals.Add(database, received, sent, 1);
            }

            public void RecordStatement(TenantContext database, string protocol, bool failed, TimeSpan elapsed) =>
                inner.RecordStatement(database, protocol, failed, elapsed);

            public void RecordClientBytes(TenantContext database, long received, long sent, DateTimeOffset? transferredAt = null)
            {
                inner.RecordClientBytes(database, received, sent, transferredAt);
                totals.Add(database, received, sent, 0);
            }

            public IDisposable TrackActive(Action report) => inner.TrackActive(report);

            public void RecordReplicationBytes(TenantContext database, string peerRegion, string kind, bool sent, long bytes) =>
                inner.RecordReplicationBytes(database, peerRegion, kind, sent, bytes);

            public void RecordReplicatedOperations(TenantContext database, string peerRegion, int operations) =>
                inner.RecordReplicatedOperations(database, peerRegion, operations);

            public void RecordReplicationState(TenantContext database, string peerRegion, long remaining, bool diverged, double? convergenceMs = null) =>
                inner.RecordReplicationState(database, peerRegion, remaining, diverged, convergenceMs);
        }
    }
}
