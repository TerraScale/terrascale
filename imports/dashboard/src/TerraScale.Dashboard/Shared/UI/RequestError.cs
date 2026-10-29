using Microsoft.AspNetCore.WebUtilities;

namespace TerraScale.Dashboard.Shared.UI;

public sealed record RequestError(DocumentModel Document, string Heading, string Detail)
{
    // Native form navigations explicitly accept HTML, including forms posted under /api.
    // API callers need a problem body even when they omit Accept or send */*.
    public static bool WantsJson(HttpContext http) =>
        http.Request.Headers.ContainsKey("Datastar-Request") ||
        http.Request.Headers.Accept.Any(value => value is not null &&
            (value.Contains("json", StringComparison.OrdinalIgnoreCase) || value.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))) ||
        http.Request.Path.StartsWithSegments("/api") &&
        !http.Request.Headers.Accept.Any(value => value is not null && value.Contains("text/html", StringComparison.OrdinalIgnoreCase));

    public static IResult Response(HttpContext http, int status, string? detail = null)
    {
        var heading = status switch
        {
            401 => "Sign in again",
            403 => "This request was not allowed",
            404 => "Page not found",
            500 => "Something went wrong on our side",
            503 => "Temporarily unavailable",
            _ => ReasonPhrases.GetReasonPhrase(status)
        };
        detail ??= status switch
        {
            401 => "Your session may have expired. Sign in to TerraScale and try again.",
            403 => "Your form may have expired, or your account may not have access. Go back to keep your input, then reload and try again. Ask an owner or admin if you still need access.",
            404 => "The requested page or API route could not be found. Check the address and try again.",
            413 => "The payload is too large. Reduce the size and try again.",
            429 => "Too many requests. Wait a moment and try again.",
            500 => "The dashboard could not complete your request. Please try again. If this keeps happening, contact support with the request ID below.",
            503 => "A TerraScale service is temporarily unavailable. Please try again shortly.",
            _ => "The dashboard could not complete this request. Check your input and try again."
        };
        if (WantsJson(http)) return Results.Problem(detail, statusCode: status, title: heading,
            extensions: new Dictionary<string, object?> { ["requestId"] = http.TraceIdentifier });
        var view = Slices.RequestErrorPage.Create(new RequestError(new DocumentModel(heading, DocumentModel.NonceFor(http)), heading, detail));
        view.StatusCode = status;
        return view;
    }
}
