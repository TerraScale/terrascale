using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Bogus;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine.Cluster;
using TerraBase.Features.CrossRegion;
using TerraBase.Features.CrossRegion.V1;
using TerraBase.Features.Replication;
using TerraBase.Testing;

namespace TerraBase.Fast.CrossRegion;

public sealed class CrossRegionHeadsIntegrityTests
{
    private const string Credentials = "-----BEGIN NATS USER JWT-----\neyJ0.e30.x\n------END NATS USER JWT------\n-----BEGIN USER NKEY SEED-----\nSUAEXAMPLE\n------END USER NKEY SEED------\n";
    private const string GruLabel = "region=\"gru-1\"";
    private static readonly string[] InSfo = ["sfo-1"];

    [Test]
    [Arguments("header")]
    [Arguments("payload")]
    [Arguments("raw-uuid")]
    public async Task Damaged_heads_cannot_delete_a_live_replica_or_adopt_a_database_and_a_healthy_sweep_resumes(string damage)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var faker = new Faker();
        var network = new FakeRegionNetwork();
        var clock = new RoundClock(DateTimeOffset.UtcNow);
        var bus = new DamagingBus(network.Join("sfo", "sfo-a"));
        var directory = Directory.CreateTempSubdirectory("terrabase-wan-heads-integrity-").FullName;
        try
        {
            var credentials = Path.Combine(directory, "user.creds");
            await File.WriteAllTextAsync(credentials, Credentials, token);
            await using var gru = await StartAsync(network, credentials, "gru-1", "gru", "gru-a");
            await using var sfo = await StartAsync(network, credentials, "sfo-1", "sfo", "sfo-a", bus, clock);
            try
            {
                var organization = Guid.NewGuid().ToString("D");
                var project = Guid.NewGuid().ToString("D");
                var organizationName = faker.Company.CompanyName();
                var projectName = faker.Random.AlphaNumeric(12);
                foreach (var api in new[] { gru, sfo })
                {
                    using var service = api.Service(api.User);
                    using var registered = await service.PostAsJsonAsync(ManagementApi.Uri("/v1/management/commits"), new
                    {
                        id = Guid.NewGuid().ToString("D"),
                        ops = new object[]
                        {
                            new { op = "create_organization", id = organization, name = organizationName },
                            new { op = "create_project", organization, id = project, name = projectName },
                        },
                    }, token);
                    registered.EnsureSuccessStatusCode();
                }

                var saved = await CreatePlacedAsync(gru, organization, project, "saved", token);
                using var source = await gru.CreateClientAsync(organization, "documents:read", "documents:write");
                using var target = await sfo.CreateClientAsync(organization, "documents:read", "documents:write");
                await ManagementApi.CreateCollectionAsync(source, saved, "items");
                var document = ManagementApi.Uri(ManagementApi.DocumentPath(saved, "items", "value"));
                var savedValue = faker.Lorem.Sentence();
                using var written = await source.PutAsJsonAsync(document, new { value = savedValue }, token);
                await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.Created);
                var savedVersion = written.Headers.ETag!.Tag;

                bus.Begin.TrySetResult();
                await clock.PausedAsync(token);
                await AssertReadAsync(target, document, savedValue, savedVersion, token);
                var savedFile = DatabaseFile(sfo, saved);
                await Assert.That(File.Exists(savedFile)).IsTrue();
                await AssertCatalogAsync(sfo, saved, [saved.Id], token);

                // The next round must protect a catalog candidate saved before its remote heads are read.
                var newcomer = await CreatePlacedAsync(gru, organization, project, "newcomer", token);
                await ManagementApi.CreateCollectionAsync(source, newcomer, "items");
                var newcomerDocument = ManagementApi.Uri(ManagementApi.DocumentPath(newcomer, "items", "value"));
                var newcomerValue = faker.Lorem.Sentence();
                using var newcomerWrite = await source.PutAsJsonAsync(newcomerDocument, new { value = newcomerValue }, token);
                await Assert.That(newcomerWrite.StatusCode).IsEqualTo(HttpStatusCode.Created);
                var updatedValue = savedValue + " " + faker.Lorem.Sentence();
                using var updated = await source.PutAsJsonAsync(document, new { value = updatedValue }, token);
                await Assert.That(updated.StatusCode).IsEqualTo(HttpStatusCode.OK);

                bus.DatabaseId = WanProtocol.Id(saved.Id);
                bus.Damage = damage;
                clock.Advance(WanPuller.Interval);
                await clock.PausedAsync(token);
                await Assert.That(bus.DamagedReplies).IsGreaterThan(0);
                await AssertCatalogAsync(sfo, saved, [saved.Id], token);
                await Assert.That(File.Exists(savedFile)).IsTrue();
                await AssertReadAsync(target, document, savedValue, savedVersion, token);
                using (var absent = await sfo.Client.GetAsync(ManagementApi.Uri(newcomer.Path), token))
                {
                    await Assert.That(absent.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
                }

                await Assert.That(File.Exists(DatabaseFile(sfo, newcomer))).IsFalse();
                await AssertReadAsync(source, document, updatedValue, updated.Headers.ETag!.Tag, token);
                clock.Advance(TimeSpan.FromMilliseconds(500));
                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token)).IsGreaterThan(0);

                bus.Damage = null;
                clock.Advance(TimeSpan.FromMilliseconds(500));
                await clock.PausedAsync(token);
                await AssertReadAsync(target, document, updatedValue, updated.Headers.ETag!.Tag, token);
                await AssertReadAsync(target, newcomerDocument, newcomerValue, newcomerWrite.Headers.ETag!.Tag, token);
                await AssertCatalogAsync(sfo, saved, [saved.Id, newcomer.Id], token);
                await Assert.That(File.Exists(savedFile)).IsTrue();
                await Assert.That(File.Exists(DatabaseFile(sfo, newcomer))).IsTrue();

                // A rejected round cannot certify its sweep; a fresh healthy sweep certifies catch-up.
                clock.Advance(WanPuller.Interval);
                await clock.PausedAsync(token);
                await Assert.That(await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, token)).IsEqualTo(0);
            }
            finally
            {
                bus.Begin.TrySetResult();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Task<ManagementApi> StartAsync(
        FakeRegionNetwork network, string credentials, string region, string cell, string node, IRegionBus? bus = null, RoundClock? clock = null) =>
        ManagementApi.StartAsync(settings =>
        {
            settings["region"] = region;
            settings["node-name"] = node;
            settings["wan-regions"] = "gru-1=gru,sfo-1=sfo";
            settings["nats-url"] = "nats://127.0.0.1:4222";
            settings["nats-credentials-file"] = credentials;
        }, services =>
        {
            services.AddSingleton(bus ?? network.Join(cell, node));
            services.AddSingleton<IMemberStore>(new FakeMemberStore(TimeProvider.System));
            if (clock is not null)
            {
                var registration = services.Single(service => service.ImplementationType == typeof(WanPuller));
                services[services.IndexOf(registration)] = ServiceDescriptor.Singleton<IHostedService>(
                    provider => ActivatorUtilities.CreateInstance<WanPuller>(provider, clock));
                services.AddSingleton(provider => ActivatorUtilities.CreateInstance<WanMetrics>(provider, clock));
            }
        }, managementCredential: true);

    private static async Task<ManagedDatabaseRef> CreatePlacedAsync(
        ManagementApi api, string organization, string project, string name, CancellationToken token)
    {
        using var response = await api.Client.PostAsJsonAsync(
            ManagementApi.Uri($"/v1/management/organizations/{organization}/projects/{project}/databases"),
            new { name, replicaRegions = InSfo }, token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var record = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        return new ManagedDatabaseRef(organization, project, record.GetProperty("id").GetString()!, name);
    }

    private static string DatabaseFile(ManagementApi api, ManagedDatabaseRef database) =>
        Path.Combine(api.Server.DataDirectory, database.Organization, database.Project, database.Id + ".db");

    private static async Task AssertCatalogAsync(ManagementApi api, ManagedDatabaseRef database, string[] expectedIds, CancellationToken token)
    {
        using var response = await api.Client.GetAsync(ManagementApi.Uri(database.ProjectPath + "/databases"), token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        var ids = page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!).ToArray();
        await Assert.That(ids).IsEquivalentTo(expectedIds);
        using var saved = await api.Client.GetAsync(ManagementApi.Uri(database.Path), token);
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    private static async Task AssertReadAsync(HttpClient client, Uri document, string value, string version, CancellationToken token)
    {
        using var response = await client.GetAsync(document, token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Headers.ETag?.Tag).IsEqualTo(version);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        await Assert.That(body.GetProperty("document").GetProperty("value").GetString()).IsEqualTo(value);
    }

    private sealed class RoundClock(DateTimeOffset started) : FakeTimeProvider(started)
    {
        private readonly Channel<bool> _pauses = Channel.CreateBounded<bool>(1);

        public async Task PausedAsync(CancellationToken token) =>
            _ = await _pauses.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(30), token);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            if (dueTime == WanPuller.Interval && !_pauses.Writer.TryWrite(true))
            {
                throw new InvalidOperationException("a WAN round pause was not consumed");
            }

            return timer;
        }
    }

    // Real responders supply every page and pull; only this receiving transport damages heads replies.
    private sealed class DamagingBus(IRegionBus inner) : IRegionBus
    {
        private int _damagedReplies;

        public TaskCompletionSource Begin { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DamagedReplies => Volatile.Read(ref _damagedReplies);

        public ByteString DatabaseId { get; set; } = ByteString.Empty;

        public string? Damage
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> RequestManyAsync(
            string subject, ReadOnlyMemory<byte> request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Begin.Task.WaitAsync(cancellationToken);
            await foreach (var reply in inner.RequestManyAsync(subject, request, cancellationToken))
            {
                yield return DamageReply(reply);
            }
        }

        public async ValueTask<ReadOnlyMemory<byte>> RequestAsync(string subject, ReadOnlyMemory<byte> request, CancellationToken cancellationToken)
        {
            var reply = await inner.RequestAsync(subject, request, cancellationToken);
            return WanRequest.Parser.ParseFrom(request.Span).Heads is not null ? DamageReply(reply) : reply;
        }

        public Task ServeAsync(string subject, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<byte[]>> answer, CancellationToken cancellationToken) =>
            inner.ServeAsync(subject, answer, cancellationToken);

        private ReadOnlyMemory<byte> DamageReply(ReadOnlyMemory<byte> reply)
        {
            if (Damage is not { } damage)
            {
                return reply;
            }

            byte[] damaged;
            if (damage == "raw-uuid")
            {
                // This exact transport bit flip keeps protobuf and all UUID lengths valid, as the lossy WAN fault does.
                damaged = WanProtocol.DecodeHeads(reply).ToByteArray();
                var offset = damaged.AsSpan().IndexOf(DatabaseId.Span);
                if (DatabaseId.Length != 16 || offset < 0)
                {
                    throw new InvalidOperationException("the real heads reply did not contain the saved database UUID");
                }

                damaged[offset + 15] ^= 0x5a;
                var changed = HeadsReply.Parser.ParseFrom(damaged);
                if (changed.Heads.Any(head => head.DatabaseId.Length != 16) || changed.Heads.Any(head => head.DatabaseId == DatabaseId))
                {
                    throw new InvalidOperationException("the injected fault did not change only the saved UUID bytes");
                }
            }
            else
            {
                damaged = reply.ToArray();
                if (damaged.Length <= ApplicationEnvelope.HeaderBytes)
                {
                    throw new InvalidOperationException("the real heads reply was not an application envelope");
                }

                damaged[damage == "header" ? 0 : ApplicationEnvelope.HeaderBytes] ^= 0x5a;
            }

            _ = Interlocked.Increment(ref _damagedReplies);
            return damaged;
        }
    }
}
