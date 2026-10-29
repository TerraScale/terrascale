namespace TerraScale.Dashboard.Platform.Http
{
    public static class HttpRedirects
    {
        internal static IResult SeeOther(string location)
        {
            return new SeeOtherResult(location);
        }

        private sealed record SeeOtherResult(string Location) : IResult
        {
            public Task ExecuteAsync(HttpContext context)
            {
                context.Response.StatusCode = StatusCodes.Status303SeeOther;
                context.Response.Headers.Location = WorkspaceUrl.PreserveRequest(context, Location);
                return Task.CompletedTask;
            }
        }
    }
}
