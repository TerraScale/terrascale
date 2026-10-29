using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TerraScale.Authentication.Features.Email;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.SignIn;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Store;
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
    public async Task The_contract_documents_each_operation_with_its_body_and_the_client_credential()
    {
        await using var server = await AuthTestServer.StartAsync();
        var paths = (await server.Client.GetFromJsonAsync<JsonElement>("/openapi/v1.json")).GetProperty("paths");
        var operations = paths.EnumerateObject().Where(path => path.Name.StartsWith("/v1/{slug}/email/", StringComparison.Ordinal))
            .SelectMany(path => path.Value.EnumerateObject().Select(operation => (Path: path.Name, Method: operation.Name, Operation: operation.Value)))
            .ToList();
        await Assert.That(operations.Count).IsEqualTo(13);
        var allowance = operations.Single(operation => operation.Path == "/v1/{slug}/email/allowance");
        await Assert.That(allowance.Method).IsEqualTo("post");
        await Assert.That(allowance.Operation.GetProperty("operationId").GetString()).IsEqualTo("email_allowance_send");
        foreach (var (path, method, operation) in operations)
        {
            var security = operation.GetProperty("security").EnumerateArray().Single().EnumerateObject().Single().Name;
            await Assert.That(security).IsEqualTo("clientBasic").Because($"{method} {path}");
            await Assert.That(operation.GetProperty("responses").TryGetProperty("401", out _)).IsTrue().Because($"{method} {path}");
            if (method is "put" or "post")
            {
                var schema = operation.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString();
                if (path == "/v1/{slug}/email/allowance")
                {
                    await Assert.That(schema).IsEqualTo("#/components/schemas/AllowanceEmailRequest");
                }
                else
                {
                    await Assert.That(schema).Matches("^#/components/schemas/Email[A-Za-z]+Request$").Because($"{method} {path}");
                }
            }
        }

        await Assert.That(paths.GetProperty("/v1/{slug}/email/templates/{kind}/{locale}").GetProperty("get").GetProperty("description").GetString())
            .Contains("lockout").And.Contains("locked_until");
        var test = paths.GetProperty("/v1/{slug}/email/test").GetProperty("post");
        await Assert.That(test.GetProperty("responses").GetProperty("502").GetProperty("content").GetProperty("application/json")
            .GetProperty("schema").GetProperty("$ref").GetString()).IsEqualTo("#/components/schemas/ErrorBody");
    }

    [Test]
    [Arguments(200, "{\"success\":true,\"result\":{\"queued\":[\"admin@example.test\"],\"delivered\":[],\"permanent_bounces\":[],\"suppressed_recipients\":[]}}", 202, null)]
    public async Task Test_sends_report_actual_provider_acceptance(int status, string body, int expected, string? error)
    {
        await using var provider = await EmailProviderFixture.FakeCloudflare.StartAsync((HttpStatusCode)status, body: body);
        using var http = ProviderClient(provider);
        var captured = new CapturingEmailSender();
        await using var oidc = await OidcHarness.StartAsync(fixture, email: captured, services: services =>
            services.AddSingleton<IEmailSender>(new ProductSender(captured, Cloudflare(http))));
        using var response = await SendAsync(oidc, HttpMethod.Post, Base + "/test", new JsonObject
        {
            ["kind"] = "password_reset",
            ["locale"] = "en",
            ["to"] = "admin@example.test",
            ["actor"] = Actor.DeepClone(),
        });
        await Assert.That((int)response.StatusCode).IsEqualTo(expected);
        await Assert.That(provider.Calls).IsEqualTo(1);
        if (error is not null)
        {
            await Assert.That(await ErrorCodeAsync(response)).IsEqualTo(error);
            await Assert.That(response.Headers.RetryAfter is not null).IsEqualTo(expected == 429);
        }
    }

    private static HttpClient ProviderClient(EmailProviderFixture.FakeCloudflare provider) => new(new EmailProviderFixture.ProviderRoute(provider.Address, provider)
    {
        InnerHandler = new HttpClientHandler { AllowAutoRedirect = false },
    });

    private static CloudflareEmailSender Cloudflare(HttpClient http) => new(http,
        new CloudflareEmailOptions("synthetic-account", "synthetic-token"), NullLogger<CloudflareEmailSender>.Instance);

    private sealed class ProductSender(CapturingEmailSender signIn, IEmailSender? provider = null) : IEmailSender
    {
        public Task SendAsync(string recipient, EmailMessage message, CancellationToken cancellationToken) => signIn.SendAsync(recipient, message, cancellationToken);

        public async Task<EmailDeliveryOutcome> DeliverAsync(string recipient, EmailMessage message, CancellationToken cancellationToken)
        {
            if (message.Purpose is EmailPurpose.Authentication or EmailPurpose.Invitation)
            {
                // Authentication mail goes through the outbox's DeliverAsync too; it is captured, never a product outcome.
                await signIn.SendAsync(recipient, message, cancellationToken);
                return EmailDeliveryOutcome.Accepted;
            }

            if (provider is not null)
            {
                return await provider.DeliverAsync(recipient, message, cancellationToken);
            }
            await signIn.SendAsync(recipient, message, cancellationToken);
            return EmailDeliveryOutcome.Accepted;
        }
    }

    [Test]
    public async Task Versions_list_newest_first_and_a_restore_appends_a_new_version()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        foreach (var (version, name) in new[] { (0L, "First"), (1L, "Second") })
        {
            using var saved = await SendAsync(oidc, HttpMethod.Put, Base + "/branding", Body(version, "branding", new JsonObject { ["brand_name"] = name }));
            await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        using (var template = await SendAsync(oidc, HttpMethod.Put, Base + "/templates/new_device/en", Body(2, "fields", new JsonObject { ["footnote"] = "Not you? Write to {{ support_email }}." })))
        {
            await Assert.That(template.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        var list = (await JsonAsync(await SendAsync(oidc, HttpMethod.Get, Base + "/versions?limit=2")))["versions"]!.AsArray();
        await Assert.That(string.Join(',', list.Select(entry => entry!["version"]!.GetValue<long>()))).IsEqualTo("3,2");
        await Assert.That(list[0]!["change"]!.GetValue<string>()).IsEqualTo("template:new_device/en");
        await Assert.That(list[1]!["actor"]!["email"]!.GetValue<string>()).IsEqualTo("admin@example.test");

        var first = await JsonAsync(await SendAsync(oidc, HttpMethod.Get, Base + "/versions/1"));
        await Assert.That(first["branding"]!["brand_name"]!.GetValue<string>()).IsEqualTo("First");
        await Assert.That(first["templates"]!.AsArray().Count).IsEqualTo(0);
        var defaults = await JsonAsync(await SendAsync(oidc, HttpMethod.Get, Base + "/versions/0"));
        await Assert.That(defaults["version"]!.GetValue<long>()).IsEqualTo(0);
        foreach (var query in new[] { "?limit=0&limit=101", "?limit=5&limit=5", "?limit=101", "?limit=x" })
        {
            using var invalid = await SendAsync(oidc, HttpMethod.Get, Base + "/versions" + query);
            await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity).Because(query);
        }

        foreach (var missing in new[] { "/versions/9", "/versions/-1", "/versions/x" })
        {
            using var response = await SendAsync(oidc, HttpMethod.Get, Base + missing);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        using var stale = await SendAsync(oidc, HttpMethod.Post, Base + "/versions/1/restore", Body(2));
        await Assert.That(stale.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        var restored = await JsonAsync(await SendAsync(oidc, HttpMethod.Post, Base + "/versions/1/restore", Body(3)));
        await Assert.That(restored["version"]!.GetValue<long>()).IsEqualTo(4);
        await Assert.That(restored["change"]!.GetValue<string>()).IsEqualTo("rollback:1");
        await Assert.That(restored["branding"]!["brand_name"]!.GetValue<string>()).IsEqualTo("First");
        await Assert.That(restored["templates"]!.AsArray().Count).IsEqualTo(0);
        await Assert.That((await JsonAsync(await SendAsync(oidc, HttpMethod.Get, Base + "/versions/3")))["templates"]!.AsArray().Count).IsEqualTo(1);
    }

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

    private static async Task<JsonNode> JsonAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            await Assert.That(response.IsSuccessStatusCode).IsTrue().Because($"{(int)response.StatusCode}: {text}");
            return JsonNode.Parse(text)!;
        }
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString()!;

    [GeneratedRegex(@"\b[0-9]{6}\b")]
    private static partial Regex SixDigits();
}
