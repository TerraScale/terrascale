using TerraBase.Errors;

namespace TerraBase.Features.Management;

/// <summary>Management failures shared by several operations, with their stable codes.</summary>
internal static class ManagementErrors
{
    /// <summary>The resource, or one of its parents, does not exist, or the user is not a member of its organization.</summary>
    public static readonly NotFound NotFound = new("the resource does not exist");

    /// <summary>The acting member's role does not allow the operation.</summary>
    public static readonly Forbidden Forbidden = new("your role in the organization does not allow this");

    /// <summary>The acting member's role, a viewer's for example, does not allow the change.</summary>
    public static readonly Forbidden RoleTooLow = new("your role in the organization does not allow this", "forbidden_role");

    /// <summary>Another resource of the same parent already has the name.</summary>
    public static readonly Conflict NameTaken = new("name is already taken", "name_taken");

    /// <summary>The resource still has children; delete them first.</summary>
    public static readonly Conflict NotEmpty = new("delete what it contains first", "not_empty");

    /// <summary>The change would leave the organization without an owner.</summary>
    public static readonly Conflict LastOwner = new("the organization must keep an owner", "last_owner");

    /// <summary>The user is already a member of the organization.</summary>
    public static readonly Conflict AlreadyMember = new("the user is already a member", "already_member");

    /// <summary>An unexpired invitation for the email address is pending.</summary>
    public static readonly Conflict InvitationPending = new("an invitation for this email is pending", "invitation_pending");

    /// <summary>The member is already an owner.</summary>
    public static readonly Conflict AlreadyOwner = new("the member is already an owner", "already_owner");

    /// <summary>The invitation expired.</summary>
    public static readonly Conflict Expired = new("the invitation expired", "expired");

    /// <summary>A client-chosen id is already used by another resource.</summary>
    public static readonly Conflict IdTaken = new("the id is already taken", "id_taken");

    /// <summary>The invitation is no longer pending: it was accepted, declined or revoked.</summary>
    public static readonly Conflict InvitationClosed = new("the invitation is no longer pending", "invitation_closed");

    /// <summary>Another invitation already has the token hash.</summary>
    public static readonly Conflict TokenTaken = new("another invitation has this token", "token_taken");

    /// <summary>The operation belongs to the database's home region (docs/cross-region.md).</summary>
    public static readonly Conflict WrongRegion = new("the database is managed in another region: send it to its home region", "wrong_region");

    /// <summary>A platform database (ts-auth, ts-dashboard) is always placed in every region; its placement cannot change.</summary>
    public static readonly Conflict PlatformPlacementLocked = new("platform databases are placed in every region and their placement cannot change", "platform_placement_locked");

    /// <summary>The organization's live databases reach its plan's database allowance (https://terrascale.tech/pricing.json).</summary>
    public static Conflict DatabaseLimit(string plan, long allowed) => new(
        $"the organization's {plan} plan allows {allowed} database{(allowed == 1 ? "" : "s")}; delete a database or upgrade the plan at https://dash.terrascale.tech/organization?tab=billing",
        DatabaseLimitCode);

    /// <summary>
    /// This region holds none of the organization's database allowance and could not reach every region that may hold some
    /// (docs/terrabase/decisions/0016).
    /// </summary>
    public static readonly Conflict DatabaseAllowanceElsewhere = new(
        "the rest of the organization's database allowance is held in regions this region cannot reach now; retry shortly", DatabaseLimitCode);

    /// <summary>The stable code of <see cref="DatabaseLimit"/>.</summary>
    public const string DatabaseLimitCode = "database_limit";

    /// <summary>The management catalog has a version this build does not know; it is left unchanged.</summary>
    public static readonly Unavailable CatalogUnsupported = new("the management catalog has an old or unsupported format; discard the affected data");

}
