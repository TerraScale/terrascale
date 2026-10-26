using System.Collections.Concurrent;
using System.Net;
using Bogus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TerraScale.Dashboard.E2E.Features.Databases;
using TerraScale.Dashboard.Features.Home;
using TerraScale.Dashboard.Platform.Http;

namespace TerraScale.Dashboard.E2E.Platform;

[ClassDataSource<DashboardRootFixture>]
public sealed class SensitiveLoggingJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RequestsDependenciesAndMcpKeepDiagnosticsWithoutCredentialValues(bool providerError)
    {
        var secret = "sensitive-" + new Faker().Random.AlphaNumeric(32);
        var captured = new Capture();
        var environment = new DemoEnvironment();
        await environment.InitializeAsync();
        FailingStripe stripe = null!;
        await using var app = environment.CreateTestHost(fixture.Root, 0, "http://127.0.0.1", builder =>
        {
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
            builder.Logging.AddFilter(_ => true);
            builder.Logging.AddProvider(captured);
            builder.Services.Replace(ServiceDescriptor.Singleton<IHomeMeasurements>(new FailingMeasurements(secret)));
            var original = (IHttpClientFactory)builder.Services.Last(item => item.ServiceType == typeof(IHttpClientFactory)).ImplementationInstance!;
            stripe = new(original, secret, providerError);
            builder.Services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(stripe));
            builder.Services.AddHttpClient("logging-probe").ConfigurePrimaryHttpMessageHandler(() => new Upstream())
                .AddHttpMessageHandler<ServerTiming.Handler>();
        });
        using var stripeLifetime = stripe;
        app.MapGet("/api/logging/{token}", async (HttpContext http, ILoggerFactory logs, IHttpMessageHandlerFactory handlers) =>
        {
            using var scope = logs.CreateLogger("LoggingProbe").BeginScope(new Dictionary<string, object>
            { ["RequestId"] = secret, ["Authorization"] = secret, ["RequestPath"] = http.Request.Path });
            using var outbound = new HttpClient(handlers.CreateHandler("logging-probe"), false);
            using var response = await outbound.GetAsync("https://probe.invalid/" + secret + "?token=" + secret +
                (http.Request.Query.ContainsKey("fail") ? "&fail=true" : ""));
            return Results.StatusCode((int)response.StatusCode);
        });
        app.MapGet("/api/logging-stream/{token}", async (HttpContext http) =>
        {
            await http.Response.WriteAsync("started", http.RequestAborted);
            throw new InvalidOperationException("https://probe.invalid/?token=" + secret);
        });
        await app.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new() })
        { BaseAddress = new(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)) };
        foreach (var fail in new[] { false, true })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/logging/" + secret + "?token=" + secret + (fail ? "&fail=true" : ""));
            request.Headers.Authorization = new("Bearer", secret);
            request.Headers.Add("Cookie", "test=" + secret);
            request.Headers.Referrer = new("https://probe.invalid/?token=" + secret);
            using var response = await client.SendAsync(request);
            await Assert.That(response.StatusCode).IsEqualTo(fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
            await Assert.That(response.Headers.Contains("Server-Timing")).IsTrue();
        }
        using (var request = new HttpRequestMessage(new HttpMethod(secret), "/api/missing/" + secret))
        using (var response = await client.SendAsync(request))
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        var disconnected = false;
        try { using var response = await client.GetAsync("/api/logging-stream/" + secret); }
        catch (HttpRequestException) { disconnected = true; }
        await Assert.That(disconnected).IsTrue();

        // Test-only identity/Stripe providers: no production users, emails or external network calls.
        var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        var pat = await HttpJourney.CreatePatAsync(client, password: account.Password);
        var result = await HttpJourney.McpAsync(client, pat, "tools/call", new { name = "get_overview", arguments = new { } });
        await Assert.That(result.GetProperty("result").GetProperty("isError").GetBoolean()).IsTrue();
        var csrf = await HttpJourney.CsrfAsync(client, "/organization?tab=billing");
        stripe.Fail = true;
        using (var response = await HttpJourney.PostFormAsync(client, "/billing/checkout", new()
        { ["_csrf"] = csrf, ["plan"] = "pro", ["interval"] = "monthly", ["return"] = "/organization?tab=billing" }))
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await app.StopAsync();

        var records = captured.Records.ToArray();
        var all = string.Join('\n', records.Select(record => record.Text));
        foreach (var value in new[] { secret, pat, csrf }) await Assert.That(all.Contains(value, StringComparison.Ordinal)).IsFalse();
        await Assert.That(records.Any(record => record.Category == "Microsoft.AspNetCore.Hosting.Diagnostics" && record.Event == 2 && record.Text.Contains("StatusCode=200", StringComparison.Ordinal) && record.Text.Contains("ElapsedMilliseconds=", StringComparison.Ordinal))).IsTrue();
        await Assert.That(records.Any(record => record.Category.StartsWith("System.Net.Http.HttpClient.logging-probe", StringComparison.Ordinal))).IsTrue();
        await Assert.That(records.Any(record => record.Category == "ServerTiming" && record.Text.Contains("/api/logging/{token}", StringComparison.Ordinal) && record.Text.Contains("200", StringComparison.Ordinal))).IsTrue();
        await Assert.That(records.Any(record => record.Category == "ServerTiming" && record.Text.Contains("RequestId=", StringComparison.Ordinal))).IsTrue();
        await Assert.That(records.Any(record => record.Category.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal) && record.Level == LogLevel.Error && record.Text.Contains("ErrorType=InvalidOperationException", StringComparison.Ordinal))).IsTrue();
        await Assert.That(records.Any(record => record.Level == LogLevel.Warning && record.Text.Contains("get_overview", StringComparison.Ordinal) && record.Text.Contains("503", StringComparison.Ordinal))).IsTrue();
        await Assert.That(records.Any(record => record.Level == LogLevel.Warning && record.Text.Contains("Checkout for organization", StringComparison.Ordinal) && record.Text.Contains(providerError ? "StripeException" : "HttpRequestException", StringComparison.Ordinal))).IsTrue();
    }

    private sealed class Upstream : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri!.Query.Contains("fail=true", StringComparison.Ordinal)
                ? throw new HttpRequestException(request.RequestUri.AbsoluteUri, null, HttpStatusCode.ServiceUnavailable)
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private sealed class FailingMeasurements(string secret) : IHomeMeasurements
    {
        public Task<HomeMeasurement> ReadAsync(AccountContext context, string range, CancellationToken cancellationToken) =>
            throw new HttpRequestException("https://probe.invalid/?token=" + secret, null, HttpStatusCode.ServiceUnavailable);
    }

    private sealed class FailingStripe(IHttpClientFactory original, string secret, bool providerError) : HttpMessageHandler, IHttpClientFactory
    {
        public bool Fail;
        public HttpClient CreateClient(string name) => new(this, false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Fail && request.RequestUri!.Host == "api.stripe.com")
            {
                if (!providerError) throw new HttpRequestException("https://probe.invalid/?key=" + secret);
                return new(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new
                    { error = new { type = "invalid_request_error", message = "https://probe.invalid/?key=" + secret, code = secret, param = secret } }), System.Text.Encoding.UTF8, "application/json")
                };
            }
            using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers) forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (request.Content is { } content)
            {
                forwarded.Content = new ByteArrayContent(await content.ReadAsByteArrayAsync(cancellationToken));
                foreach (var header in content.Headers) forwarded.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            using var client = original.CreateClient("platform");
            return await client.SendAsync(forwarded, cancellationToken);
        }
    }

    private sealed record Record(string Category, LogLevel Level, int Event, string Text);
    private sealed class Capture : ILoggerProvider, ISupportExternalScope
    {
        public ConcurrentQueue<Record> Records { get; } = new();
        private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();
        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => scopes = scopeProvider;
        public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);
        public void Dispose() { }
        private sealed class Sink(Capture owner, string category) : ILogger
        {
            public bool IsEnabled(LogLevel logLevel) => true;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner.scopes.Push(state);
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var text = formatter(state, exception) + " " + exception + " " + Fields(state);
                owner.scopes.ForEachScope((scope, _) => text += " " + Fields(scope), 0);
                owner.Records.Enqueue(new(category, level, eventId.Id, text));
            }
            private static string Fields(object? state) => state is IEnumerable<KeyValuePair<string, object?>> fields
                ? string.Join(" ", fields.Select(field => field.Key + "=" + field.Value)) : state?.ToString() ?? "";
        }
    }
}
