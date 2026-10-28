using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Fast.Platform;

public sealed class ErrorEnvelopeTests
{
    // Test-only routes are mapped inside this process; production builds have no such endpoints.
    private static void FailingRoutes(WebApplication app)
    {
        app.MapGet("/__test/crash", (HttpContext _) => throw new InvalidOperationException("db password=hunter2"));
        app.MapGet("/__test/conflict", (HttpContext _) => throw new ApiException(409, "conflict", "The resource changed."));
        app.MapGet("/__test/unavailable", (HttpContext _) => throw ApiException.Unavailable());
        app.MapGet("/__test/cancelled-work", (HttpContext _) => throw new OperationCanceledException("not the client"));
        // The real SDK against storage that answers 503, as gru's TerraBase did during a writer storm. Loopback, like
        // ts-auth's own storage origin, needs no credential; the handler answers before any connection.
        app.MapGet("/__test/storage-unavailable", (HttpContext _) => StorageAsync(HttpStatusCode.ServiceUnavailable, "capacity"));
        app.MapGet("/__test/storage-conflict", (HttpContext _) => StorageAsync(HttpStatusCode.PreconditionFailed, "precondition_failed"));
        app.MapGet("/ui/__test/storage-unavailable", (HttpContext _) => StorageAsync(HttpStatusCode.ServiceUnavailable, "capacity"));
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
    [Arguments(false)]
    [Arguments(true)]
    public async Task Unhandled_exceptions_return_the_generic_envelope_without_details(bool trusted)
    {
        await using var server = await AuthTestServer.StartAsync(routes: FailingRoutes);
        var id = TerraScale.Authentication.Store.AuthStore.NewId();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/__test/crash");
        request.Headers.Add("X-Request-ID", id);
        request.Headers.Add("X-TerraScale-Request-Proof", TerraScale.Endpoints.RequestIdentity.Sign(id,
            trusted ? TerraBaseTestServer.PeerSecret : new string('0', 64)));
        using var response = await server.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(text);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("internal_error");
        await Assert.That(text).DoesNotContain("hunter2");
        await Assert.That(response.Headers.CacheControl?.NoStore).IsTrue();
        await Assert.That(TerraScale.Endpoints.RequestIdentity.IsValid(response.Headers.GetValues("X-Request-ID").Single())).IsTrue();
        await Assert.That(response.Headers.GetValues("X-Request-ID").Single() == id).IsEqualTo(trusted);
        await Assert.That(response.Headers.Contains("X-TerraScale-Request-Proof")).IsFalse();
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
    public async Task Storage_that_terrabase_reports_unavailable_answers_a_retryable_503()
    {
        await using var server = await AuthTestServer.StartAsync(routes: FailingRoutes);
        using var response = await server.Client.GetAsync(new Uri("/__test/storage-unavailable", UriKind.Relative));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("temporarily_unavailable");
        await Assert.That(response.Headers.CacheControl?.NoStore).IsTrue();
    }

    [Test]
    public async Task A_storage_write_that_lost_a_race_answers_409_conflict_not_500()
    {
        await using var server = await AuthTestServer.StartAsync(routes: FailingRoutes);
        using var response = await server.Client.GetAsync(new Uri("/__test/storage-conflict", UriKind.Relative));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("conflict");
    }

    [Test]
    public async Task A_hosted_page_browser_gets_a_readable_page_when_storage_fails()
    {
        await using var server = await AuthTestServer.StartAsync(routes: FailingRoutes);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/ui/__test/storage-unavailable", UriKind.Relative));
        request.Headers.Add("Accept", "text/html");
        using var response = await server.Client.SendAsync(request);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("text/html");
        await Assert.That(await response.Content.ReadAsStringAsync()).Contains("Something went wrong");
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

    private static async Task<IResult> StorageAsync(HttpStatusCode status, string code)
    {
        using var http = new HttpClient(new StorageAnswers(status, code));
        await new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions { BaseAddress = new Uri("http://127.0.0.1/") })
            .Database(TerraScale.Authentication.Store.AuthStore.NewId(), "auth").ListCollectionsAsync();
        return Results.Ok();
    }

    private sealed class StorageAnswers(HttpStatusCode status, string code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent($$$"""{"error":{"code":"{{{code}}}","message":"synthetic"}}""", System.Text.Encoding.UTF8, "application/json"),
            });
    }
}
