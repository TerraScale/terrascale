using Microsoft.AspNetCore.Http;
using TerraScale.Dashboard;
using TerraScale.Dashboard.Infrastructure.Configuration;

namespace TerraScale.Dashboard.FastTests;

public sealed class NavigationPolicyTests
{
    [Test]
    public async Task LiveSpeculationNeverActivatesTheAccount()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/";
        context.Request.Headers["Sec-Purpose"] = "prefetch";
        var invoked = false;
        var middleware = new PagePerformanceMiddleware(_ =>
        {
            invoked = true;
            return Task.CompletedTask;
        }, new DashboardSecurityOptions(DemoMode: false));
        await middleware.InvokeAsync(context);
        await Assert.That(invoked).IsFalse();
        await Assert.That(context.Response.StatusCode).IsEqualTo(204);
    }

    [Test]
    [Arguments("/data?view=sql", "GET", "prefetch")]
    [Arguments("/checkout/session", "GET", "prefetch")]
    [Arguments("/api-keys", "GET", "prefetch")]
    [Arguments("/", "POST", "prefetch")]
    [Arguments("/", "GET", "prefetch;prerender")]
    public async Task UnsafeSpeculationDoesNotReachTheEndpoint(string url, string method, string purpose)
    {
        var context = new DefaultHttpContext();
        var uri = new Uri("http://localhost" + url);
        context.Request.Method = method;
        context.Request.Path = uri.AbsolutePath;
        context.Request.QueryString = new QueryString(uri.Query);
        context.Request.Headers["Sec-Purpose"] = purpose;
        var invoked = false;
        var middleware = new PagePerformanceMiddleware(_ =>
        {
            invoked = true;
            return Task.CompletedTask;
        }, new DashboardSecurityOptions(DemoMode: true));
        await middleware.InvokeAsync(context);
        await Assert.That(invoked).IsFalse();
        await Assert.That(context.Response.StatusCode).IsEqualTo(204);
    }
}
