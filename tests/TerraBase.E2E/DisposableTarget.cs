using System.Text.Json;

namespace TerraBase.E2E;

/// <summary>
/// The disposable Fly app a machine-pinned run (<see cref="Cluster.FlyRevisionVariable"/>) targets
/// (docs/terrabase/e2e-disposable-app.md, "Target"). Every part is named explicitly, with no default: the app, its URL
/// (the app's own <c>.fly.dev</c> host), its org, its custom private network when the org is production's (option A),
/// and each region's machine. The configuration is checked before any flyctl call or request; then, read-only,
/// that Fly agrees (<see cref="CheckApp"/>, <see cref="CheckMachines"/>) before the first write or fault.
/// </summary>
internal static class DisposableTarget
{
    public const string AppVariable = "TERRABASE_E2E_FLY_APP";
    public const string OrgVariable = "TERRABASE_E2E_FLY_ORG";
    public const string NetworkVariable = "TERRABASE_E2E_FLY_NETWORK";
    public const string MachinesVariable = "TERRABASE_E2E_FLY_MACHINES";

    // Production's org; an app there is isolated only on its own private network (option A).
    private const string ProductionOrg = "terrascale";

    // The production apps (ts-terrabase, ts-auth, ts-dashboard in org terrascale); previews are ts-pr-<n>.
    private static readonly HashSet<string> Production = new(["ts-terrabase", "ts-auth", "ts-dashboard"], StringComparer.OrdinalIgnoreCase);

    /// <summary>A checked target: the app, its org, its custom network (empty outside option A) and each region's machine.</summary>
    internal sealed record Target(string App, string Org, string Network, IReadOnlyDictionary<string, string> Machines);

    /// <summary>The target, or an <see cref="InvalidOperationException"/> naming what is wrong; reads only <paramref name="variable"/>.</summary>
    public static Target Resolve(Func<string, string?> variable, IReadOnlyList<string> regions)
    {
        ArgumentNullException.ThrowIfNull(variable);
        ArgumentNullException.ThrowIfNull(regions);
        var app = Required(variable, AppVariable, "the disposable app");
        if (Production.Contains(app) || app.StartsWith("ts-pr-", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{app} is a production or preview app; a machine-pinned run targets only a disposable app");
        }

        if (!Uri.TryCreate(variable(Cluster.FlyUrlVariable), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps
            || !string.Equals(url.Host, $"{app}.fly.dev", StringComparison.OrdinalIgnoreCase) || url.AbsolutePath != "/")
        {
            throw new InvalidOperationException($"{Cluster.FlyUrlVariable} must be https://{app}.fly.dev/, the disposable app's own host");
        }

        var org = Required(variable, OrgVariable, "the disposable app's org");
        if (org.StartsWith("terrascale-previews", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{org} is the PR previews' org; a machine-pinned run targets only a disposable app's org");
        }

        var network = variable(NetworkVariable) ?? "";
        if (string.Equals(org, ProductionOrg, StringComparison.OrdinalIgnoreCase) && network.Length == 0)
        {
            throw new InvalidOperationException($"{org} is production's org: option A needs {NetworkVariable} naming the app's own private network");
        }

        var machines = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in Required(variable, MachinesVariable, "each region's machine as gru=<id>,sjc=<id>,fra=<id>").Split(','))
        {
            if (entry.Split('=') is not [{ Length: > 0 } region, { Length: > 0 } machine] || !machines.TryAdd(region.Trim(), machine.Trim()))
            {
                throw new InvalidOperationException($"{MachinesVariable} must list each region once as region=<machine id>; got {entry}");
            }
        }

        var missing = regions.Where(region => !machines.ContainsKey(region)).ToArray();
        return missing.Length == 0
            ? new Target(app, org, network, machines)
            : throw new InvalidOperationException($"{MachinesVariable} names no machine for {string.Join(", ", missing)}");
    }

    /// <summary>Throws unless <c>flyctl apps list --json</c> shows the app in the expected org, on the expected network.</summary>
    public static void CheckApp(Target target, JsonElement apps)
    {
        ArgumentNullException.ThrowIfNull(target);
        var app = apps.EnumerateArray().Where(found => found.GetProperty("Name").GetString() == target.App).ToArray() is [var only]
            ? only
            : throw new InvalidOperationException($"flyctl lists no single app {target.App}");
        var org = app.GetProperty("Organization").GetProperty("Slug").GetString();
        var network = app.GetProperty("Network").GetString() ?? "";
        if (org != target.Org || network != target.Network)
        {
            throw new InvalidOperationException(
                $"{target.App} is in org {org} on network \"{network}\"; the run expects org {target.Org} on network \"{target.Network}\"");
        }
    }

    /// <summary>Throws unless <c>flyctl machine list --json</c> shows each expected machine in its region and no other there.</summary>
    public static void CheckMachines(Target target, JsonElement listed)
    {
        ArgumentNullException.ThrowIfNull(target);
        var machines = listed.EnumerateArray().Select(machine => (Id: machine.GetProperty("id").GetString(), Region: machine.GetProperty("region").GetString())).ToArray();
        foreach (var (region, expected) in target.Machines)
        {
            var there = machines.Where(machine => machine.Region == region).Select(machine => machine.Id).ToArray();
            if (there is not [var id] || id != expected)
            {
                throw new InvalidOperationException($"{target.App} must have exactly machine {expected} in {region}; flyctl lists [{string.Join(", ", there)}]");
            }
        }
    }

    private static string Required(Func<string, string?> variable, string name, string what) =>
        variable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"a machine-pinned run needs {name} naming {what}; it has no default");
}
