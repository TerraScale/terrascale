using TerraBase.Errors;

namespace TerraBase.Features.Management;

internal static class ManagementErrors
{
    public static readonly NotFound NotFound = new("the resource does not exist");

    public static readonly Conflict NameTaken = new("name is already taken", "name_taken");

    public static readonly Conflict NotEmpty = new("delete what it contains first", "not_empty");

    public static readonly Conflict IdTaken = new("the id is already taken", "id_taken");

    public static readonly Conflict WrongRegion = new("the database is managed in another region: send it to its home region", "wrong_region");

    public static readonly Conflict PlatformPlacementLocked = new("platform databases are placed in every region and their placement cannot change", "platform_placement_locked");

    public static readonly Conflict PlatformDatabaseProtected = new("platform databases cannot be deleted, renamed or given a TTL", "platform_database_protected");

    public static readonly Conflict PlatformProjectProtected = new("platform projects cannot be closed", "platform_project_protected");

    public static readonly Forbidden PlatformPlanLocked = new("the platform organization's plan cannot change", "platform_plan_locked");

    public static Conflict DatabaseLimit(string plan, long allowed) => new(
        $"the organization's {plan} plan allows {allowed} database{(allowed == 1 ? "" : "s")}; delete a database or upgrade the plan at https://dash.terrascale.tech/organization?tab=billing",
        DatabaseLimitCode);

    public const string DatabaseLimitCode = "database_limit";

    public static readonly Unavailable CatalogUnsupported = new("the management catalog has an old or unsupported format; discard the affected data");

}
