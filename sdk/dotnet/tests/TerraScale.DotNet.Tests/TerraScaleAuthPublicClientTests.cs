using System.Net;
using System.Text;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleAuthPublicClientTests
{
    private const string Organization = "01JA0000000000000000000001";
    private const string Project = "01JA0000000000000000000002";
    private const string Tokens = """{"access_token":"fixture-access","token_type":"Bearer","expires_in":300,"refresh_token":"fixture-next"}""";
    private const string Device = """{"device_code":"fixture-device","user_code":"fixture-user","verification_uri":"https://auth.example.test/ui/terrascale/device","verification_uri_complete":"https://auth.example.test/ui/terrascale/device?user_code=fixture-user","expires_in":600,"interval":5}""";

    [Test]
    [Arguments("start")]
    [Arguments("device")]
    [Arguments("code")]
    [Arguments("refresh")]
    [Arguments("revoke")]
    [Arguments("exchange")]
    public async Task Public_client_forms_omit_secret_and_authentication_headers_and_send_once(string operation)
    {
        Dictionary<string, string>? sent = null;
        using var handler = new Handler(async (request, ct) =>
        {
            await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
            await Assert.That(request.Headers.Contains("Authorization")).IsFalse();
            await Assert.That(request.Content!.Headers.ContentType!.MediaType).IsEqualTo("application/x-www-form-urlencoded");
            await Assert.That(request.RequestUri!.AbsolutePath).IsEqualTo(operation == "start" ? "/terrascale/oauth/device" : operation == "revoke" ? "/terrascale/oauth/revoke" : "/terrascale/oauth/token");
            sent = (await request.Content.ReadAsStringAsync(ct)).Split('&').Select(item => item.Split('=', 2))
                .ToDictionary(pair => Decode(pair[0]), pair => Decode(pair[1]));
            return Json(operation == "start" ? Device : operation == "revoke" ? "" : Tokens);
        });
        using var http = new HttpClient(handler);
        var oidc = Client(http).Oidc;
        const string id = "public +é";
        switch (operation)
        {
            case "start":
                var pending = await oidc.StartDeviceAuthorizationAsync(id, "openid offline_access");
                await Assert.That(pending.ExpiresIn).IsEqualTo(600);
                await Assert.That(pending.Interval).IsEqualTo(5);
                break;
            case "code": await oidc.ExchangePublicAuthorizationCodeAsync(id, "code +&é", "https://app.example/callback?x=1", "verifier+&é"); break;
            case "device": await oidc.ExchangeDeviceCodeAsync(id, "device +&é"); break;
            case "refresh": await oidc.RefreshTokenAsync(id, "refresh +&é"); break;
            case "revoke": await oidc.RevokeTokenAsync(id, "refresh +&é"); break;
            case "exchange": await oidc.ExchangePublicClientTokenAsync(id, "subject +&é", Organization, Project, "documents:read"); break;
        }
        await Assert.That(handler.Calls).IsEqualTo(1);
        await Assert.That(sent!["client_id"]).IsEqualTo(id);
        await Assert.That(sent.ContainsKey("client_secret")).IsFalse();
        var expected = operation switch
        {
            "start" => new Dictionary<string, string> { ["scope"] = "openid offline_access" },
            "code" => new() { ["grant_type"] = "authorization_code", ["code"] = "code +&é", ["redirect_uri"] = "https://app.example/callback?x=1", ["code_verifier"] = "verifier+&é" },
            "device" => new() { ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code", ["device_code"] = "device +&é" },
            "refresh" => new() { ["grant_type"] = "refresh_token", ["refresh_token"] = "refresh +&é" },
            "revoke" => new() { ["token"] = "refresh +&é" },
            _ => new() { ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange", ["subject_token"] = "subject +&é", ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token", ["organization"] = Organization, ["project"] = Project, ["scope"] = "documents:read" }
        };
        await Assert.That(sent.Count).IsEqualTo(expected.Count + 1);
        foreach (var pair in expected) await Assert.That(sent[pair.Key]).IsEqualTo(pair.Value);
    }

    private static TerraScaleAuthClient Client(HttpClient http) => new(http,
        new TerraScaleAuthClientOptions { BaseAddress = new Uri("https://auth.example.test"), InstanceSlug = "terrascale" });
    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return send(request, cancellationToken);
        }
    }
}
