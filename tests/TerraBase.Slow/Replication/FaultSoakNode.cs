using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine.Cluster;
using TerraBase.Testing;

namespace TerraBase.Slow.Replication;

/// <summary>A real application node started by the Slow fault-soak fixture in a child process.</summary>
[Category("fault-soak-host")]
public sealed class FaultSoakNode
{
    private static readonly string[] Regions = ["gru", "sjc", "fra"];

    internal const string Prefix = "TERRABASE_SOAK_";

    [Test]
    [Timeout(6 * 3600 * 1000)]
    public async Task Hosts_one_region_until_killed(CancellationToken cancellationToken)
    {
        var info = Environment.GetEnvironmentVariable(Prefix + "INFO");
        Skip.When(string.IsNullOrEmpty(info), "a child node started by FaultSoakCluster");
        static string Read(string name)
        {
            return Environment.GetEnvironmentVariable(Prefix + name) ?? throw new InvalidOperationException($"missing child node setting {name}");
        }
        var offset = TimeSpan.FromMilliseconds(int.Parse(Read("SKEW"), CultureInfo.InvariantCulture));
        await using var api = await ManagementApi.StartAsync(settings =>
        {
            settings["region"] = Read("REGION");
            settings["node-name"] = Read("MACHINE");
            settings["data-directory"] = Read("DATA");
        }, services =>
        {
            if (Read("LOCAL") != "1")
            {
                services.AddSingleton<IMemberDirectory>(new Members(Read("MEMBERS")));
            }
        },
            time: new SkewedClock(offset), cancellationToken: cancellationToken, userId: Read("OWNER"));
        var projectToken = api.Issuer.Mint(api.User, token =>
        {
            token.NotBefore = DateTime.UtcNow.AddMinutes(-5);
            token.Expires = DateTime.UtcNow.AddHours(1);
            token.Claims!["project_id"] = Read("PROJECT");
            token.Claims!["org_id"] = Read("ORGANIZATION");
            token.Claims!["scope"] = "databases:read databases:write";
        });
        await File.WriteAllTextAsync(info + ".partial", new JsonObject
        {
            ["internal"] = api.Server.BaseAddress.ToString(),
            ["peer"] = api.Server.PeerAddress!.ToString(),
            ["client"] = api.Server.ClientAddress!.ToString(),
            ["projectToken"] = projectToken,
        }.ToJsonString(), cancellationToken);
        File.Move(info + ".partial", info!, overwrite: true);
        try
        {
            var database = new TerraBase.Tenancy.TenantContext(Read("ORGANIZATION"), TerraBase.Tenancy.PlanTier.Enterprise, Read("PROJECT"), Read("DATABASE"));
            if (Read("REGION") == "gru")
            {
                await CatalogFixture.RegisterProjectAsync(api, api.User, database.OrganizationId, database.ProjectId!, cancellationToken);
                await api.SetPlanAsync(api.User, database.OrganizationId, "enterprise", cancellationToken);
                await CatalogFixture.RegisterAsync(api, api.User, database, "soak", [.. Regions.Where(region => region != "gru")], cancellationToken);
            }
            else
            {
                await CatalogFixture.MaterializeAsync(api, database, "soak", cancellationToken);
            }
        }
        catch (Exception error)
        {
            await using var output = Console.OpenStandardError();
            await output.WriteAsync(System.Text.Encoding.UTF8.GetBytes(error + Environment.NewLine), CancellationToken.None);
            throw;
        }
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }

    // Only wall time is skewed: heartbeats, timers and deadlines keep advancing normally.
    private sealed class SkewedClock(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow() + offset;
    }

    private sealed class Members(string directory) : IMemberDirectory
    {
        public Task<ImmutableArray<ClusterMember>> ListAsync(CancellationToken cancellationToken)
        {
            List<ClusterMember> members = [];
            foreach (var file in Directory.EnumerateFiles(directory).Where(file => !Path.GetFileName(file).StartsWith('.')))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (File.ReadAllText(file).Split('\n') is [var region, var address])
                    {
                        members.Add(new ClusterMember(Path.GetFileName(file), address, region, string.Empty));
                    }
                }
                catch (FileNotFoundException)
                {
                    // The parent removed a killed node during this refresh.
                }
            }

            return Task.FromResult<ImmutableArray<ClusterMember>>([.. members.OrderBy(member => member.Name, StringComparer.Ordinal)]);
        }
    }
}
