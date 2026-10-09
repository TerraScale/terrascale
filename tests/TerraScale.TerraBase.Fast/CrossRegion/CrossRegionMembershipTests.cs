using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Bogus;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine.Cluster;
using TerraBase.Features.CrossRegion;
using TerraBase.Features.CrossRegion.V1;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.CrossRegion;

public sealed class CrossRegionMembershipTests
{
    private const string Credentials = "-----BEGIN NATS USER JWT-----\neyJ0.e30.x\n------END NATS USER JWT------\n-----BEGIN USER NKEY SEED-----\nSUAEXAMPLE\n------END USER NKEY SEED------\n";
    private const string GruLabel = "region=\"gru-1\"";
    private static readonly string[] InSfo = ["sfo-1"];
    private static readonly string[] KnownMembers = ["gru-a", "gru-b", "gru-a", "gru-b"];
    private static readonly string[] HolderMembers = ["gru-c", "gru-c"];

    [Test]
    [Arguments("direct")]
    [Arguments("shared-page")]
    [Arguments("member-page")]
    [Arguments("malformed")]
    public async Task Members_discovered_in_every_reply_preserve_a_silent_holders_database_and_resume_after_heal(string discovery)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var faker = new Faker();
        var network = new FakeRegionNetwork();
        network.Partition("gru", "sfo");
        var directory = Directory.CreateTempSubdirectory("terrabase-wan-discovery-").FullName;
        try
        {
            var credentials = Path.Combine(directory, "user.creds");
            await File.WriteAllTextAsync(credentials, Credentials, token);
            await using var a = await StartAsync(network, credentials, "gru-1", "gru", "gru-a");
            await using var b = await StartAsync(network, credentials, "gru-1", "gru", "gru-b");
            await using var c = await StartAsync(network, credentials, "gru-1", "gru", "gru-c");
            var bus = new MembershipBus(network.Join("sfo", "sfo-a"), discovery);
            await using var sfo = await StartAsync(network, credentials, "sfo-1", "sfo", "sfo-a", bus);

            if (discovery is "shared-page" or "member-page")
            {
                // Two genuine heads force a continuation on A's shared reply or B's direct reply.
                var paged = discovery == "shared-page" ? a : b;
                var seed = await paged.CreateManagedDatabaseAsync("page-one");
                _ = await paged.CreateManagedDatabaseAsync(seed.Organization, seed.Project, "page-two");
            }

            var organization = EntityIds.New().ToString();
            var project = EntityIds.New().ToString();
            var organizationName = faker.Company.CompanyName();
            var projectName = faker.Random.AlphaNumeric(12);
            foreach (var api in new[] { c, sfo })
            {
                using var service = api.Service(api.User);
                using var registered = await service.PostAsJsonAsync(ManagementApi.Uri("/v1/management/commits"), new
                {
                    id = EntityIds.New().ToString(),
                    ops = new object[]
                    {
                        new { op = "create_organization", id = organization, name = organizationName },
                        new { op = "create_project", organization, id = project, name = projectName },
                    },
                }, token);
                registered.EnsureSuccessStatusCode();
            }

            // A and B never register this project or hold its database; only C can supply its heads and writes.
            using var created = await c.Client.PostAsJsonAsync(
                ManagementApi.Uri($"/v1/management/organizations/{organization}/projects/{project}/databases"),
                new { name = "canary", replicaRegions = InSfo }, token);
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
            var record = await created.Content.ReadFromJsonAsync<JsonElement>(token);
            var database = new ManagedDatabaseRef(organization, project, record.GetProperty("id").GetString()!, "canary");
            using var source = await c.CreateClientAsync(organization, "documents:read", "documents:write");
            using var target = await sfo.CreateClientAsync(organization, "documents:read", "documents:write");
            await ManagementApi.CreateCollectionAsync(source, database, "items");
            var document = ManagementApi.Uri(ManagementApi.DocumentPath(database, "items", "value"));
            var value = faker.Lorem.Sentence();
            using var written = await source.PutAsJsonAsync(document, new { value }, token);
            await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.Created);

            network.Heal();
            await EventuallyReadAsync(target, document, value, written.Headers.ETag!.Tag);
            await TerraBaseTestServer.EventuallyAsync(async cancellationToken =>
                await sfo.Server.MetricAsync("terrabase_wan_members", GruLabel, cancellationToken) == 3
                && await sfo.Server.MetricAsync("terrabase_wan_lag", GruLabel, cancellationToken) == 0);
            var file = Path.Combine(sfo.Server.DataDirectory, organization, project, database.Id + ".db");
            await Assert.That(File.Exists(file)).IsTrue();

            var roundsBeforeSilence = bus.Rounds;
            if (discovery == "malformed")
            {
                bus.Malformed = true;
            }
            else
            {
                network.Mute("gru-c");
            }

            // Drain the last healthy round before writing: malformed B leaves C reachable on the network.
            await TerraBaseTestServer.EventuallyAsync(async cancellationToken =>
                await sfo.Server.MetricAsync("terrabase_wan_members", GruLabel, cancellationToken) == (discovery == "malformed" ? 1 : 2));
            var update = value + " " + faker.Lorem.Sentence();
            using var updated = await source.PutAsJsonAsync(document, new { value = update }, token);
            await Assert.That(updated.StatusCode).IsEqualTo(HttpStatusCode.OK);
            // A missing C or a malformed B reply cannot authorize deleting C's durable record.
            await TerraBaseTestServer.EventuallyAsync(async cancellationToken =>
                bus.Rounds >= roundsBeforeSilence + 3
                && await sfo.Server.MetricAsync("terrabase_wan_members", GruLabel, cancellationToken) == (discovery == "malformed" ? 1 : 2)
                && await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, cancellationToken) > 3);
            using var preserved = await sfo.Client.GetAsync(ManagementApi.Uri(database.Path), token);
            await Assert.That(preserved.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(File.Exists(file)).IsTrue();
            using var retained = await target.GetAsync(document, token);
            await Assert.That(retained.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var retainedBody = await retained.Content.ReadFromJsonAsync<JsonElement>(token);
            await Assert.That(retainedBody.GetProperty("document").GetProperty("value").GetString()).IsEqualTo(value);
            await Assert.That(retained.Headers.ETag!.Tag).IsEqualTo(written.Headers.ETag!.Tag);

            network.Heal();
            bus.Malformed = false;
            await EventuallyReadAsync(target, document, update, updated.Headers.ETag!.Tag);
            await TerraBaseTestServer.EventuallyAsync(async cancellationToken =>
                await sfo.Server.MetricAsync("terrabase_wan_members", GruLabel, cancellationToken) == 3
                && await sfo.Server.MetricAsync("terrabase_wan_lag", GruLabel, cancellationToken) == 0
                && await sfo.Server.MetricAsync("terrabase_wan_staleness_seconds", GruLabel, cancellationToken) < 3);
            await Assert.That(bus.RepeatedQueries).IsEqualTo(0);
            if (discovery is "shared-page" or "member-page")
            {
                await Assert.That(bus.PageReplies).IsGreaterThan(0);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Task<ManagementApi> StartAsync(
        FakeRegionNetwork network, string credentials, string region, string cell, string node, IRegionBus? bus = null) =>
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
        }, managementCredential: true);

    private static Task EventuallyReadAsync(HttpClient client, Uri document, string value, string version) =>
        TerraBaseTestServer.EventuallyAsync(async token =>
        {
            using var response = await client.GetAsync(document, token);
            return response.StatusCode == HttpStatusCode.OK && response.Headers.ETag?.Tag == version
                && (await response.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("document").GetProperty("value").GetString() == value;
        });

    // Only SFO's bus changes views: application responders still produce every head and change group from real files.
    private sealed class MembershipBus(IRegionBus inner, string discovery) : IRegionBus
    {
        private readonly Lock _gate = new();
        private readonly HashSet<string> _queried = [with(StringComparer.Ordinal)];
        private int _rounds;
        private int _repeatedQueries;
        private int _pageReplies;

        public int Rounds => Volatile.Read(ref _rounds);

        public int RepeatedQueries => Volatile.Read(ref _repeatedQueries);

        public int PageReplies => Volatile.Read(ref _pageReplies);

        public bool Malformed
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> RequestManyAsync(
            string subject, ReadOnlyMemory<byte> request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _queried.Clear();
            }

            _ = Interlocked.Increment(ref _rounds);
            await foreach (var data in inner.RequestManyAsync(subject, request, cancellationToken))
            {
                var reply = HeadsReply.Parser.ParseFrom(data.Span);
                lock (_gate)
                {
                    _ = _queried.Add(WanProtocol.MemberSubject("gru", reply.Member));
                }

                yield return Rewrite(data, WanRequest.Parser.ParseFrom(request.Span));
            }
        }

        public async ValueTask<ReadOnlyMemory<byte>> RequestAsync(string subject, ReadOnlyMemory<byte> request, CancellationToken cancellationToken)
        {
            var parsed = WanRequest.Parser.ParseFrom(request.Span);
            if (parsed.Heads is { After.Length: 0 })
            {
                lock (_gate)
                {
                    if (!_queried.Add(subject))
                    {
                        _ = Interlocked.Increment(ref _repeatedQueries);
                        throw new IOException("a member was queried twice in one round");
                    }
                }
            }

            return Rewrite(await inner.RequestAsync(subject, request, cancellationToken), parsed);
        }

        public Task ServeAsync(string subject, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<byte[]>> answer, CancellationToken cancellationToken) =>
            inner.ServeAsync(subject, answer, cancellationToken);

        private ReadOnlyMemory<byte> Rewrite(ReadOnlyMemory<byte> data, WanRequest request)
        {
            if (request.Heads is null)
            {
                return data;
            }

            var reply = HeadsReply.Parser.ParseFrom(data.Span).Clone();
            var pagedMember = discovery == "shared-page" ? "gru-a" : discovery == "member-page" ? "gru-b" : null;
            if (reply.Member == pagedMember && reply.Heads.Count > 1)
            {
                while (reply.Heads.Count > 1)
                {
                    reply.Heads.RemoveAt(reply.Heads.Count - 1);
                }

                reply.More = true;
            }

            var continuation = request.Heads.After.Length > 0;
            if (continuation)
            {
                _ = Interlocked.Increment(ref _pageReplies);
            }

            var discoversHolder = reply.Member == "gru-c"
                || ((discovery is "direct" or "malformed") && reply.Member == "gru-b")
                || (reply.Member == pagedMember && continuation);
            reply.Members.Clear();
            reply.Members.AddRange(KnownMembers);
            if (discoversHolder)
            {
                reply.Members.AddRange(HolderMembers);
            }

            if (Malformed && reply.Member == "gru-b")
            {
                reply.Members.Add("bad.member");
            }

            return reply.ToByteArray();
        }
    }
}
