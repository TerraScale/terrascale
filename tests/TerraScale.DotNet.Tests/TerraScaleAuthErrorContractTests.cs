using System.Net;
using System.Text;
using System.Text.Json;
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
                BaseAddress = new Uri("https://auth.customer.example"), InstanceSlug = "acme", ApiKey = "contract-test-key"
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

    private sealed class Handler(string code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { error = new { code } }), Encoding.UTF8, "application/json")
            });
    }
}
