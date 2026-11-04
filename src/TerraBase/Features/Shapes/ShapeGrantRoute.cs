using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Sql;
using TerraBase.Features.Management;
using TerraBase.Tenancy;

namespace TerraBase.Features.Shapes;

internal sealed record ShapeGrantRequest(string? Where, IReadOnlyList<string>? Columns);

internal sealed record ShapeGrantBody(string Table, string? Where, IReadOnlyList<string>? Columns, DateTimeOffset UpdatedAt)
{
    internal static ShapeGrantBody From(ShapeGrant grant) => new(grant.Table, grant.Where, grant.Columns, grant.UpdatedAt);
}

internal sealed record ShapeGrantsBody(IReadOnlyList<ShapeGrantBody> Grants);

internal static class ShapeGrantRoute
{
    internal const string Grants = ProjectApiRoute.Database + "/read-grants";
    internal const string Grant = ProjectApiRoute.Database + "/tables/{table}/read-grant";
    internal const int MaxColumns = 256;
    internal const string Probe = "00000000000000000000000000";

    internal const string Description =
        "A read grant lets end-user tokens read one table's shape. where is an optional condition every end user's shape must also match; "
        + "it takes the same expressions as the shape where parameter plus :sub, the end user's id from the token. columns optionally limits the columns end users receive. "
        + "API keys and project tokens are not limited by grants. " + ProjectApiRoute.IdentityDescription;

    internal static async ValueTask<object> DatabaseAsync(HttpContext context, IManagementService management, CancellationToken cancellationToken) =>
        await ManagementRoute.DatabaseAsync(context, management, cancellationToken).ConfigureAwait(false) switch
        {
            TenantContext tenant => tenant,
            NotFound error => ManagementRoute.DatabaseNotFound(error),
            Unavailable error => ErrorResponse.From(error),
        };

    internal static object Check(string? table, ShapeGrantRequest request, DateTimeOffset now)
    {
        if (!ShapeTables.IsValidName(table))
        {
            return ShapeRoute.BadTable;
        }

        if (request.Where is { } where && (where.Length is 0 or > ShapeFilters.MaxWhere || ShapeFilters.Bind(where, Probe) is null))
        {
            return ShapeFilters.Refused($"where must be a condition of at most {ShapeFilters.MaxWhere} characters whose only parameter is :sub");
        }

        if (request.Where is { } condition && (!SqlText.TryTokenize(condition, out var tokens) || SqlSandbox.Check(tokens) is not null))
        {
            return ShapeFilters.Refused("where uses a function or name the SQL sandbox denies");
        }

        if (request.Columns is { } columns
            && (columns.Count is 0 or > MaxColumns || columns.Any(column => column is not { Length: > 0 and <= 128 } || column.Contains(',', StringComparison.Ordinal))))
        {
            return new Validation($"columns must name 1 to {MaxColumns} columns without commas");
        }

        return new ShapeGrant(table!, request.Where, request.Columns?.Distinct(StringComparer.Ordinal).ToList(), now);
    }
}
