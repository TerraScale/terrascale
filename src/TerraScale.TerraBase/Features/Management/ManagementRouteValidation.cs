using TerraBase.Errors;

namespace TerraBase.Features.Management;

/// <summary>Validates management route identities after authorization and database-name resolution.</summary>
public static class ManagementRouteValidation
{
    /// <summary>Refuses malformed identities before a management endpoint can read or mutate its resources.</summary>
    public static WebApplication UseTerraBaseManagementRouteValidation(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Use(async (context, next) =>
        {
            if (context.GetEndpoint() is { } endpoint
                && (endpoint.Metadata.GetMetadata<ProjectManagementRoute>() is not null
                    || context.Features.Get<LegacyManagementRoute>() is not null
                    || context.Request.Path.StartsWithSegments(ManagementRoute.Prefix, StringComparison.OrdinalIgnoreCase)))
            {
                await (ManagementRoute.CheckRouteIds(context) switch
                {
                    Done => next(context),
                    Validation error => ErrorResponse.From(error).ExecuteAsync(context),
                }).ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
        });
        return app;
    }
}
