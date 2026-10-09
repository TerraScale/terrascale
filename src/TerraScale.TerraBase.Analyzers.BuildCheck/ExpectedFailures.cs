using RequestError = Microsoft.AspNetCore.Http.BadHttpRequestException;

namespace TerraBase.Analyzers.BuildCheck;

// Compiled, never executed: aliases and target-typed construction must not bypass RS0030.
internal static class ExpectedFailures
{
    internal static object Message() => new RequestError("expected failure"); // RS0030

    internal static object Status() => new RequestError("expected failure", 400); // RS0030

    internal static object Inner() => new RequestError("expected failure", new InvalidOperationException()); // RS0030

    internal static object StatusAndInner() => new RequestError("expected failure", 400, new InvalidOperationException()); // RS0030

    internal static RequestError TargetTyped() => new("expected failure"); // RS0030

    // Catching a framework-created exception remains allowed.
    internal static string Caught(RequestError error) => error.Message;
}
