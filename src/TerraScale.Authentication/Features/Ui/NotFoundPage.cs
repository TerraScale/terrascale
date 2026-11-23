using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Net.Http.Headers;
using TerraScale.Authentication.Features.Oidc;

namespace TerraScale.Authentication.Features.Ui;

public static class NotFoundPage
{
    public static async Task HandleAsync(StatusCodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var http = context.HttpContext;
        if (http.Response.StatusCode != StatusCodes.Status404NotFound || !Applies(http.Request))
        {
            return;
        }

        var page = new SignInPageModel(SignInStep.NotFound, OidcIssuer.Slug);
        await SignInPageModel.Render(http, page, StatusCodes.Status404NotFound).ExecuteAsync(http);
    }

    private static bool Applies(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            return false;
        }

        if (!MediaTypeHeaderValue.TryParseList(request.Headers.Accept, out var accept)
            || !accept.Any(type => type.MediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var path = request.Path;
        foreach (var reserved in (ReadOnlySpan<string>)["/v1", "/openapi", "/scalar", "/metrics", "/healthz", "/readyz", "/.well-known"])
        {
            if (path.StartsWithSegments(reserved, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        var segments = (path.Value ?? string.Empty).Split('/', 4, StringSplitOptions.RemoveEmptyEntries);
        return segments.Length < 2 || !(segments[1].Equals("oauth", StringComparison.OrdinalIgnoreCase)
            || segments[1].Equals(".well-known", StringComparison.OrdinalIgnoreCase));
    }
}
