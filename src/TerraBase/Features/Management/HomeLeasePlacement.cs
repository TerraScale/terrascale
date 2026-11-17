using TerraBase.Errors;

namespace TerraBase.Features.Management;

internal static class HomeLeasePlacement
{
    internal static readonly SetDatabaseRegionsResult Pending =
        new Unavailable("this node does not hold the database's home lease at its placement version, so the placement cannot change yet; retry");

    internal static long? Bumped(SetDatabaseRegionsResult result) => result is ManagedDatabase database ? database.PlacementVersion : null;
}
