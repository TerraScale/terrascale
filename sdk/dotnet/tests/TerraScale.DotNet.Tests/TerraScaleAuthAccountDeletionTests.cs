using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleAuthAccountDeletionTests
{
    private const string Grant = "fixture-operation-grant-canary";

    [Test]
    [Arguments(204, true)]
    [Arguments(202, false)]
    public async Task ProofDeletionBindsReceiptThenContinuationAndStatusOmitProof(int status, bool completed)
    {
        var receipt = Receipt();
        using var handler = new CaptureHandler((HttpStatusCode)status, responseGrant: Grant);
        using var http = new HttpClient(handler);
        var platform = Client(http).Platform;
        var deletion = await platform.DeleteAccountWithProofAsync("fixture-sign-in", "fixture-step-up", receipt);
        await Assert.That(deletion.Completed).IsEqualTo(completed);
        await Assert.That(deletion.OperationGrant).IsEqualTo(Grant);
        await Assert.That(deletion.ToString()).DoesNotContain(Grant);
        await Assert.That(handler.DeletionAuthorization).IsNull();
        await Assert.That(handler.Proof).IsEqualTo("fixture-step-up");
        await Assert.That(handler.Receipt).IsEqualTo(receipt);
        await Assert.That(handler.Authorization!.ToString()).IsEqualTo("Bearer fixture-sign-in");
        await Assert.That(handler.HasContent).IsFalse();
        await Assert.That(handler.Calls).IsEqualTo(1);
        await Assert.That(await platform.DeleteAccountAsync("fixture-sign-in", receipt, deletion.OperationGrant!, CancellationToken.None)).IsEqualTo(completed);
        await Assert.That(handler.Proof).IsNull();
        await Assert.That(handler.Receipt).IsEqualTo(receipt);
        await Assert.That(handler.DeletionAuthorization).IsEqualTo(Grant);
        await Assert.That(handler.Authorization!.ToString()).IsEqualTo("Bearer fixture-sign-in");
        await Assert.That(await platform.AccountDeletionCompletedAsync(receipt)).IsEqualTo(completed);
        await Assert.That(handler.Proof).IsNull();
        await Assert.That(handler.Receipt).IsEqualTo(receipt);
        await Assert.That(handler.Authorization).IsNull();
        await Assert.That(handler.DeletionAuthorization).IsNull();
        await Assert.That(handler.Method).IsEqualTo(HttpMethod.Get);
        await Assert.That(handler.Calls).IsEqualTo(3);
        await Assert.That(http.DefaultRequestHeaders.Contains("X-TerraScale-Step-Up-Proof")).IsFalse();
        await Assert.That(http.DefaultRequestHeaders.Contains("X-TerraScale-Deletion-Receipt")).IsFalse();
    }

    [Test]
    [Arguments(0)]
    [Arguments(200)]
    [Arguments(503)]
    [Arguments(403)]
    public async Task AmbiguousFirstDeletionWithProofAndReceiptIsNeverRepeated(int status)
    {
        var receipt = Receipt();
        using var handler = new CaptureHandler((HttpStatusCode)status, throwTransportError: status == 0, responseGrant: Grant);
        using var http = new HttpClient(handler);
        Task Call() => Client(http).Platform.DeleteAccountWithProofAsync("fixture-sign-in", "fixture-step-up", receipt);
        if (status == 0) await Assert.ThrowsAsync<HttpRequestException>(Call);
        else
        {
            var error = await Assert.ThrowsAsync<TerraScaleAuthException>(Call);
            await Assert.That(error!.ToString()).DoesNotContain(Grant);
            await Assert.That(error.InnerException).IsNull();
        }
        await Assert.That(handler.Proof).IsEqualTo("fixture-step-up");
        await Assert.That(handler.Receipt).IsEqualTo(receipt);
        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    private static Task<bool> Call(TerraScaleAuthPlatformClient platform, bool statusQuery, string receipt,
        CancellationToken cancellationToken = default) => statusQuery
        ? platform.AccountDeletionCompletedAsync(receipt, cancellationToken)
        : platform.DeleteAccountAsync("fixture-sign-in", receipt, Grant, cancellationToken);

    private static string Receipt() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static TerraScaleAuthClient Client(HttpClient http) => new(http, new TerraScaleAuthClientOptions
    {
        BaseAddress = new Uri("https://auth.example.test"),
        InstanceSlug = "terrascale",
        ApiKey = "fixture-management-key",
    });

    private sealed class CaptureHandler(HttpStatusCode status, bool throwTransportError = false, string? responseGrant = null) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? Path { get; private set; }
        public string? Receipt { get; private set; }
        public string? Proof { get; private set; }
        public string? DeletionAuthorization { get; private set; }
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public bool HasContent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Method = request.Method;
            Path = request.RequestUri!.PathAndQuery;
            Receipt = request.Headers.TryGetValues("X-TerraScale-Deletion-Receipt", out var receipts) ? receipts.Single() : null;
            Proof = request.Headers.TryGetValues("X-TerraScale-Step-Up-Proof", out var proofs) ? proofs.Single() : null;
            DeletionAuthorization = request.Headers.TryGetValues("X-TerraScale-Deletion-Authorization", out var grants) ? grants.Single() : null;
            Authorization = request.Headers.Authorization;
            HasContent = request.Content is not null;
            if (throwTransportError) throw new HttpRequestException("Synthetic transport failure.");
            var response = new HttpResponseMessage(status)
            {
                Content = status == HttpStatusCode.Conflict ? new StringContent("""{"error":{"code":"deletion_refused"}}""") : null
            };
            if (responseGrant is not null) response.Headers.TryAddWithoutValidation("X-TerraScale-Deletion-Authorization", responseGrant);
            return Task.FromResult(response);
        }
    }

    private sealed class CancellationHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }
}
