using System.Net;
using System.Text;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Features.Backup;
using TerraBase.Testing;

namespace TerraBase.E2E.PitrRepro;

/// <summary>
/// Suspected defect 2: a segment PUT the bucket stores but answers with a 500 (a lost response). The platform shipper keeps
/// its pending log after a failure and retries it with the frames read since, under a key with the same start and a later
/// time, so the bucket holds two overlapping segments of one generation.
/// </summary>
[Category("pitr-repro")]
public sealed class AmbiguousPutRepro
{
    private static readonly Faker Faker = new();

    [Test]
    [Scenario(Nodes.InProcess)]
    public async Task A_stored_then_failed_segment_put_leaves_an_overlapping_segment_and_later_restores_fail(CancellationToken cancellationToken)
    {
        ReproHost.SkipUnlessRequested();
        var ambiguous = new AmbiguousPuts();
        await using var host = await ReproHost.StartAsync(services =>
            services.AddHttpClient(S3ObjectStore.HttpClientName).AddHttpMessageHandler(() => new AmbiguousPutHandler(ambiguous)));
        var database = await host.Api.CreateManagedDatabaseAsync("main");
        using var data = await host.Api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people", cancellationToken);
        await ReproHost.PutAsync(data, database, "people", "seed", Faker.Lorem.Sentence(8), cancellationToken);
        await host.ShipAsync(TimeSpan.FromSeconds(10), cancellationToken);
        var generation = host.Status(database).Generation!;

        // B's segment is stored, and every attempt at it (the SDK retries 500s) is answered 500.
        ambiguous.Prefix = $"{ReproHost.File(database)}/wal/";
        await ReproHost.PutAsync(data, database, "people", "b", Faker.Lorem.Sentence(8), cancellationToken);
        await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);
        ambiguous.Prefix = null;
        ReproHost.Log($"after the ambiguous PUT of {ambiguous.Key}: attempts={ambiguous.Attempts} stored={host.S3.Objects.ContainsKey(ambiguous.Key!)}; {host.StatusLine(database)}; {await host.CountersAsync(cancellationToken)}");
        await Assert.That(ambiguous.Attempts).IsGreaterThan(0);
        await Assert.That(host.S3.Objects.ContainsKey(ambiguous.Key!)).IsTrue();
        await Assert.That(host.Status(database).LastError).IsNotNull();

        // C commits before the retry; then D ships normally.
        await ReproHost.PutAsync(data, database, "people", "c", Faker.Lorem.Sentence(8), cancellationToken);
        await host.ShipAsync(TimeSpan.FromSeconds(2), cancellationToken);
        await ReproHost.PutAsync(data, database, "people", "d", Faker.Lorem.Sentence(8), cancellationToken);
        await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);
        var segments = host.Segments(database, generation);
        ReproHost.Log($"segments: {string.Join(", ", segments.Select(segment => $"start={segment.Start} at={segment.At:O}"))}; {host.StatusLine(database)}");
        await Assert.That(segments.Count).IsEqualTo(3);
        await Assert.That(segments[0].Start).IsEqualTo(segments[1].Start);
        ReproHost.Log($"window {await host.WindowAsync(database, cancellationToken)}");

        // Before the retry's time only the first copy applies: B restores.
        var (first, firstBody) = await host.RestoreAsync(database, segments[0].At, "ambiguous-first", cancellationToken);
        var firstB = await ReproHost.ReadAsync(data, database, "ambiguous-first", "people", "b", cancellationToken);
        ReproHost.Log($"restore at s1 {segments[0].At:O} -> {(int)first} {firstBody}; b={(int)firstB}");
        await Assert.That(first).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(firstB).IsEqualTo(HttpStatusCode.OK);

        // From the retry's time on, every restore of this generation fails, latest included.
        foreach (var (at, label) in new[] { (segments[1].At, "retry"), (segments[2].At, "latest") })
        {
            var (status, body) = await host.RestoreAsync(database, at, "ambiguous-" + label, cancellationToken);
            var direct = await host.RestoreDirectAsync(database, at, cancellationToken);
            ReproHost.Log($"restore at {label} {at:O} -> {(int)status} {body}; direct: {direct}");
            await Assert.That(status).IsEqualTo(HttpStatusCode.InternalServerError);
            await Assert.That(direct).IsEqualTo("InvalidDataException: the backup of this database is missing a log segment");
        }
    }

    // The first PUT under Prefix chooses the key; every PUT of that key while Prefix is set is stored, then answered 500.
    private sealed class AmbiguousPuts
    {
        private readonly Lock _gate = new();
        private int _attempts;
        private string? _prefix;

        public string? Prefix
        {
            get
            {
                lock (_gate)
                {
                    return _prefix;
                }
            }

            set
            {
                lock (_gate)
                {
                    _prefix = value;
                }
            }
        }

        public string? Key { get; private set; }

        public int Attempts => Volatile.Read(ref _attempts);

        public bool Matches(string key)
        {
            lock (_gate)
            {
                if (_prefix is not { } prefix || !key.StartsWith(prefix, StringComparison.Ordinal) || Key is not null && Key != key)
                {
                    return false;
                }

                Key = key;
                _attempts++;
                return true;
            }
        }
    }

    private sealed class AmbiguousPutHandler(AmbiguousPuts puts) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath).TrimStart('/');
            var key = path[(path.IndexOf('/', StringComparison.Ordinal) + 1)..];
            if (request.Method != HttpMethod.Put || !response.IsSuccessStatusCode || !puts.Matches(key))
            {
                return response;
            }

            response.Dispose();
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("<Error><Code>InternalError</Code><Message>stored, then the response was lost</Message></Error>", Encoding.UTF8, "application/xml"),
            };
        }
    }
}
