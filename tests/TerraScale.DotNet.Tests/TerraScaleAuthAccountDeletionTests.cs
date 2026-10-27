using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleAuthAccountDeletionTests
{
    private const string Grant = "fixture-operation-grant-canary";
    [Test]
    [Arguments(false, 204, true)]
    [Arguments(false, 202, false)]
    [Arguments(true, 204, true)]
    [Arguments(true, 202, false)]
    public async Task ReceiptOperationsSendExpectedCredentialsAndReportOutcome(bool statusQuery, int status, bool completed)
    {
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "contracts/auth/routes.json")));
        var operation = manifest.RootElement.GetProperty("operations").EnumerateArray().Single(item =>
            item.GetProperty("operation_id").GetString() == (statusQuery ? "account_deletion_status" : "delete_account"));
        await Assert.That(operation.GetProperty(completed ? "success_status" : "pending_status").GetInt32()).IsEqualTo(status);
        var receipt = Receipt();
        using var handler = new CaptureHandler((HttpStatusCode)status);
        using var http = new HttpClient(handler);
        var platform = Client(http).Platform;
        var result = await Call(platform, statusQuery, receipt);
        await Assert.That(result).IsEqualTo(completed);
        await Assert.That(handler.Calls).IsEqualTo(1);
        await Assert.That(handler.Method).IsEqualTo(statusQuery ? HttpMethod.Get : HttpMethod.Delete);
        await Assert.That(handler.Path).IsEqualTo(statusQuery ? "/v1/terrascale/account/deletion" : "/v1/terrascale/account");
        await Assert.That(handler.Authorization?.ToString()).IsEqualTo(statusQuery ? null : "Bearer fixture-sign-in");
        await Assert.That(handler.Receipt).IsEqualTo(receipt);
        await Assert.That(handler.DeletionAuthorization).IsEqualTo(statusQuery ? null : Grant);
        await Assert.That(handler.HasContent).IsFalse();
        await Assert.That(http.DefaultRequestHeaders.Contains("X-TerraScale-Deletion-Receipt")).IsFalse();
    }

    [Test]
    [Arguments(false, 200)]
    [Arguments(false, 201)]
    [Arguments(false, 401)]
    [Arguments(false, 404)]
    [Arguments(false, 409)]
    [Arguments(false, 503)]
    [Arguments(true, 200)]
    [Arguments(true, 201)]
    [Arguments(true, 401)]
    [Arguments(true, 404)]
    [Arguments(true, 409)]
    [Arguments(true, 503)]
    public async Task OtherResponsesAreErrorsAndNeverRetried(bool statusQuery, int status)
    {
        using var handler = new CaptureHandler((HttpStatusCode)status);
        using var http = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<TerraScaleAuthException>(() => Call(Client(http).Platform, statusQuery, Receipt()));
        await Assert.That(exception!.StatusCode).IsEqualTo((HttpStatusCode)status);
        if (status < 300) await Assert.That(exception.ErrorCode).IsEqualTo("invalid_response");
        if (status == 409) await Assert.That(exception.ErrorCode).IsEqualTo("deletion_refused");
        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InvalidReceiptsFailBeforeDispatchWithoutEchoingTheirValue(bool statusQuery)
    {
        using var handler = new CaptureHandler(HttpStatusCode.NoContent);
        using var http = new HttpClient(handler);
        var platform = Client(http).Platform;
        foreach (var invalid in new[] { "", new string('A', 42), new string('A', 44), new string('A', 42) + "=",
                     "+" + new string('A', 42), "/" + new string('A', 42), "é" + new string('A', 42),
                     "\n" + new string('A', 42), new string('A', 42) + "B" })
        {
            var exception = await Assert.ThrowsAsync<ArgumentException>(() => Call(platform, statusQuery, invalid));
            if (invalid.Length > 40) await Assert.That(exception!.Message.Contains(invalid, StringComparison.Ordinal)).IsFalse();
        }
        await Assert.ThrowsAsync<ArgumentNullException>(() => Call(platform, statusQuery, null!));
        await Assert.That(handler.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task StatusRejectsDefaultAuthorizationRatherThanSendingIt()
    {
        using var handler = new CaptureHandler(HttpStatusCode.NoContent);
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "fixture-default");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(http).Platform.AccountDeletionCompletedAsync(Receipt()));
        await Assert.That(handler.Calls).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CancellationStopsInFlightReceiptRequests(bool statusQuery)
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new CancellationHandler();
        using var http = new HttpClient(handler);
        var pending = Call(Client(http).Platform, statusQuery, Receipt(), cancellation.Token);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TransportErrorsAreNotRetried(bool statusQuery)
    {
        using var handler = new CaptureHandler(HttpStatusCode.NoContent, throwTransportError: true);
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => Call(Client(http).Platform, statusQuery, Receipt()));
        await Assert.That(handler.Calls).IsEqualTo(1);
    }

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

    [Test]
    public async Task ProofDeletionValidatesReceiptWithoutEchoingSecrets()
    {
        using var handler = new CaptureHandler(HttpStatusCode.NoContent);
        using var http = new HttpClient(handler);
        foreach (var receipt in new[] { "", new string('A', 42), new string('A', 44), new string('A', 42) + "B", "fixture-secret\r\nInjected: canary" })
        {
            var error = await Assert.ThrowsAsync<ArgumentException>(() => Client(http).Platform.DeleteAccountWithProofAsync("fixture-sign-in", "fixture-step-up", receipt));
            if (receipt.Length > 0) await Assert.That(error!.ToString()).DoesNotContain(receipt);
        }
        await Assert.That(handler.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task ProofDeletionCanOmitReceipt()
    {
        using var handler = new CaptureHandler(HttpStatusCode.NoContent);
        using var http = new HttpClient(handler);
        var deletion = await Client(http).Platform.DeleteAccountWithProofAsync("fixture-sign-in", "fixture-step-up", cancellationToken: CancellationToken.None);
        await Assert.That(deletion.Completed).IsTrue();
        await Assert.That(deletion.OperationGrant).IsNull();
        await Assert.That(handler.Receipt).IsNull();
        await Assert.That(handler.Authorization!.ToString()).IsEqualTo("Bearer fixture-sign-in");
    }

    [Test]
    [Arguments(204, true)]
    [Arguments(202, false)]
    public async Task OrganizationRemovalRequiresItsExplicitCompletion(int status, bool removed)
    {
        var organizationId = new TerraScaleIdGenerator().NewId();
        var receipt = Receipt();
        using var handler = new CaptureHandler((HttpStatusCode)status);
        using var http = new HttpClient(handler);
        var result = await Client(http).Platform.AccountDeletionOrganizationCompletedAsync(receipt, organizationId);
        await Assert.That(result).IsEqualTo(removed);
        await Assert.That(handler.Path).IsEqualTo("/v1/terrascale/account/deletion?organization=" + organizationId);
        await Assert.That(handler.Method).IsEqualTo(HttpMethod.Get);
        await Assert.That(handler.Receipt).IsEqualTo(receipt);
        await Assert.That(handler.Authorization).IsNull();
        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task NoncandidateOrganizationIsAnErrorAndInvalidIdIsNeverSent()
    {
        using var handler = new CaptureHandler(HttpStatusCode.NotFound);
        using var http = new HttpClient(handler);
        var platform = Client(http).Platform;
        await Assert.ThrowsAsync<ArgumentException>(() => platform.AccountDeletionOrganizationCompletedAsync(Receipt(), "../other"));
        await Assert.That(handler.Calls).IsEqualTo(0);
        var exception = await Assert.ThrowsAsync<TerraScaleAuthException>(() =>
            platform.AccountDeletionOrganizationCompletedAsync(Receipt(), new TerraScaleIdGenerator().NewId()));
        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task ContinuationRejectsMissingOrUnsafeAuthorizationBeforeDispatchWithoutEcho()
    {
        using var handler = new CaptureHandler(HttpStatusCode.NoContent);
        using var http = new HttpClient(handler);
        var platform = Client(http).Platform;
        foreach (var grant in new[] { "", " ", Grant + "\r\nInjected: canary", new string('a', 8193) })
        {
            var error = await Assert.ThrowsAsync<ArgumentException>(() => platform.DeleteAccountAsync("fixture-sign-in", Receipt(), grant));
            if (grant.Length > 1) await Assert.That(error!.ToString()).DoesNotContain(grant);
        }
        await Assert.ThrowsAsync<ArgumentNullException>(() => platform.DeleteAccountAsync("fixture-sign-in", Receipt(), null!));
        await Assert.That(handler.Calls).IsEqualTo(0);
    }

    [Test]
    [Arguments(" ")]
    [Arguments("fixture-first,fixture-second")]
    public async Task DirectDeletionRejectsMalformedGrantWithoutExposingIt(string grant)
    {
        using var handler = new CaptureHandler(HttpStatusCode.Accepted, responseGrant: grant);
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<TerraScaleAuthException>(() => Client(http).Platform.DeleteAccountWithProofAsync("fixture-sign-in", "fixture-step-up", Receipt()));
        await Assert.That(error!.ErrorCode).IsEqualTo("invalid_response");
        if (grant.Length > 1) await Assert.That(error.ToString()).DoesNotContain(grant);
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
