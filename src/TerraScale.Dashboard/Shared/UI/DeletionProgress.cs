namespace TerraScale.Dashboard.Shared.UI;

public sealed record DeletionProgressPage(DocumentModel Document, string Action, IFormCollection Form, int DelayMilliseconds);

public static class DeletionProgress
{
    public static IResult Create(HttpContext http, IFormCollection form, string? action = null, int delayMilliseconds = 2000)
    {
        http.Response.Headers.CacheControl = "no-store";
        var view = DeletionProgressView.Create(new(new("Deletion in progress", DocumentModel.NonceFor(http)),
            action ?? http.Request.Path + http.Request.QueryString, form, delayMilliseconds));
        view.StatusCode = StatusCodes.Status202Accepted;
        return view;
    }
}
