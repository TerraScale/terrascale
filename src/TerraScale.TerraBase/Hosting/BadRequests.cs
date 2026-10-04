namespace TerraScale.TerraBase.Hosting;

/// <summary>Answers <see cref="BadHttpRequestException"/> with its status code instead of logging it as a server error.</summary>
public static class BadRequests
{
    /// <summary>Adds the middleware; register it before the endpoints.</summary>
    public static WebApplication UseTerraBaseBadRequests(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context).ConfigureAwait(false);
            }
            catch (BadHttpRequestException error) when (!context.Response.HasStarted)
            {
                // The message describes the rule that failed and never echoes client input.
                context.Response.StatusCode = error.StatusCode;
                await context.Response.WriteAsync(error.Message, context.RequestAborted).ConfigureAwait(false);
            }
        });
        return app;
    }
}
