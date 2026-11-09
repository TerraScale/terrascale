namespace TerraBase.Hosting;

internal static class RouteValues
{
    extension(HttpContext context)
    {
        public string RouteValue(string name) => context.Request.RouteValues[name] as string ?? throw new InvalidOperationException($"route value {name} is not bound");
    }
}
