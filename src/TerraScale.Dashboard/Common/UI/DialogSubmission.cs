namespace TerraScale.Dashboard.Common.UI;

public static class DialogSubmission
{
    public static bool Matches(HttpContext http, string action) => HttpMethods.IsPost(http.Request.Method) && http.Request.Path == action && http.Request.HasFormContentType;

    public static string Value(HttpContext http, string action, string field, string current) =>
        IsSecret(field) ? "" : Matches(http, action) ? http.Request.Form[field].ToString() : current;

    private static bool IsSecret(string field) =>
        field.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        field.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        field.Contains("key", StringComparison.OrdinalIgnoreCase) ||
        field.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        field.Contains("header", StringComparison.OrdinalIgnoreCase) ||
        field.Contains("csrf", StringComparison.OrdinalIgnoreCase) ||
        field.Equals("code", StringComparison.OrdinalIgnoreCase);
}
