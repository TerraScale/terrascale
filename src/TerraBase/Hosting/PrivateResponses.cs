using Microsoft.AspNetCore.Authorization;

namespace TerraBase.Hosting;

internal static class PrivateResponses
{
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
