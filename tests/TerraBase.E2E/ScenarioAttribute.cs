namespace TerraBase.E2E;

public enum Nodes
{
    Harness,
    InProcess,
    Processes,
}

[Flags]
public enum Faults
{
    None = 0,
    ProcessKill = 1,
    FileTampering = 2,
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class ScenarioAttribute(Nodes nodes, Faults faults = Faults.None) : Attribute
{
    public Nodes Nodes { get; } = nodes;
    public Faults Faults { get; } = faults;
}
