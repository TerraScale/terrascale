using Microsoft.AspNetCore.Authorization;

namespace TerraBase.Hosting;

/// <summary>Default cache policy for protected tenant responses.</summary>
internal static class PrivateResponses
{
    /// <summary>Register before response writers; endpoint metadata is read when headers are committed.</summary>
    public static WebApplication UseTerraBasePrivateResponses(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Use((context, next) =>
        {
            context.Response.OnStarting(static state =>
            {
                var current = (HttpContext)state;
                var metadata = current.GetEndpoint()?.Metadata;
                if (metadata?.GetMetadata<IAuthorizeData>() is not null
                    && metadata.GetMetadata<IAllowAnonymous>() is null
                    && current.Response.Headers.CacheControl.Count == 0)
                {
                    // Keep explicit policies (including shape private/no-store and SSE) intact.
                    current.Response.Headers.CacheControl = "no-store";
                }

                return Task.CompletedTask;
            }, context);
            return next(context);
        });
        return app;
    }
}
