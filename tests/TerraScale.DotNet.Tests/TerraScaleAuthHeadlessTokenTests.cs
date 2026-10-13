using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleAuthHeadlessTokenTests
{
    [Test]
    [Arguments("password")]
    [Arguments("email-code")]
    [Arguments("exchange")]
    [Arguments("client-credentials")]
    [Arguments("refresh")]
    [Arguments("revoke")]
    [Arguments("userinfo")]
    public async Task OAuthFormsAndTokensMatchProducerFixtures(string operation)
    {
        var ids = new TerraScaleIdGenerator(); var organization = ids.NewId(); var project = ids.NewId(); var user = ids.NewId();
        using var fixture = JsonDocument.Parse((await File.ReadAllTextAsync(FixturePath())).Replace("{user}", user, StringComparison.Ordinal));
        var responseName = operation switch { "exchange" => "exchange_response", "client-credentials" => "client_credentials_response", "userinfo" => "userinfo_response", _ => "sign_in_response" };
        using var handler = new Handler(fixture.RootElement.GetProperty(responseName).GetRawText());
        using var http = new HttpClient(handler);
        var client = new TerraScaleAuthClient(http, new TerraScaleAuthClientOptions { BaseAddress = new Uri("https://auth.example.test"), InstanceSlug = "terrascale" });
        TerraScaleAuthTokenResponse? tokens = null;
        switch (operation)
        {
            case "password": tokens = await client.Headless.SignInWithPasswordAsync("id: é+", "secret+= é", "a+b@example.test", "p&=+é", IPAddress.Parse("192.0.2.1"), "openid email offline_access"); break;
            case "email-code": tokens = await client.Headless.SignInWithEmailCodeAsync("id: é+", "secret+= é", "a+b@example.test", "123456", IPAddress.Parse("2001:db8::1"), "pending+&=é", "openid email"); break;
            case "exchange": tokens = await client.Oidc.ExchangeTokenAsync("id: é+", "secret+= é", "subject+&=é", organization, project, "documents:read documents:write"); break;
            case "client-credentials": tokens = await client.Oidc.RequestClientCredentialsTokenAsync("id: é+", "secret+= é", "documents:read", project); break;
            case "refresh": tokens = await client.Oidc.RefreshTokenAsync("id: é+", "secret+= é", "refresh+&=é"); break;
            case "revoke": await client.Oidc.RevokeTokenAsync("id: é+", "secret+= é", "refresh+&=é"); break;
            case "userinfo":
                var info = await client.Oidc.GetUserInfoAsync("fixture-access-token");
                await Assert.That(info.Subject).IsEqualTo(user);
                await Assert.That(info.EmailVerified).IsTrue();
                break;
        }
        await Assert.That(handler.Calls).IsEqualTo(1);
        await Assert.That(handler.Path).IsEqualTo(operation == "revoke" ? "/terrascale/oauth/revoke" : operation == "userinfo" ? "/terrascale/oauth/userinfo" : "/terrascale/oauth/token");
        if (operation == "userinfo")
        {
            await Assert.That(handler.Auth!.ToString()).IsEqualTo("Bearer fixture-access-token");
            await Assert.That(handler.Body).IsNull();
            return;
        }
        await Assert.That(handler.MediaType).IsEqualTo("application/x-www-form-urlencoded");
        var form = DecodeForm(handler.Body!);
        if (operation is "password" or "email-code" or "exchange")
        {
            await Assert.That(handler.Auth!.Scheme).IsEqualTo("Basic");
            var credentials = Encoding.ASCII.GetString(Convert.FromBase64String(handler.Auth.Parameter!)).Split(':');
            await Assert.That(Decode(credentials[0])).IsEqualTo("id: é+");
            await Assert.That(Decode(credentials[1])).IsEqualTo("secret+= é");
            await Assert.That(form.ContainsKey("client_secret")).IsFalse();
            await Assert.That(form.ContainsKey("client_id")).IsFalse();
        }
        else
        {
            await Assert.That(handler.Auth).IsNull();
            await Assert.That(form["client_id"]).IsEqualTo("id: é+");
            await Assert.That(form["client_secret"]).IsEqualTo("secret+= é");
        }
        switch (operation)
        {
            case "password":
                await Assert.That(form["grant_type"]).IsEqualTo("urn:terrascale:params:oauth:grant-type:password");
                await Assert.That(form["password"]).IsEqualTo("p&=+é");
                await Assert.That(form["email"]).IsEqualTo("a+b@example.test");
                await Assert.That(form["ip_address"]).IsEqualTo("192.0.2.1");
                await Assert.That(form["scope"]).IsEqualTo("openid email offline_access");
                await Assert.That(form.Count).IsEqualTo(5); break;
            case "email-code":
                await Assert.That(form["grant_type"]).IsEqualTo("urn:terrascale:params:oauth:grant-type:email-code");
                await Assert.That(form["code"]).IsEqualTo("123456");
                await Assert.That(form["pending_token"]).IsEqualTo("pending+&=é");
                await Assert.That(form["ip_address"]).IsEqualTo("2001:db8::1");
                await Assert.That(form["scope"]).IsEqualTo("openid email");
                await Assert.That(form.Count).IsEqualTo(6); break;
            case "exchange":
                await Assert.That(form["grant_type"]).IsEqualTo("urn:ietf:params:oauth:grant-type:token-exchange");
                await Assert.That(form["subject_token_type"]).IsEqualTo("urn:ietf:params:oauth:token-type:access_token");
                await Assert.That(form["subject_token"]).IsEqualTo("subject+&=é");
                await Assert.That(form["organization"]).IsEqualTo(organization);
                await Assert.That(form["project"]).IsEqualTo(project);
                await Assert.That(form["scope"]).IsEqualTo("documents:read documents:write");
                await Assert.That(form.ContainsKey("database")).IsFalse();
                await Assert.That(form.Count).IsEqualTo(6); break;
            case "client-credentials":
                await Assert.That(form["grant_type"]).IsEqualTo("client_credentials");
                await Assert.That(form["project"]).IsEqualTo(project);
                await Assert.That(form["scope"]).IsEqualTo("documents:read");
                await Assert.That(form.Count).IsEqualTo(5); break;
            case "refresh":
                await Assert.That(form["grant_type"]).IsEqualTo("refresh_token");
                await Assert.That(form["refresh_token"]).IsEqualTo("refresh+&=é");
                await Assert.That(form.Count).IsEqualTo(4); break;
            case "revoke":
                await Assert.That(form["token"]).IsEqualTo("refresh+&=é");
                await Assert.That(form.Count).IsEqualTo(3); break;
        }
        if (tokens is not null)
        {
            var expected = fixture.RootElement.GetProperty(responseName);
            await Assert.That(tokens.AccessToken).IsEqualTo(expected.GetProperty("access_token").GetString());
            await Assert.That(tokens.Scope).IsNull();
            await Assert.That(tokens.ExpiresIn).IsEqualTo(expected.GetProperty("expires_in").GetInt64());
            if (operation == "exchange") await Assert.That(tokens.IssuedTokenType).IsEqualTo("urn:ietf:params:oauth:token-type:access_token");
            await Assert.That(tokens.ToString()).DoesNotContain(tokens.AccessToken);
        }
    }

    private static string FixturePath([CallerFilePath] string source = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../contracts/auth/fixtures/oauth.json"));
    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace("+", " ", StringComparison.Ordinal));
    private static Dictionary<string, string> DecodeForm(string body) => body.Split('&').Select(pair => pair.Split('=', 2)).ToDictionary(pair => Decode(pair[0]), pair => Decode(pair[1]), StringComparer.Ordinal);

    private sealed class Handler(string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Body { get; private set; }
        public string? Path { get; private set; }
        public string? MediaType { get; private set; }
        public AuthenticationHeaderValue? Auth { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Path = request.RequestUri!.AbsolutePath; Auth = request.Headers.Authorization;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            MediaType = request.Content?.Headers.ContentType?.MediaType;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
