using System.Net;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet.Tests.TerraBase;
using TUnit.Core;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleAuthErrorContractTests
{
    [Test]
    public async Task SourcePinnedAuthAndOAuthCodesRemainAdmittedByPublicClient()
    {
        using var resource = typeof(TerraScaleAuthClient).Assembly.GetManifestResourceStream("TerraScale.DotNet.Auth.error-codes.json")!;
        using var vocabulary = await JsonDocument.ParseAsync(resource);
        foreach (var item in vocabulary.RootElement.GetProperty("codes").EnumerateArray())
        {
            var code = item.GetString()!;
            using var http = new HttpClient(new Handler(code));
            var client = new TerraScaleAuthClient(http, new TerraScaleAuthClientOptions
            {
                BaseAddress = new Uri("https://auth.customer.example"),
                InstanceSlug = "acme",
                ApiKey = "contract-test-key"
            });
            try
            {
                await client.Users.ListAsync();
                throw new InvalidOperationException("Expected the error contract response.");
            }
            catch (TerraScaleAuthException error)
            {
                await Assert.That(error.ErrorCode).IsEqualTo(code);
            }
        }
    }

    [Test]
    [Arguments(403, "mfa_required", "mfa_required")]
    [Arguments(409, "mfa_not_enrolled", "mfa_not_enrolled")]
    [Arguments(403, "MFA_REQUIRED", null)]
    [Arguments(409, "mfa_not_enrolled synthetic-private-detail", null)]
    public async Task MfaRefusalResponsesRetainOnlyExactSafeCodesAndAreNotRetried(int status, string code, string? expectedCode)
    {
        const string privateDetail = "synthetic-private-detail";
        const string requestId = "01JA0000000000000000000001";
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { error = new { code, message = privateDetail }, extra = privateDetail }), Encoding.UTF8, "application/json")
            };
            response.Headers.TryAddWithoutValidation("X-Request-ID", requestId);
            response.Headers.RetryAfter = new(TimeSpan.Zero);
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var client = new TerraScaleAuthClient(http, new TerraScaleAuthClientOptions
        {
            BaseAddress = new Uri("https://auth.customer.example"),
            InstanceSlug = "acme",
            ApiKey = "contract-test-key"
        });
        // Exercise shared error mapping through a read with the default retry policy; no MFA route is added.
        var error = await Assert.ThrowsAsync<TerraScaleAuthException>(() => client.Users.ListAsync());
        await Assert.That(error!.StatusCode).IsEqualTo((HttpStatusCode)status);
        await Assert.That(error.ErrorCode).IsEqualTo(expectedCode);
        await Assert.That(error.Message).IsEqualTo($"ts-auth returned HTTP {status}.");
        await Assert.That(error.RequestId).IsEqualTo(requestId);
        await Assert.That(error.RetryAfter).IsEqualTo(TimeSpan.Zero);
        await Assert.That(error.ToString()).DoesNotContain(privateDetail);
        await Assert.That(error.InnerException).IsNull();
        await Assert.That(calls).IsEqualTo(1);
    }

    private sealed class Handler(string code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { error = new { code } }), Encoding.UTF8, "application/json")
            });
    }
}
