using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Errors;

internal static class ErrorResults
{
    public static IResult ToResult(HttpContext http, DashboardNotFound failure) =>
        RequestError.Response(http, StatusCodes.Status404NotFound, failure.Message);

    public static IResult ToResult(HttpContext http, DashboardForbidden failure) =>
        RequestError.Response(http, StatusCodes.Status403Forbidden, failure.Message);
}
