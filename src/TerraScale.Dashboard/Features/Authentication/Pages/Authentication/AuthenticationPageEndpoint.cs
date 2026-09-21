using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.Authentication;

public sealed class AuthenticationPageEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/auth/{section}");
        Summary("Renders the ts-auth management console");
        Tags("Authentication", "Pages");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var path = HttpContext.Request.Path.Value?.TrimEnd('/') ?? "/auth";
        var section = HttpContext.Request.RouteValues["section"]?.ToString() ?? "overview";
        if (AuthSection.Find(section) is null)
            return Task.FromResult<IResult>(Results.NotFound());
        HttpContext.Response.Headers.CacheControl = "no-store";
        var query = HttpContext.Request.Query;
        var model = AuthenticationPageModel.From(store, path, section, query["toast"].FirstOrDefault(), query["error"].FirstOrDefault(), query["q"].ToString(), query["id"].ToString(), query["create"] == "1");
        return Task.FromResult<IResult>(AuthenticationSlice.Create(model));
    }
}

public sealed class AuthenticationOverviewEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/auth");
        Summary("Renders the ts-auth management console overview");
        Tags("Authentication", "Pages");
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var query = HttpContext.Request.Query;
        var model = AuthenticationPageModel.From(store, "/auth", "overview", query["toast"].FirstOrDefault(), query["error"].FirstOrDefault(), query["q"].ToString(), query["id"].ToString(), query["create"] == "1");
        return Task.FromResult<IResult>(AuthenticationSlice.Create(model));
    }
}
