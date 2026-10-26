using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleAuthDeadlineTests
{
    [Test]
    [Arguments(false, "caller")]
    [Arguments(true, "caller")]
    [Arguments(false, "deadline")]
    [Arguments(true, "deadline")]
    [Arguments(false, "both")]
    [Arguments(true, "both")]
    public async Task StalledHeadersAndBodiesPreserveCancellationIdentity(bool body, string outcome)
    {
        using var caller = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Stall(CancellationToken token)
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { if (outcome == "both") caller.Cancel(); throw; }
        }
        using var content = new Content((_, token) => Stall(token));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var handler = new Handler(async token =>
        {
            if (!body) await Stall(token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var pending = Client(http).Platform.ListOrganizationsAsync("fixture-token", cancellationToken: caller.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (outcome == "caller") caller.Cancel();
        if (outcome == "deadline")
        {
            var error = await Assert.ThrowsAsync<TimeoutException>(() => pending.WaitAsync(TimeSpan.FromSeconds(40)));
            await Assert.That(error!.Message).IsEqualTo("ts-auth did not answer within the request deadline.");
            await Assert.That(error.InnerException).IsNull();
        }
        else
        {
            var error = await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(40)));
            await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
        }
        await Assert.That(handler.Calls).IsEqualTo(1);
        if (body) await Assert.That(content.Disposed).IsTrue();
        // The caller still owns a usable HttpClient after a failed auth operation.
        using var next = await http.GetAsync("https://auth.example.test/fixture");
        await Assert.That(next.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CallerCancellationWinsDuringSuccessfulOrMalformedSynchronousParsing(bool malformed)
    {
        using var caller = new CancellationTokenSource();
        using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("7", System.Text.Encoding.UTF8, "application/json")
        }));
        using var http = new HttpClient(handler);
        // A test-only converter cancels at the exact synchronous parse boundary, without a timing race.
        var type = JsonMetadataServices.CreateValueInfo<int>(new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() }, new CancellingConverter(caller, malformed));
        var method = typeof(TerraScaleAuthClient).GetMethod("GetMetadataAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(int));
        var pending = (Task<int>)method.Invoke(Client(http), ["/terrascale/.well-known/openid-configuration", type, caller.Token])!;
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
        await Assert.That(error.ToString()).DoesNotContain("fixture-parse-canary");
        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task CancellationWinsBeforeEmptyOrPendingResultsAreReturned(int operation)
    {
        using var caller = new CancellationTokenSource();
        using var content = new Content((_, _) => Task.CompletedTask, caller.Cancel);
        using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(
            operation == 1 ? HttpStatusCode.Accepted : operation == 0 ? HttpStatusCode.OK : HttpStatusCode.NoContent) { Content = content }));
        using var http = new HttpClient(handler);
        var client = Client(http);
        const string id = "01JA0000000000000000000007";
        Task pending = operation switch
        {
            0 => client.Oidc.RevokeTokenAsync("fixture-client", "fixture-token", caller.Token),
            1 => client.Platform.DeleteProjectAsync("fixture-token", id, id, caller.Token),
            2 => client.Platform.DeleteOrganizationAsync("fixture-token", id, caller.Token),
            _ => client.Platform.AccountDeletionCompletedAsync(new string('A', 43), caller.Token)
        };
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
        await Assert.That(content.Disposed).IsTrue();
        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task UnrelatedTransportCancellationIsNotReportedAsAnSdkTimeout()
    {
        using var transport = new CancellationTokenSource();
        transport.Cancel();
        using var handler = new Handler(_ => Task.FromException<HttpResponseMessage>(new OperationCanceledException(transport.Token)));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => Client(http).Platform.ListOrganizationsAsync("fixture-token"));
        await Assert.That(error!.CancellationToken).IsEqualTo(transport.Token);
        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task HttpClientTimeoutIsDistinctFromCallerCancellation()
    {
        using var handler = new Handler(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(50) };
        var error = await Assert.ThrowsAsync<TimeoutException>(() => Client(http).Platform.ListOrganizationsAsync("fixture-token"));
        await Assert.That(error!.Message).IsEqualTo("ts-auth did not answer within the caller's HttpClient.Timeout.");
        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    private static TerraScaleAuthClient Client(HttpClient http) => new(http, new TerraScaleAuthClientOptions
    {
        BaseAddress = new Uri("https://auth.example.test"), InstanceSlug = "terrascale"
    });

    private sealed class CancellingConverter(CancellationTokenSource caller, bool malformed) : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            caller.Cancel();
            if (malformed) throw new JsonException("fixture-parse-canary");
            return reader.GetInt32();
        }
        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }

    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            ++Calls == 1 ? send(token) : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private sealed class Content(Func<Stream, CancellationToken, Task> write, Action? dispose = null) : HttpContent
    {
        public bool Disposed { get; private set; }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => write(stream, CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) => write(stream, token);
        protected override void Dispose(bool disposing) { Disposed = true; dispose?.Invoke(); base.Dispose(disposing); }
    }
}
