namespace TerraScale.Dashboard.Common.UI;

public static class DialogSubmission
{
    public static bool Matches(HttpContext http, string action) => HttpMethods.IsPost(http.Request.Method) && http.Request.Path == action && http.Request.HasFormContentType;

    public static string Value(HttpContext http, string action, string field, string current) =>
        Matches(http, action) ? http.Request.Form[field].ToString() : current;
}
