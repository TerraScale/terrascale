using TerraScale.TerraBase.Errors;

namespace TerraScale.TerraBase.Features.Management;

/// <summary>Management failures shared by several operations, with their stable codes.</summary>
internal static class ManagementErrors
{
    /// <summary>The acting member's role, a viewer's for example, does not allow the change.</summary>
    public static readonly Forbidden RoleTooLow = new("your role in the organization does not allow this", "forbidden_role");
}
