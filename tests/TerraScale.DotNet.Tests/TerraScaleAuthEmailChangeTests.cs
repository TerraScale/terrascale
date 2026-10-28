using System.Net;
using System.Text;
using System.Text.Json;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleAuthEmailChangeTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SendsTheTypedContractOnce(bool confirm)
    {
        using var wire = new Wire(confirm ? HttpStatusCode.NoContent : HttpStatusCode.Accepted,
            """{"new_email":"new@example.test","expires_at":"2026-10-28T12:10:00Z"}""");
        using var http = new HttpClient(wire);
        var platform = Client(http).Platform;
        if (confirm) await platform.ConfirmEmailChangeAsync("fixture-bearer", new("123456"));
        else
        {
            var pending = await platform.RequestEmailChangeAsync("fixture-bearer", new("new@example.test"), "fixture-proof");
            await Assert.That(pending.NewEmail).IsEqualTo("new@example.test");
            await Assert.That(pending.ExpiresAt.Offset).IsEqualTo(TimeSpan.Zero);
        }
        await Assert.That(wire.Path).IsEqualTo("/v1/terrascale/account/email-change" + (confirm ? "/confirm" : ""));
        await Assert.That(wire.Body).IsEqualTo(confirm ? "{\"code\":\"123456\"}" : "{\"new_email\":\"new@example.test\"}");
        await Assert.That(wire.Proof).IsEqualTo(confirm ? null : "fixture-proof");
        await Assert.That(wire.Calls).IsEqualTo(1);
        await Assert.That(http.DefaultRequestHeaders.Contains("X-TerraScale-Step-Up-Proof")).IsFalse();
        await Assert.That(new TerraScaleAuthEmailChangeConfirmation("123456").ToString()).DoesNotContain("123456");
    }

    [Test]
    [Arguments(false, 400, "invalid_request")]
    [Arguments(false, 403, "step_up_required")]
    [Arguments(false, 409, "email_taken")]
    [Arguments(false, 429, "rate_limited")]
    [Arguments(true, 403, "email_change_denied")]
    [Arguments(true, 409, "email_taken")]
    [Arguments(true, 503, "server_error")]
    public async Task PreservesSafeErrorsWithoutRetry(bool confirm, int status, string code)
    {
        using var wire = new Wire((HttpStatusCode)status, JsonSerializer.Serialize(new { error = new { code, message = "secret-code-canary" } }));
        using var http = new HttpClient(wire);
        var error = await Assert.ThrowsAsync<TerraScaleAuthException>(() => Call(Client(http), confirm));
        await Assert.That(error!.ErrorCode).IsEqualTo(code);
        await Assert.That(error.ToString()).DoesNotContain("secret-code-canary");
        await Assert.That(wire.Calls).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LostResponsesAreNotReplayed(bool confirm)
    {
        using var wire = new Wire(0, "");
        using var http = new HttpClient(wire);
        await Assert.ThrowsAsync<HttpRequestException>(() => Call(Client(http), confirm));
        await Assert.That(wire.Calls).IsEqualTo(1);
    }

    private static Task Call(TerraScaleAuthClient client, bool confirm) => confirm
        ? client.Platform.ConfirmEmailChangeAsync("fixture-bearer", new("123456"))
        : client.Platform.RequestEmailChangeAsync("fixture-bearer", new("new@example.test"), "fixture-proof");

    private static TerraScaleAuthClient Client(HttpClient http) => new(http, new TerraScaleAuthClientOptions
    { BaseAddress = new Uri("https://auth.example.test"), InstanceSlug = "terrascale" });

    private sealed class Wire(HttpStatusCode status, string response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Path { get; private set; }
        public string? Body { get; private set; }
        public string? Proof { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
            await Assert.That(request.Headers.Authorization?.ToString()).IsEqualTo("Bearer fixture-bearer");
            await Assert.That(request.Content!.Headers.ContentType!.MediaType).IsEqualTo("application/json");
            Path = request.RequestUri!.AbsolutePath;
            Body = await request.Content.ReadAsStringAsync(cancellationToken);
            Proof = request.Headers.TryGetValues("X-TerraScale-Step-Up-Proof", out var values) ? values.Single() : null;
            if (status == 0) throw new HttpRequestException("Synthetic lost response");
            return new(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
