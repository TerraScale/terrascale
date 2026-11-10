using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Email;

/// <summary>
/// Main email API flows over real HTTP and TerraBase: the contract, version history and restore,
/// provider acceptance, and saved branding and copy used by the next sign-in email.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed partial class EmailApiTests(TerraBaseFixture fixture)
{
    private const string Base = $"/v1/{OidcIssuer.Slug}/email";
    private static readonly JsonObject Actor = new() { ["id"] = "user_123", ["email"] = "admin@example.test" };

    [Test]
    public async Task The_next_sign_in_code_email_uses_the_saved_branding_and_copy()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        await oidc.CreateUserAsync();
        using (var branding = await SendAsync(oidc, HttpMethod.Put, Base + "/branding", Body(0, "branding", new JsonObject
        {
            ["brand_name"] = "Acme Cloud",
            ["primary_color"] = "#e11d48",
            ["icon_url"] = "https://cdn.example.test/icon.png",
            ["footer_text"] = "Acme Inc.\nRua Exemplo 100, São Paulo",
            ["support_email"] = "help@acme.test",
            ["reply_to"] = "help@acme.test",
        })))
        {
            await Assert.That(branding.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        using (var template = await SendAsync(oidc, HttpMethod.Put, Base + "/templates/sign_in_code/en", Body(1, "fields", new JsonObject
        {
            ["subject"] = "Your {{ app_name }} code: {{ code }}",
            ["body"] = "Hi {{ user.email }}!\n\nQuestions? {{ support_email }}",
        })))
        {
            await Assert.That(template.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        var (code, message) = await RequestCodeAsync(oidc);
        await Assert.That(message.Subject).IsEqualTo($"Your Acme Cloud code: {code}");
        await Assert.That(message.FromName).IsEqualTo("Acme Cloud");
        await Assert.That(message.ReplyTo).IsEqualTo("help@acme.test");
        await Assert.That(message.Text).Contains("Hi alice@example.test!\n\nQuestions? help@acme.test\n\n    " + code);
        await Assert.That(message.Text).EndsWith("Acme Inc.\nRua Exemplo 100, São Paulo\nNeed help? help@acme.test\nAcme Cloud\n");
        await Assert.That(message.Html).Contains("src=\"https://cdn.example.test/icon.png\"").And.Contains(">Acme Cloud</td>")
            .And.Contains("href=\"mailto:help@acme.test\"").And.DoesNotContain("email-logo.png").And.DoesNotContain("terrascale.tech");
        // The code box takes the accent (the primary colour when no accent is set).
        await Assert.That(message.Html).Contains("background-color:#fdedf0;border:1px solid #f8c6d1;");

        // Rolling back to the defaults sends the TerraScale email again.
        using (var restore = await SendAsync(oidc, HttpMethod.Post, Base + "/versions/0/restore", Body(2)))
        {
            await Assert.That(restore.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        clock.Advance(HostedAuth.MailCooldown);
        var (next, original) = await RequestCodeAsync(oidc);
        await Assert.That(original.Subject).IsEqualTo($"{next} is your TerraScale sign-in code");
        await Assert.That(original.FromName).IsEqualTo("TerraScale");
    }

    private static async Task<(string Code, EmailMessage Message)> RequestCodeAsync(OidcHarness oidc)
    {
        using var response = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-in", new() { ["email"] = "alice@example.test", ["next"] = string.Empty });
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var (_, message) = await oidc.Email.NextAsync();
        return (SixDigits().Match(message.Text).Value, message);
    }

    private static JsonObject Body(long expectedVersion, string? key = null, JsonNode? value = null)
    {
        var body = new JsonObject { ["expected_version"] = expectedVersion, ["actor"] = Actor.DeepClone() };
        if (key is not null)
        {
            body[key] = value;
        }

        return body;
    }

    private static string Basic(string id, string secret) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{id}:{secret}"));

    private static async Task<HttpResponseMessage> SendAsync(OidcHarness oidc, HttpMethod method, string path, JsonNode? body = null, string? credential = "default", HttpClient? client = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (credential is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", credential == "default" ? Basic("01M4EQ2FRJ8S8B29E8TQVWNW4J", OidcHarness.ClientSecret) : credential);
        }

        if (body is not null && method != HttpMethod.Get)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        }

        return await (client ?? oidc.Client).SendAsync(request);
    }

    [GeneratedRegex(@"\b[0-9]{6}\b")]
    private static partial Regex SixDigits();
}
