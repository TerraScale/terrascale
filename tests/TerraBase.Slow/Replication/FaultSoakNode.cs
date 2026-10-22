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
        var authority = api.Issuer.Mint(api.User, token =>
        {
            token.NotBefore = DateTime.UtcNow.AddMinutes(-5);
            token.Expires = DateTime.UtcNow.AddHours(1);
        });
        api.Issuer.GrantProject(authority, Read("ORGANIZATION"), Read("PROJECT"));
        var projectToken = api.Issuer.Mint(api.User, token =>
        {
            token.NotBefore = DateTime.UtcNow.AddMinutes(-5);
            token.Expires = DateTime.UtcNow.AddHours(1);
            token.Claims!["project_id"] = Read("PROJECT");
            token.Claims!["scope"] = "databases:read databases:write";
        });
        await File.WriteAllTextAsync(info + ".partial", new JsonObject
        {
            ["internal"] = api.Server.BaseAddress.ToString(),
            ["peer"] = api.Server.PeerAddress!.ToString(),
            ["client"] = api.Server.ClientAddress!.ToString(),
            ["authority"] = authority,
            ["projectToken"] = projectToken,
        }.ToJsonString(), cancellationToken);
        File.Move(info + ".partial", info!, overwrite: true);
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
