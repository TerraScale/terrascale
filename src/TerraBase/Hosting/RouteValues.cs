namespace TerraBase.Hosting;

internal static class RouteValues
{
    extension(HttpContext context)
    {
        public string RouteValue(string name) => context.Request.RouteValues[name] as string ?? string.Empty;
    }
}
