using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Auth.Contracts;
using TerraScale.Auth.Tests;

namespace TerraScale.Auth.Fast.Platform;

public sealed class ErrorEnvelopeTests
{
    // Test-only routes are mapped inside this process; production builds have no such endpoints.
    private static void FailingRoutes(WebApplication app)
    {
        app.MapGet("/__test/crash", (HttpContext _) => throw new InvalidOperationException("db password=hunter2"));
        app.MapGet("/__test/conflict", (HttpContext _) => throw new ApiException(409, "conflict", "The resource changed."));
        app.MapGet("/__test/unavailable", (HttpContext _) => throw ApiException.Unavailable());
        app.MapGet("/__test/cancelled-work", (HttpContext _) => throw new OperationCanceledException("not the client"));
        app.MapGet("/__test/slow", async (HttpContext context) =>
        {
            var signals = context.RequestServices.GetRequiredService<DisconnectSignals>();
            // OnCompleted runs once the whole pipeline, ErrorEnvelopeMiddleware included, is done.
            context.Response.OnCompleted(() =>
            {
                signals.Finished.TrySetResult();
                return Task.CompletedTask;
            });
            signals.Entered.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted);
            return Results.Ok();
        });
    }

    [Test]
    public async Task Unhandled_exceptions_return_the_generic_envelope_without_details()
    {
        await using var server = await AuthTestServer.StartAsync(routes: FailingRoutes);

        using var response = await server.Client.GetAsync("/__test/crash");
        var text = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(text);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("internal_error");
        await Assert.That(text).DoesNotContain("hunter2");
        await Assert.That(response.Headers.CacheControl?.NoStore).IsTrue();
    }

    [Test]
    public async Task Api_errors_keep_their_status_and_code()
    {
        await using var server = await AuthTestServer.StartAsync(routes: FailingRoutes);

        using var conflict = await server.Client.GetAsync("/__test/conflict");
        var conflictBody = await conflict.Content.ReadFromJsonAsync<JsonElement>();
        using var unavailable = await server.Client.GetAsync("/__test/unavailable");
        var unavailableBody = await unavailable.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(conflict.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(conflictBody.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("conflict");
        await Assert.That(conflictBody.GetProperty("error").GetProperty("message").GetString()).IsEqualTo("The resource changed.");
        await Assert.That(unavailable.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(unavailableBody.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("temporarily_unavailable");
    }

    [Test]
    public async Task A_cancellation_that_is_not_the_client_leaving_is_a_server_error()
    {
        await using var server = await AuthTestServer.StartAsync(routes: FailingRoutes);

        using var response = await server.Client.GetAsync("/__test/cancelled-work");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("internal_error");
    }

    [Test]
    public async Task A_client_that_disconnects_gets_no_envelope_and_no_error_report()
    {
        var logs = new CapturingLogs();
        var signals = new DisconnectSignals();
        await using var server = await AuthTestServer.StartAsync(
            services: services => services
                .AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(logs)
                .AddSingleton(signals),
            routes: FailingRoutes);

        using var leaving = new CancellationTokenSource();
        var request = server.Client.GetAsync("/__test/slow", leaving.Token);
        // Cancel only once the server is inside the handler, then wait until it has unwound.
        await signals.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await leaving.CancelAsync();
        await Assert.ThrowsAsync<TaskCanceledException>(() => request);
        await signals.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(logs.Text).DoesNotContain("Unhandled request exception");
        await Assert.That(logs.Text).DoesNotContain("Request failed");
    }

    /// <summary>Lets the disconnect test synchronize with the server-side handler.</summary>
    public sealed class DisconnectSignals
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
