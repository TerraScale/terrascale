namespace TerraScale.Dashboard.Common.Databases
{
    public sealed record DatabaseDiscovery(IReadOnlyList<DatabaseRegion> Regions, IReadOnlyList<DatabaseRegion> Catalog);
}
