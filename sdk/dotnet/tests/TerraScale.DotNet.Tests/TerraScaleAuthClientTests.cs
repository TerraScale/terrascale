using System.Net;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleAuthClientTests
{
    private static TerraScaleAuthClient CreateClient(
        HttpMessageHandler handler,
        TerraScaleAuthClientOptions? options = null)
    {
        options ??= new TerraScaleAuthClientOptions
        {
            BaseAddress = new Uri("https://auth.example.test/"),
            ApiKey = "sk_key_secret",
            InstanceSlug = "acme",
        };
        return new TerraScaleAuthClient(new HttpClient(handler), options);
    }

    [Test]
    public async Task SecretBearingRecordsRedactSecretsInToString()
    {
        var tokens = new TerraScaleAuthTokenResponse("atk_secretvalue", "Bearer", 3600, null, "rtk_secretvalue", "idt_secretvalue");

        await Assert.That(tokens.ToString()).DoesNotContain("atk_secretvalue")
            .And.DoesNotContain("rtk_secretvalue")
            .And.DoesNotContain("idt_secretvalue");
    }

    [Test]
    public async Task PlatformRoutesCarryTheCallerSignInToken()
    {
        var handler = new RecordingHandler("""{"items":[],"next":null}""");
        var client = CreateClient(handler);

        _ = await client.Platform.ListOrganizationsAsync("fixture-sign-in");

        await Assert.That(handler.Authorization).IsEqualTo("Bearer fixture-sign-in");
        await Assert.That(handler.RequestUri).IsEqualTo("/v1/terrascale/organizations");
        await Assert.That(handler.Accept).IsEqualTo("application/json");
    }

    // The producer serves only the organization-level route (list_audit_events in routes.json);
    // the legacy sk_-keyed instance audit client is retired (TS-1243).
    [Test]
    public async Task NoInstanceAuditClientRemains()
    {
        await Assert.That(typeof(TerraScaleAuthClient).Assembly.GetType("TerraScale.DotNet.TerraScaleAuthAuditClient")).IsNull();
    }

    [Test]
    public async Task OptionsValidationRejectsMissingPiecesAndHostileInput()
    {
        using var handler = new RecordingHandler("[]");
        // An OIDC-only client legitimately skips the management key.
        _ = CreateClient(handler, new TerraScaleAuthClientOptions
        {
            BaseAddress = new Uri("https://auth.example.test/"),
            InstanceSlug = "acme",
        });

        foreach (var options in new[]
        {
            new TerraScaleAuthClientOptions { ApiKey = "sk_key", InstanceSlug = "acme" },
            new TerraScaleAuthClientOptions { BaseAddress = new Uri("https://auth.example.test/"), ApiKey = "sk_key" },
            new TerraScaleAuthClientOptions
            {
                BaseAddress = new Uri("https://auth.example.test/"),
                ApiKey = "sk_key\r\nX-Injected: true",
                InstanceSlug = "acme",
            },
            new TerraScaleAuthClientOptions
            {
                BaseAddress = new Uri("http://auth.example.com/"),
                ApiKey = "sk_key",
                InstanceSlug = "acme",
            },
            new TerraScaleAuthClientOptions
            {
                BaseAddress = new Uri("https://auth.example.test/path"),
                ApiKey = "sk_key",
                InstanceSlug = "acme",
            },
        })
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
            {
                _ = CreateClient(handler, options);
                return Task.CompletedTask;
            });
        }
    }

    [Test]
    public async Task ErrorEnvelopeSurfacesErrorCodeAndMessage()
    {
        var handler = new RecordingHandler("""{"error":{"code":"email_taken","message":"an account with this email already exists"}}""")
        {
            StatusCode = HttpStatusCode.Conflict,
        };
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<TerraScaleAuthException>(() => client.Users.CreateAsync(email: "a@b.test"));

        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(exception.ErrorCode).IsEqualTo("email_taken");
        await Assert.That(exception.Message).IsEqualTo("ts-auth returned HTTP 409.");
    }

    [Test]
    public async Task UserEnumerationWalksCursorPages()
    {
        var handler = new PagedUsersHandler();
        var client = CreateClient(handler);

        var users = new List<TerraScaleAuthUser>();
        await foreach (var user in client.Users.EnumerateAsync(pageSize: 2))
        {
            users.Add(user);
        }

        await Assert.That(users.Select(user => user.Id).SequenceEqual(["usr_1", "usr_2", "usr_3"])).IsTrue();
        // The short second page is terminal: no third request is sent.
        await Assert.That(handler.RequestUris.SequenceEqual([
            "/v1/acme/users?limit=2",
            "/v1/acme/users?limit=2&starting_after=usr_2",
        ])).IsTrue();
    }

    private sealed class RecordingHandler(string body) : HttpMessageHandler
    {
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public string? Authorization { get; private set; }
        public string? RequestUri { get; private set; }
        public string? Accept { get; private set; }
        public string? Body { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            RequestUri = request.RequestUri?.AbsolutePath;
            Accept = request.Headers.Accept.FirstOrDefault()?.MediaType;
            Body = request.Content is null ? null : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            return Task.FromResult(new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class FlakyOnceHandler(string body) : HttpMessageHandler
    {
        public int Attempts;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromResult(new HttpResponseMessage(
                Attempts == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class AlwaysFailingHandler : HttpMessageHandler
    {
        public int Attempts;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class PagedUsersHandler : HttpMessageHandler
    {
        private static readonly string[][] Pages =
        [
            ["""{"id":"usr_1","email":"a@b.test","email_verified":false,"username":null,"name":null,"banned":false,"ban_reason":null,"created_at":1,"last_sign_in_at":null,"public_metadata":null,"private_metadata":null}""", """{"id":"usr_2","email":"b@c.test","email_verified":false,"username":null,"name":null,"banned":false,"ban_reason":null,"created_at":2,"last_sign_in_at":null,"public_metadata":null,"private_metadata":null}""" ],
            ["""{"id":"usr_3","email":"d@e.test","email_verified":false,"username":null,"name":null,"banned":false,"ban_reason":null,"created_at":3,"last_sign_in_at":null,"public_metadata":null,"private_metadata":null}""" ],
        ];

        public List<string> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!.PathAndQuery);
            var index = RequestUris.Count - 1;
            var page = Pages[Math.Min(index, Pages.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[" + string.Join(",", page) + "]}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class HostsHandler(List<string> hosts) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            hosts.Add(request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"keys":[]}""", System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
