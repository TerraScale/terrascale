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

namespace TerraScale.Authentication.Slow.Email;

/// <summary>
/// The <c>/v1/{slug}/email/*</c> API over real HTTP on a real TerraBase: client authentication, branding and copy saved
/// as versions with conflicts and rollback, the placeholder sandbox and HTML encoding, previews matched against
/// committed snapshots, test sends, and the next sign-in code email using what was saved.
/// Set <c>TS_AUTH_UPDATE_SNAPSHOTS=1</c> to rewrite the snapshots after an intended change, then review the diff.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed partial class EmailApiTests(TerraBaseFixture fixture)
{
    [ClassDataSource<BrowserFixture>(Shared = SharedType.PerTestSession)]
    public required BrowserFixture Chromium { get; init; }

    private const string Base = $"/v1/{OidcIssuer.Slug}/email";
    private static readonly JsonObject Actor = new() { ["id"] = "user_123", ["email"] = "admin@example.test" };

    [Test]
    public async Task Every_endpoint_answers_only_the_dashboard_client()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture, values => values["service-clients"] = $"01M4EQ2FRJ8S8B29E8TQVWNW4M:{OidcHarness.ClientSecret}");
        (HttpMethod Method, string Path)[] routes =
        [
            (HttpMethod.Get, "/branding"), (HttpMethod.Put, "/branding"), (HttpMethod.Get, "/templates"),
            (HttpMethod.Get, "/templates/sign_in_code/en"), (HttpMethod.Put, "/templates/sign_in_code/en"), (HttpMethod.Post, "/preview"),
            (HttpMethod.Post, "/test"), (HttpMethod.Get, "/versions"), (HttpMethod.Get, "/versions/0"), (HttpMethod.Post, "/versions/0/restore"),
        ];
        string?[] credentials = [null, Basic("01M4EQ2FRJ8S8B29E8TQVWNW4J", "wrong-secret-0123456789abcdef0123456789"), Basic("01M4EQ2FRJ8S8B29E8TQVWNW4M", OidcHarness.ClientSecret), "Bearer " + OidcHarness.ClientSecret, "Basic !!!"];
        foreach (var (method, path) in routes)
        {
            foreach (var credential in credentials)
            {
                using var refused = await SendAsync(oidc, method, Base + path, new JsonObject(), credential);
                await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized).Because($"{method} {path} with {credential}");
                await Assert.That(refused.Headers.WwwAuthenticate.ToString()).StartsWith("Basic realm=\"ts-auth\"");
                await Assert.That(await ErrorCodeAsync(refused)).IsEqualTo("invalid_client");
            }

            using var otherInstance = await SendAsync(oidc, method, $"/v1/acme/email{path}", new JsonObject());
            await Assert.That(otherInstance.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        // RFC 6749 section 2.3.1: the id and secret are form-urlencoded inside the Basic credential.
        using var encoded = await SendAsync(oidc, HttpMethod.Get, Base + "/branding", null, Basic("%30" + TerraScale.Authentication.Features.Oidc.OidcIssuer.DashboardClientId[1..], Uri.EscapeDataString(OidcHarness.ClientSecret)));
        await Assert.That(encoded.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(encoded.Headers.CacheControl?.NoStore).IsTrue();
    }

    [Test]
    public async Task Branding_is_validated_saved_as_versions_and_refuses_a_stale_version()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        var initial = await JsonAsync(await SendAsync(oidc, HttpMethod.Get, Base + "/branding"));
        await Assert.That(initial["version"]!.GetValue<long>()).IsEqualTo(0);
        await Assert.That(initial["branding"]!["brand_name"]).IsNull();
        await Assert.That(initial["defaults"]!["brand_name"]!.GetValue<string>()).IsEqualTo("TerraScale");
        await Assert.That(initial["defaults"]!["primary_color"]!.GetValue<string>()).IsEqualTo("#2158e8");

        foreach (var (branding, field) in new (JsonObject, string)[]
        {
            (new() { ["primary_color"] = "blue" }, "branding.primary_color"),
            (new() { ["logo_url"] = "http://cdn.example.test/logo.png", ["logo_width"] = 120, ["logo_height"] = 32 }, "branding.logo_url"),
            (new() { ["logo_url"] = "https://user:pass@cdn.example.test/logo.png", ["logo_width"] = 120, ["logo_height"] = 32 }, "branding.logo_url"),
            (new() { ["logo_url"] = "https://cdn.example.test/logo.png" }, "logo_width"),
            (new() { ["theme"] = "sepia" }, "branding.theme"),
            (new() { ["support_email"] = "Support <help@example.test>" }, "branding.support_email"),
            (new() { ["sender_name"] = "Bank <security@bank.example>" }, "branding.sender_name"),
            (new() { ["brand_name"] = new string('x', 81) }, "branding.brand_name"),
            (new() { ["footer_text"] = "line\u0007bell" }, "branding.footer_text"),
        })
        {
            using var invalid = await SendAsync(oidc, HttpMethod.Put, Base + "/branding", Body(0, "branding", branding));
            await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity).Because(branding.ToJsonString());
            await Assert.That(await MessageAsync(invalid)).Contains(field);
        }

        using var unknownField = await SendAsync(oidc, HttpMethod.Put, Base + "/branding", Body(0, "branding", new JsonObject { ["primaryColor"] = "#ff0000" }));
        await Assert.That(unknownField.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var noActor = await SendAsync(oidc, HttpMethod.Put, Base + "/branding", new JsonObject { ["expected_version"] = 0, ["branding"] = new JsonObject() });
        await Assert.That(noActor.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);

        var saved = await JsonAsync(await SendAsync(oidc, HttpMethod.Put, Base + "/branding", Body(0, "branding", new JsonObject
        {
            ["brand_name"] = "Acme Cloud",
            ["primary_color"] = "#E11D48",
            ["logo_url"] = "https://cdn.example.test/logo.png",
            ["logo_width"] = 120,
            ["logo_height"] = 32,
        })));
        await Assert.That(saved["version"]!.GetValue<long>()).IsEqualTo(1);
        await Assert.That(saved["branding"]!["primary_color"]!.GetValue<string>()).IsEqualTo("#e11d48");
        await Assert.That(saved["updated_by"]!["id"]!.GetValue<string>()).IsEqualTo("user_123");

        using var stale = await SendAsync(oidc, HttpMethod.Put, Base + "/branding", Body(0, "branding", new JsonObject { ["brand_name"] = "Lost update" }));
        await Assert.That(stale.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(await ErrorCodeAsync(stale)).IsEqualTo("version_conflict");
        var current = await JsonAsync(await SendAsync(oidc, HttpMethod.Get, Base + "/branding"));
        await Assert.That(current["branding"]!["brand_name"]!.GetValue<string>()).IsEqualTo("Acme Cloud");
    }

    [Test]
    public async Task Concurrent_saves_from_one_version_have_exactly_one_winner()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await using var replica = await oidc.StartReplicaAsync();
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(index => SendAsync(oidc, HttpMethod.Put, Base + "/branding",
            Body(0, "branding", new JsonObject { ["brand_name"] = $"Writer {index}" }), client: index % 2 == 0 ? oidc.Client : replica.Client)));
        try
        {
            await Assert.That(responses.Count(response => response.StatusCode == HttpStatusCode.OK)).IsEqualTo(1);
            await Assert.That(responses.Count(response => response.StatusCode == HttpStatusCode.Conflict)).IsEqualTo(5);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        var versions = await JsonAsync(await SendAsync(oidc, HttpMethod.Get, Base + "/versions"));
        await Assert.That(versions["versions"]!.AsArray().Count).IsEqualTo(1);
    }

    [Test]
    public async Task Copy_takes_only_the_kinds_placeholders_and_resets_to_the_default()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        const string Template = Base + "/templates/sign_in_code/pt-BR";
        foreach (var (fields, message) in new (JsonObject, string)[]
        {
            (new() { ["subject"] = "Abra {{ link }}" }, "{{ link }}"),
            (new() { ["subject"] = "Until {{ locked_until }}" }, "{{ locked_until }}"),
            (new() { ["subject"] = "{% if code %}{{ code }}{% endif %}" }, "placeholders"),
            (new() { ["subject"] = "{{ code | upcase }}" }, "placeholders"),
            (new() { ["subject"] = "{{ user.password }}" }, "{{ user.password }}"),
            (new() { ["body"] = new string('x', 2001) }, "fields.body"),
            (new() { ["subject"] = "two\nlines" }, "fields.subject"),
            (new() { ["button"] = "Entrar" }, "fields.button"),
        })
        {
            using var invalid = await SendAsync(oidc, HttpMethod.Put, Template, Body(0, "fields", fields));
            await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity).Because(fields.ToJsonString());
            await Assert.That(await MessageAsync(invalid)).Contains(message);
        }

        foreach (var path in new[] { Base + "/templates/welcome/en", Base + "/templates/sign_in_code/fr" })
        {
            using var missing = await SendAsync(oidc, HttpMethod.Get, path);
            await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }

        var saved = await JsonAsync(await SendAsync(oidc, HttpMethod.Put, Template, Body(0, "fields", new JsonObject
        {
            ["subject"] = "  Código {{code}} para {{ app_name }}  ",
            ["body"] = "Olá!\r\n\r\nSeu código chegou.",
            ["heading"] = "   ",
        })));
        await Assert.That(saved["overridden"]!.GetValue<bool>()).IsTrue();
        await Assert.That(saved["fields"]!["subject"]!.GetValue<string>()).IsEqualTo("Código {{code}} para {{ app_name }}");
        await Assert.That(saved["fields"]!["body"]!.GetValue<string>()).IsEqualTo("Olá!\n\nSeu código chegou.");
        await Assert.That(saved["fields"]!["heading"]).IsNull();
        await Assert.That(saved["defaults"]!["heading"]!.GetValue<string>()).IsEqualTo("Seu código de acesso");
        await Assert.That(string.Join(',', saved["variables"]!.AsArray().Select(node => node!.GetValue<string>()))).IsEqualTo("app_name,user.email,support_email,code,expires_in");

        var list = await JsonAsync(await SendAsync(oidc, HttpMethod.Get, Base + "/templates"));
        var templates = list["templates"]!.AsArray();
        await Assert.That(templates.Count).IsEqualTo(20);
        await Assert.That(templates.Single(entry => entry!["overridden"]!.GetValue<bool>())!["locale"]!.GetValue<string>()).IsEqualTo("pt-BR");

        var reset = await JsonAsync(await SendAsync(oidc, HttpMethod.Put, Template, Body(1, "fields", new JsonObject { ["subject"] = null })));
        await Assert.That(reset["version"]!.GetValue<long>()).IsEqualTo(2);
        await Assert.That(reset["overridden"]!.GetValue<bool>()).IsFalse();
    }

    [Test]
    public async Task Previews_encode_every_value_and_match_the_snapshots()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        var hostile = await JsonAsync(await SendAsync(oidc, HttpMethod.Post, Base + "/preview", new JsonObject
        {
            ["kind"] = "magic_link",
            ["locale"] = "en",
            ["branding"] = new JsonObject { ["brand_name"] = "<script>alert(1)</script>", ["footer_text"] = "\"><img src=x onerror=alert(2)>" },
            ["fields"] = new JsonObject { ["heading"] = "<b onclick=alert(3)>Hi</b> {{ app_name }}", ["button"] = "</a><a href=\"javascript:alert(4)\">" },
        }));
        var html = hostile["html"]!.GetValue<string>();
        foreach (var raw in new[] { "<script>", "<img src=x", "<b onclick", "<a href=\"javascript" })
        {
            await Assert.That(html).DoesNotContain(raw);
        }

        await Assert.That(html).Contains("&lt;b onclick=alert(3)&gt;Hi&lt;/b&gt; &lt;script&gt;alert(1)&lt;/script&gt;");
        await Assert.That(hostile["subject"]!.GetValue<string>()).IsEqualTo("Sign in to <script>alert(1)</script>");

        foreach (var kind in AuthEmails.Kinds.Keys)
        {
            foreach (var locale in AuthEmails.Locales)
            {
                var preview = await JsonAsync(await SendAsync(oidc, HttpMethod.Post, Base + "/preview", new JsonObject { ["kind"] = kind, ["locale"] = locale }));
                var text = $"Subject: {preview["subject"]!.GetValue<string>()}\n\n{preview["text"]!.GetValue<string>()}";
                await MatchesSnapshotAsync($"{kind}.{locale}.txt", text);
                if (locale == AuthEmails.English && kind is "magic_link" or "new_device" or "lockout")
                {
                    await MatchesSnapshotAsync($"{kind}.{locale}.html", preview["html"]!.GetValue<string>());
                }
            }
        }
    }

    [Test]
    [Arguments("en", "Temporary authentication pause for Acme Cloud")]
    [Arguments("pt-BR", "Pausa temporária de autenticação em Acme Cloud")]
    public async Task Lockout_copy_uses_the_UTC_deadline_and_saved_branding_without_a_code_link_or_expiry(string locale, string subject)
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 1, 1, 21, 45, 5, TimeSpan.FromHours(-3)));
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        const string Deadline = "2026-01-02 01:00:05 UTC";
        var template = Base + $"/templates/lockout/{locale}";
        var defaults = await JsonAsync(await SendAsync(oidc, HttpMethod.Get, template));
        await Assert.That(string.Join(',', defaults["variables"]!.AsArray().Select(node => node!.GetValue<string>())))
            .IsEqualTo("app_name,user.email,support_email,locked_until");
        await Assert.That(defaults["defaults"]!["button"]).IsNull();

        foreach (var fields in new JsonObject[]
        {
            new() { ["body"] = "{{ code }}" }, new() { ["body"] = "{{ link }}" }, new() { ["body"] = "{{ expires_in }}" },
            new() { ["body"] = "{{ time }}" }, new() { ["button"] = "Continue" },
        })
        {
            using var invalid = await SendAsync(oidc, HttpMethod.Put, template, Body(0, "fields", fields));
            await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity).Because(fields.ToJsonString());
        }

        using var branding = await SendAsync(oidc, HttpMethod.Put, Base + "/branding", Body(0, "branding", new JsonObject
        {
            ["brand_name"] = "Acme Cloud",
            ["support_email"] = "security@acme.test",
        }));
        await Assert.That(branding.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var saved = await SendAsync(oidc, HttpMethod.Put, template, Body(1, "fields", new JsonObject
        {
            ["footnote"] = "{{ app_name }}: {{ locked_until }}. {{ support_email }}",
        }));
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var preview = await JsonAsync(await SendAsync(oidc, HttpMethod.Post, Base + "/preview", new JsonObject { ["kind"] = "lockout", ["locale"] = locale }));
        await Assert.That(preview["subject"]!.GetValue<string>()).IsEqualTo(subject);
        var text = preview["text"]!.GetValue<string>();
        var html = preview["html"]!.GetValue<string>();
        await Assert.That(text).Contains($"Acme Cloud: {Deadline}. security@acme.test").And.Contains("jane@example.com")
            .And.DoesNotContain("123456").And.DoesNotContain("/ui/terrascale/sign-in")
            .And.DoesNotContain("This code expires").And.DoesNotContain("Este código expira");
        await Assert.That(html).Contains(Deadline).And.Contains(">Acme Cloud</td>").And.Contains("mailto:security@acme.test")
            .And.DoesNotContain("class=\"code\"").And.DoesNotContain("mso-padding-alt:").And.DoesNotContain("/ui/terrascale/sign-in");
    }

    [Test]
    public async Task Themes_and_preview_colour_schemes_decide_the_dark_styles()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        const string DarkCanvas = ".canvas{background-color:#0a0c12 !important}";
        async Task<string> Html(string scheme, string? theme)
        {
            var body = new JsonObject { ["kind"] = "sign_in_code", ["locale"] = "en", ["color_scheme"] = scheme };
            if (theme is not null)
            {
                body["branding"] = new JsonObject { ["theme"] = theme };
            }

            return (await JsonAsync(await SendAsync(oidc, HttpMethod.Post, Base + "/preview", body)))["html"]!.GetValue<string>();
        }

        var auto = await Html("auto", null);
        await Assert.That(auto).Contains("@media (prefers-color-scheme:dark){\n" + DarkCanvas);
        await Assert.That(auto).Contains("<meta name=\"color-scheme\" content=\"light dark\">");
        await Assert.That(await Html("dark", null)).Contains("</style>").And.Contains("\n" + DarkCanvas).And.DoesNotContain("prefers-color-scheme");
        await Assert.That(await Html("light", null)).DoesNotContain(DarkCanvas);
        await Assert.That(await Html("dark", "light")).DoesNotContain(DarkCanvas).And.Contains("content=\"light only\"");
        await Assert.That(await Html("light", "dark")).Contains(DarkCanvas).And.DoesNotContain("prefers-color-scheme");

        using var invalid = await SendAsync(oidc, HttpMethod.Post, Base + "/preview", new JsonObject { ["kind"] = "sign_in_code", ["locale"] = "en", ["color_scheme"] = "sepia" });
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);

        // Button text is black or white, whichever reaches 4.5:1; mid grey #777777 gets black (4.69:1, white is 4.48:1).
        foreach (var (primary, text) in new[] { ("#2158e8", "#ffffff"), ("#777777", "#000000"), ("#facc15", "#000000") })
        {
            var button = (await JsonAsync(await SendAsync(oidc, HttpMethod.Post, Base + "/preview", new JsonObject
            {
                ["kind"] = "magic_link",
                ["locale"] = "en",
                ["branding"] = new JsonObject { ["primary_color"] = primary },
            })))["html"]!.GetValue<string>();
            await Assert.That(button).Contains($"color:{text};text-decoration:none;").Because(primary);
        }
    }

    [Test]
    public async Task A_malformed_stored_configuration_is_unavailable_and_codes_still_go_out_with_the_defaults()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        await oidc.CreateUserAsync();
        var store = oidc.Server.App.Services.GetRequiredService<TerraScale.Authentication.Store.AuthStore>();
        string Config(string templates, string branding = "{}", string version = "1") =>
            $$"""{"instance_id":"terrascale","version":{{version}},"created_at":null,"actor":null,"change":null,"branding":{{branding}},"templates":{{templates}}}""";
        string[] malformed =
        [
            Config("""[{"kind":"sign_in_code","locale":"en","fields":null}]"""),
            Config("""[{"kind":"welcome","locale":"en","fields":{}}]"""),
            Config("""[{"kind":"sign_in_code","locale":"en","fields":{"subject":"{% raw %}"}}]"""),
            Config("[]", """{"primary_color":"blue"}"""),
            Config("[]", """{"logo_url":"javascript:alert(1)","logo_width":10,"logo_height":10}"""),
            Config("[]", version: "\"x\""),
            Config("[null]"),
        ];
        foreach (var document in malformed)
        {
            var current = await store.GetAsync(EmailSettings.Collection, OidcIssuer.InstanceId, OidcIssuer.InstanceId);
            var record = TerraBaseFixture.Json(document);
            if (current is null)
            {
                await store.CreateAsync(EmailSettings.Collection, OidcIssuer.InstanceId, OidcIssuer.InstanceId, record);
            }
            else
            {
                await Assert.That(await store.ReplaceAsync(EmailSettings.Collection, OidcIssuer.InstanceId, OidcIssuer.InstanceId, record, current.Version)).IsNotNull();
            }

            using var unavailable = await SendAsync(oidc, HttpMethod.Get, Base + "/branding");
            await Assert.That(unavailable.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable).Because(document);
            await Assert.That(await ErrorCodeAsync(unavailable)).IsEqualTo("temporarily_unavailable");
        }

        // The worker reads the configuration now, finds the last one (templates: [null]) malformed and still sends the code.
        var (code, message) = await RequestCodeAsync(oidc);
        await Assert.That(message.Subject).IsEqualTo($"{code} is your TerraScale sign-in code");
    }

    [Test]
    public async Task A_single_custom_logo_shows_in_light_and_dark_mode_and_a_dark_variant_swaps_in()
    {
        await using var oidc = await OidcHarness.StartAsync(fixture);
        using var png = await oidc.Client.GetAsync("/admin/email-logo.png");
        var logo = await png.Content.ReadAsByteArrayAsync();
        var browser = await Chromium.BrowserAsync();
        foreach (var (darkUrl, scheme, visible) in new (string?, string, string)[]
        {
            (null, "light", "light.png"), (null, "dark", "light.png"), ("https://cdn.example.test/dark.png", "light", "light.png"), ("https://cdn.example.test/dark.png", "dark", "dark.png"),
        })
        {
            var branding = new JsonObject { ["brand_name"] = "Acme", ["logo_url"] = "https://cdn.example.test/light.png", ["logo_width"] = 150, ["logo_height"] = 36, ["logo_dark_url"] = darkUrl };
            var html = (await JsonAsync(await SendAsync(oidc, HttpMethod.Post, Base + "/preview",
                new JsonObject { ["kind"] = "sign_in_code", ["locale"] = "en", ["color_scheme"] = scheme, ["branding"] = branding })))["html"]!.GetValue<string>();
            await using var context = await browser.NewContextAsync(new() { ColorScheme = scheme == "dark" ? Microsoft.Playwright.ColorScheme.Dark : Microsoft.Playwright.ColorScheme.Light });
            var page = await context.NewPageAsync();
            await page.RouteAsync("**/*", route => route.Request.Url.StartsWith("https://cdn.example.test/", StringComparison.Ordinal)
                ? route.FulfillAsync(new() { Status = 200, ContentType = "image/png", BodyBytes = logo })
                : route.AbortAsync());
            await page.SetContentAsync(html, new() { WaitUntil = Microsoft.Playwright.WaitUntilState.Load });
            var shown = await page.Locator("img[alt='Acme']").EvaluateAllAsync<string[]>("images => images.filter(image => image.offsetParent !== null).map(image => image.src)");
            await Assert.That(string.Join(' ', shown)).IsEqualTo("https://cdn.example.test/" + visible).Because($"dark logo {darkUrl}, {scheme} mode");
        }
    }

    [Test]
    public async Task A_test_send_delivers_a_marked_sample_and_is_limited_per_minute()
    {
        var captured = new CapturingEmailSender();
        await using var oidc = await OidcHarness.StartAsync(fixture, email: captured,
            services: services => services.AddSingleton<IEmailSender>(new ProductSender(captured)));
        var request = new JsonObject
        {
            ["kind"] = "password_reset",
            ["locale"] = "en",
            ["to"] = "admin@example.test",
            ["actor"] = Actor.DeepClone(),
            ["branding"] = new JsonObject { ["sender_name"] = "Acme Support", ["reply_to"] = "help@example.test" },
        };
        var accepted = await SendAsync(oidc, HttpMethod.Post, Base + "/test", request.DeepClone());
        await Assert.That(accepted.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        var (recipient, message) = await oidc.Email.NextAsync();
        await Assert.That(recipient).IsEqualTo("admin@example.test");
        await Assert.That(message.Subject).IsEqualTo("[Test] 123456 is your TerraScale password reset code");
        await Assert.That(message.FromName).IsEqualTo("Acme Support");
        await Assert.That(message.ReplyTo).IsEqualTo("help@example.test");
        await Assert.That((await JsonAsync(accepted))["subject"]!.GetValue<string>()).IsEqualTo(message.Subject);

        using var badRecipient = await SendAsync(oidc, HttpMethod.Post, Base + "/test", new JsonObject { ["kind"] = "mfa_code", ["locale"] = "en", ["to"] = "a@b@c", ["actor"] = Actor.DeepClone() });
        await Assert.That(badRecipient.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);

        // Ten sends per minute window: within 21 more, one window fills even if a minute boundary passes once.
        HttpResponseMessage? limited = null;
        for (var sent = 0; sent < 2 * TestEmailEndpoint.PerMinute + 1 && limited is null; sent++)
        {
            var response = await SendAsync(oidc, HttpMethod.Post, Base + "/test", request.DeepClone());
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                limited = response;
            }
            else
            {
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
                response.Dispose();
            }
        }

        await Assert.That(limited).IsNotNull();
        using (limited!)
        {
            await Assert.That(limited.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
            await Assert.That(limited.Headers.RetryAfter?.Delta).IsEqualTo(TimeSpan.FromSeconds(60));
            await Assert.That(await ErrorCodeAsync(limited)).IsEqualTo("rate_limited");
        }
    }

    [Test]
    [Arguments(429, "{}", 429, "rate_limited")]
    [Arguments(403, "{}", 502, "email_rejected")]
    [Arguments(200, "{\"success\":true,\"result\":{\"queued\":[\"someone-else@example.test\"]}}", 502, "email_acceptance_unknown")]
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

    [Test]
    [Arguments("{}", "unknown")]
    [Arguments("{\"success\":true,\"result\":{\"suppressed_recipients\":[\"provider-owner@example.test\"]}}", "rejected")]
    public async Task Product_ledgers_preserve_unconfirmed_provider_outcomes_without_resending(string body, string expected)
    {
        await using var provider = await EmailProviderFixture.FakeCloudflare.StartAsync(body: body);
        using var http = ProviderClient(provider);
        var captured = new CapturingEmailSender();
        await using var oidc = await OidcHarness.StartAsync(fixture, email: captured, services: services =>
            services.AddSingleton<IEmailSender>(new ProductSender(captured, Cloudflare(http))));
        var (user, _, tokens) = await oidc.SignInAndRedeemAsync("provider-owner@example.test");
        var access = tokens.GetProperty("access_token").GetString()!;
        var organization = (await oidc.OkAsync(HttpMethod.Post, PlatformCalls.Organizations, access, new { name = "Provider fixture" }, HttpStatusCode.Created))
            .GetProperty("id").GetString()!;
        var project = (await oidc.OkAsync(HttpMethod.Post, $"{PlatformCalls.Organizations}/{organization}/projects", access, new { name = "Provider fixture" }, HttpStatusCode.Created))
            .GetProperty("id").GetString()!;
        (string Path, JsonObject Request)[] deliveries =
        [
            ("/allowance", new() { ["organizationId"] = organization, ["billingMonth"] = DateTimeOffset.UtcNow.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture),
                ["metric"] = "operations", ["threshold"] = 80, ["used"] = 81, ["allowance"] = 100 }),
            ("/alert", new() { ["organizationId"] = organization, ["projectId"] = project, ["alertId"] = AuthStore.NewId(),
                ["state"] = "firing", ["ruleName"] = "Synthetic alert", ["metric"] = "storage_gb", ["threshold"] = 1, ["value"] = 2 }),
            ("/support", new() { ["requestId"] = AuthStore.NewId(), ["userId"] = user.Id, ["organizationId"] = organization,
                ["plan"] = "pro", ["severity"] = "normal", ["subject"] = "Synthetic request", ["message"] = "Integration fixture" }),
        ];
        foreach (var (path, request) in deliveries)
        {
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var response = await SendAsync(oidc, HttpMethod.Post, Base + path, request.DeepClone());
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString()).IsEqualTo(expected);
            }
        }
        // One attempt for each ledger; no retry or support acknowledgement after an unconfirmed send.
        await Assert.That(provider.Calls).IsEqualTo(deliveries.Length);
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
    // Its bound is wall-clock time, so it runs alone rather than beside the suite's TerraBase-heavy tests.
    [NotInParallel]
    public async Task A_stalled_configuration_read_delays_a_backlog_of_codes_once_and_they_go_out_with_the_defaults()
    {
        var stalled = new StallingHandler("/email_configs/") { InnerHandler = TerraBaseFixture.NewHandler() };
        await using var oidc = await OidcHarness.StartAsync(fixture, services: services => services.AddSingleton(new HttpClient(stalled)));
        string[] addresses = [.. Enumerable.Range(0, 4).Select(index => $"user{index}@example.test")];
        foreach (var address in addresses)
        {
            await oidc.CreateUserAsync(address);
        }

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        foreach (var address in addresses)
        {
            using var response = await oidc.PostFormAsync($"/ui/{OidcIssuer.Slug}/sign-in", new() { ["email"] = address, ["next"] = string.Empty });
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        foreach (var _ in addresses)
        {
            var (_, message) = await oidc.Email.NextAsync();
            await Assert.That(message.Subject).EndsWith(" is your TerraScale sign-in code");
        }

        // One read timed out; the rest of the backlog skipped it instead of waiting 3 s each.
        await Assert.That(elapsed.Elapsed).IsLessThan(SignInMail.ConfigTimeout * 2);
        await Assert.That(stalled.Calls).IsEqualTo(1);
    }

    /// <summary>Holds every TerraBase request whose path contains <c>fragment</c> until it is canceled.</summary>
    private sealed class StallingHandler(string fragment) : DelegatingHandler
    {
        private int calls;

        public int Calls => Volatile.Read(ref this.calls);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains(fragment, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref this.calls);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return await base.SendAsync(request, cancellationToken);
        }
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

    private static async Task<string> MessageAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("message").GetString()!;

    private static async Task MatchesSnapshotAsync(string name, string actual, [CallerFilePath] string source = "")
    {
        var path = Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "TerraScale.Authentication.Fast", "Email", "Snapshots", name);
        if (Environment.GetEnvironmentVariable("TS_AUTH_UPDATE_SNAPSHOTS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, actual);
        }

        await Assert.That(File.Exists(path)).IsTrue().Because($"{path} is missing; run with TS_AUTH_UPDATE_SNAPSHOTS=1");
        await Assert.That(actual).IsEqualTo(await File.ReadAllTextAsync(path)).Because($"{name} changed; review it and run with TS_AUTH_UPDATE_SNAPSHOTS=1");
    }

    [GeneratedRegex(@"\b[0-9]{6}\b")]
    private static partial Regex SixDigits();
}
