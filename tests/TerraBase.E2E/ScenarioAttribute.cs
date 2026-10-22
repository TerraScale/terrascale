namespace TerraBase.E2E;

/// <summary>Where a test's nodes run. On ts-orch every scenario's nodes are the service's nodes, whatever the local kind.</summary>
public enum Nodes
{
    /// <summary>No TerraBase node: a check of the harness itself (a proxy, a pin), not of a feature.</summary>
    Harness,

    /// <summary>In-process nodes of one test host (real Program and listeners, shared process).</summary>
    InProcess,

    /// <summary>Separate operating-system processes of the terrabase binary.</summary>
    Processes,
}

/// <summary>The faults a scenario injects; a partition through the membership view is simulated, a TCP proxy is a network fault.</summary>
[Flags]
public enum Faults
{
    None = 0,

    /// <summary>Two regions removed from each other's peer links by the harness (no real network split).</summary>
    SimulatedPartition = 1,

    /// <summary>Peer traffic through a TCP proxy that delays, caps and resets connections.</summary>
    NetworkFault = 2,

    /// <summary>SIGKILL of a node process.</summary>
    ProcessKill = 4,

    /// <summary>A node stopped cleanly and started again on its data (a region removed from the service and applied again on ts-orch).</summary>
    NodeStop = 8,

    /// <summary>A node's storage held so its commits wait.</summary>
    StorageStall = 16,

    /// <summary>A node's clock set ahead or behind its peers.</summary>
    ClockSkew = 32,

    /// <summary>A stopped node's files edited or restored from an older copy.</summary>
    FileTampering = 64,
}

/// <summary>
/// Declares what a test runs on and what it breaks, so every summary line says whether a result came from separate
/// processes or one host, and from a simulated partition or a real network fault. A test without it is reported as
/// undeclared, which the summary check refuses.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ScenarioAttribute(Nodes nodes, Faults faults = Faults.None) : Attribute
{
    public Nodes Nodes { get; } = nodes;

    public Faults Faults { get; } = faults;
}
