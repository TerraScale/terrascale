extern alias tsauth;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.Platform;
using TerraBase.Features.Regions;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraScale.DotNet.TerraBase;
using tsauth::TerraScale.Authentication.Storage;

namespace TerraBase.Slow.Sync;

public sealed class RegionalSocketForwardingTests
{
    private const int LargeSave = 300;

    [Test]
    [Timeout(120_000)]
    public async Task Sql_sessions_sent_to_the_regional_follower_run_on_the_writer_over_both_listeners_an_unreachable_writer_answers_wrong_region_and_an_unanswered_write_answers_write_outcome_unknown(CancellationToken token)
    {
        await using var nats = await JetStreamServer.StartAsync(token);
        using var issuer = new TestIssuer();
        var storage = new FakeS3();
        var masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var directory = Directory.CreateTempSubdirectory("regional-socket-").FullName;
        using var reservation = new TcpListener(IPAddress.Any, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var peers = Path.Combine(directory, "peers");
        await File.WriteAllTextAsync(peers, "gru-1 gru 127.0.0.2\ngru-2 gru 127.0.0.3\n", token);
        try
        {
            await using var follower = await StartAsync("gru-2", "127.0.0.3");
            await using var writer = await StartAsync("gru-1", "127.0.0.2");
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(writer.Server.Services.GetRequiredService<RegionalOwners>().Writer), TimeSpan.FromSeconds(10));
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(follower.Server.Services.GetRequiredService<RegionalOwners>().Find("gru")?.Node == "gru-1"), TimeSpan.FromSeconds(10));
            await Assert.That(follower.Server.Services.GetRequiredService<RegionalOwners>().Writer).IsFalse();
            await Assert.That(await writer.Server.Services.GetRequiredService<IPlatformCatalog>().EnsurePlatformAsync(token) is Done).IsTrue();
            var organization = await writer.CreateOrganizationAsync("Org " + EntityIds.New());
            await writer.SetPlanAsync(writer.User, organization, "enterprise", token);
            var project = await writer.CreateProjectAsync(organization, "Project");
            var bearer = issuer.Mint(writer.User, descriptor =>
            {
                descriptor.Claims["project_id"] = project;
                descriptor.Claims["org_id"] = organization;
                descriptor.Claims["scope"] = "databases:read databases:write sql:read sql:write";
            });
            using var management = writer.WithToken(bearer);
            foreach (var name in new[] { "peer", "client" })
            {
                using var created = await management.PostAsJsonAsync(ManagementApi.Uri($"/v1/projects/{project}/databases"), new { name }, token);
                await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync(token));
            }

            var followerInternal = new Uri($"http://127.0.0.3:{port}");
            using var peer = PeerTls.CreateClient(followerInternal);
            using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
            await Task.WhenAll(
                ExerciseAsync(new TerraBaseSqlClient(peer, new TerraBaseSqlClientOptions { BaseAddress = followerInternal }).Database(project, "peer"), token),
                ExerciseAsync(new TerraBaseSqlClient(client, new TerraBaseSqlClientOptions
                {
                    BaseAddress = follower.Server.ClientAddress,
                    AccessTokenProvider = _ => ValueTask.FromResult(bearer),
                }).Database(project, "client"), token));

            await File.WriteAllTextAsync(peers, "gru-1 gru 127.0.0.4\ngru-2 gru 127.0.0.3\n", token);
            var misdirected = await Assert.ThrowsAsync<TerraBaseWrongRegionException>(() =>
                new TerraBaseSqlClient(peer, new TerraBaseSqlClientOptions { BaseAddress = followerInternal }).Database(project, "peer").QueryAsync("SELECT 1", cancellationToken: token));
            await Assert.That((misdirected!.ErrorCode, misdirected.HomeRegion)).IsEqualTo(("wrong_region", "gru"));
            await Assert.That(misdirected.ResponseHeaders["Location"]).IsEquivalentTo([$"https://gru.api.terrascale.tech/v1/projects/{project}/databases/peer/sql/execute"]);

            var received = 0;
            var lost = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
            lost.Logging.ClearProviders();
            lost.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Parse("127.0.0.5"), port, listener => listener.Protocols = HttpProtocols.Http2));
            await using var unanswering = lost.Build();
            unanswering.Run(async context =>
            {
                Interlocked.Increment(ref received);
                await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted);
                context.Abort();
            });
            await unanswering.StartAsync(token);
            await File.WriteAllTextAsync(peers, "gru-1 gru 127.0.0.5\ngru-2 gru 127.0.0.3\n", token);
            foreach (var database in new[]
            {
                new TerraBaseSqlClient(peer, new TerraBaseSqlClientOptions { BaseAddress = followerInternal }).Database(project, "peer"),
                new TerraBaseSqlClient(client, new TerraBaseSqlClientOptions
                {
                    BaseAddress = follower.Server.ClientAddress,
                    AccessTokenProvider = _ => ValueTask.FromResult(bearer),
                }).Database(project, "client"),
            })
            {
                var unknown = await Assert.ThrowsAsync<TerraBaseUnavailableException>(() => database.ExecuteAsync("DELETE FROM unanswered", cancellationToken: token));
                await Assert.That((unknown!.StatusCode, unknown.ErrorCode, unknown.OutcomeRegion)).IsEqualTo((HttpStatusCode.ServiceUnavailable, "write_outcome_unknown", "gru"));
            }

            await Assert.That(received).IsEqualTo(2).Because("the SDK never resends a write whose outcome is unknown");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Task<ManagementApi> StartAsync(string node, string address) => ManagementApi.StartAsync(settings =>
        {
            settings["region"] = "gru";
            settings["data-directory"] = Path.Combine(directory, node);
            settings["master-key"] = masterKey;
            storage.Configure(settings);
        }, services =>
        {
            storage.Route(services);
            var options = (TerraBaseOptions)services.Last(descriptor => descriptor.ServiceType == typeof(TerraBaseOptions)).ImplementationInstance!;
            services.Replace(ServiceDescriptor.Singleton(options with { MachineId = node, PeersFile = peers, InternalPort = port, NatsUrl = nats.Url, NatsToken = nats.Token }));
            services.Replace(ServiceDescriptor.Singleton<IRegionCatalog>(new Serving()));
            services.Configure<KestrelServerOptions>(kestrel => kestrel.Listen(IPAddress.Parse(address), port, listener =>
            {
                listener.Protocols = HttpProtocols.Http2;
                listener.UseTerraBaseListener(TerraBaseListener.Internal);
            }));
        }, sharedIssuer: issuer);
    }

    private static async Task ExerciseAsync(TerraBaseSqlDatabase database, CancellationToken token)
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>().UseTerraBase(database).Options;
        await using (var context = new AuthDbContext(options))
        {
            await context.Database.MigrateAsync(token);
            await Assert.That(await context.Database.GetAppliedMigrationsAsync(token)).IsEquivalentTo(context.Database.GetMigrations());
        }

        await using (var context = new AuthDbContext(options))
        {
            await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var transaction = await context.Database.BeginTransactionAsync(token);
                Probe(context);
                await context.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
            });
            for (var index = 0; index < LargeSave; index++)
            {
                Probe(context);
            }

            await context.SaveChangesAsync(token);
        }

        await using (var context = new AuthDbContext(options))
        {
            await Assert.That(await context.StorageProbes.CountAsync(token)).IsEqualTo(LargeSave + 1);
        }
    }

    private static void Probe(AuthDbContext context) =>
        context.StorageProbes.Add(new StorageProbe(EntityIds.New().ToString(), DateTimeOffset.UtcNow))
            .Property(AuthDbContext.VersionProperty).CurrentValue = EntityIds.New().ToString();

    private sealed class Serving : IRegionCatalog
    {
        public ValueTask<IReadOnlyList<string>> ServingAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<string>>(["gru"]);
    }
}
