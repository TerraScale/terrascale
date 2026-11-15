namespace TerraScale.Dashboard.Platform.Datastar
{
    public static class HttpRequestExtensions
    {
        extension(HttpRequest request)
        {
            public bool IsDatastarRequest => request.Headers["Datastar-Request"] == "true";
        }
    }
}
