using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.RateLimiting;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed class ImportProjectUsersEndpoint(TenantDirectory tenants, PlatformAuthentication authentication, UserProfiles profiles, AttemptLimiter limiter)
    : ProjectTenantEndpoint(tenants, authentication)
{
    protected override int MaximumBodyBytes => 16 * 1024 * 1024;

    public override void Configure()
    {
        Post(Root + "/users/import");
        this.Describe("import_project_users", "Import end users", "Requires auth:write or the admin project role. Up to 500 rows; invalid rows return indexed errors without rejecting valid rows. Ten calls per project per minute. No email is sent. Accepts bcrypt, argon2id PHC and pbkdf2_sha256$iterations$base64-salt$base64-hash.",
            "Project tenants", typeof(ImportProjectUsersRequest), StatusCodes.Status200OK, typeof(ImportProjectUsersBody));
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
    }

    protected override async Task BeforeWriteAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var project = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        if (await limiter.AcquireAsync(project, "user-import", project, 10, TimeSpan.FromMinutes(1), cancellationToken) is { } wait)
        {
            throw ApiException.RateLimited("Too many user imports.", wait);
        }
    }

    protected override async Task<IResult> HandleAsync(ProjectCaller caller, CancellationToken cancellationToken)
    {
        var project = await this.AccessAsync(caller, Roles.Admin, cancellationToken);
        var request = await this.BodyAsync(UserProfileJson.Default.ImportProjectUsersRequest, cancellationToken);
        if (request.Users is not { Count: > 0 and <= 500 })
        {
            throw Invalid("users must contain 1 to 500 rows.");
        }

        List<ImportedProjectUser> items = [];
        List<UserImportError> errors = [];
        for (var index = 0; index < request.Users.Count; index++)
        {
            if (request.Users[index] is not { } row)
            {
                errors.Add(new(index, "invalid_request", "Each row must be an object."));
                continue;
            }

            try
            {
                items.Add(new(index, Map(await profiles.ImportAsync(project, row, cancellationToken))));
            }
            catch (ApiException error) when (error.Status is StatusCodes.Status400BadRequest or StatusCodes.Status409Conflict)
            {
                errors.Add(new(index, error.Status == StatusCodes.Status409Conflict ? "user_conflict" : "invalid_request", error.Message));
            }
        }

        return Results.Json(new ImportProjectUsersBody(items, errors), UserProfileJson.Default.ImportProjectUsersBody);
    }
}
