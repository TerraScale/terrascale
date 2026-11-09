using TerraBase.Errors;

namespace TerraBase.Features.Management;

internal static class ManagementErrors
{
    public static readonly NotFound NotFound = new("the resource does not exist");

    public static readonly Conflict NameTaken = new("name is already taken", "name_taken");

    public static readonly Conflict NotEmpty = new("delete what it contains first", "not_empty");

    public static readonly Conflict IdTaken = new("the id is already taken", "id_taken");

    // The operation belongs to the database's home region (docs/cross-region.md).
    public static readonly Conflict WrongRegion = new("the database is managed in another region: send it to its home region", "wrong_region");

    public static readonly Conflict PlatformPlacementLocked = new("platform databases are placed in every region and their placement cannot change", "platform_placement_locked");

    // The plan's database allowance: https://terrascale.tech/pricing.json.
    public static Conflict DatabaseLimit(string plan, long allowed) => new(
        $"the organization's {plan} plan allows {allowed} database{(allowed == 1 ? "" : "s")}; delete a database or upgrade the plan at https://dash.terrascale.tech/organization?tab=billing",
        DatabaseLimitCode);

    public const string DatabaseLimitCode = "database_limit";

    public static readonly Unavailable CatalogUnsupported = new("the management catalog has an old or unsupported format; discard the affected data");

}
