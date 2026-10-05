using TerraScale.Endpoints;
using TerraScale.TerraBase.Configuration;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management;

/// <summary><c>POST …/projects/{project}/databases</c>: creates an empty database in a project.</summary>
public sealed class CreateManagedDatabaseEndpoint(IManagementService management, TerraBaseOptions options) : Endpoint<NameRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(ManagementRoute.Databases);
        Name("createManagedDatabase");
        Summary("Creates a database in a project");
        Description("name: a lowercase letter, then lowercase letters, digits, _ or -, at most 63 characters; unique within the project. storageName addresses the database in the data API." + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<ManagedDatabaseBody>(StatusCodes.Status201Created);
        Produces<string>(StatusCodes.Status400BadRequest);
        Produces<string>(StatusCodes.Status404NotFound);
        Produces<string>(StatusCodes.Status409Conflict);
        RequireAuthorization(DashboardAuthenticationHandler.PolicyName);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(NameRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await management.CreateDatabaseAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), ManagementRoute.Value(HttpContext, "project"), ManagementRoute.DatabaseName(request), cancellationToken);
        return result.Value is { } value
            ? TypedResults.Created($"{HttpContext.Request.Path}/{value.Id}", ManagedDatabaseBody.From(value, options.Region))
            : ManagementRoute.Error(result.Error);
    }
}
