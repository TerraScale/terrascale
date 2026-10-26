using System.Net;
using System.Security.Cryptography;
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
        var webhook = new TerraScaleAuthCreatedWebhook("wh_1", "https://hook.example.test/", ["*"], "whsec_secretvalue");
        var tokens = new TerraScaleAuthTokenResponse("atk_secretvalue", "Bearer", 3600, null, "rtk_secretvalue", "idt_secretvalue");

        await Assert.That(webhook.ToString()).DoesNotContain("whsec_secretvalue");
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

    [Test]
    public async Task The_public_auth_origin_is_called_directly_without_regional_discovery()
    {
        var hosts = new List<string>();
        using var handler = new HostsHandler(hosts);
        var client = CreateClient(handler, new TerraScaleAuthClientOptions
        {
            BaseAddress = new Uri("https://auth.terrascale.tech/"),
            ApiKey = "sk_key_secret",
            InstanceSlug = "acme",
        });

        _ = await client.Oidc.GetJwksAsync();

        await Assert.That(hosts).IsEquivalentTo(["auth.terrascale.tech"]);
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
    public async Task FlatOAuthErrorSurfacesErrorCode()
    {
        var handler = new RecordingHandler("""{"error":"invalid_client","error_description":"bad credentials"}""")
        {
            StatusCode = HttpStatusCode.Unauthorized,
        };
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<TerraScaleAuthException>(
            () => CreateClient(handler).Oidc.RequestClientCredentialsTokenAsync("client", "secret"));

        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(exception.ErrorCode).IsEqualTo("invalid_client");
        await Assert.That(exception.Message).IsEqualTo("ts-auth returned HTTP 401.");
    }

    [Test]
    public async Task RetriesRepeatSafeReadsButNeverMutations()
    {
        var readHandler = new FlakyOnceHandler("""{"keys":[]}""");
        var readClient = CreateClient(readHandler, new TerraScaleAuthClientOptions
        {
            BaseAddress = new Uri("https://auth.example.test/"),
            ApiKey = "sk_key",
            InstanceSlug = "acme",
            MaxRetryAttempts = 1,
            RetryDelay = TimeSpan.Zero,
        });
        var keys = await readClient.Oidc.GetJwksAsync();
        await Assert.That(keys.Keys.Count).IsEqualTo(0);
        await Assert.That(readHandler.Attempts).IsEqualTo(2);

        var mutationHandler = new AlwaysFailingHandler();
        var mutationClient = CreateClient(mutationHandler, new TerraScaleAuthClientOptions
        {
            BaseAddress = new Uri("https://auth.example.test/"),
            ApiKey = "sk_key",
            InstanceSlug = "acme",
            MaxRetryAttempts = 5,
            RetryDelay = TimeSpan.Zero,
        });
        var exception = await Assert.ThrowsAsync<TerraScaleAuthException>(
            () => mutationClient.Users.DeleteAsync("usr_1"));
        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(mutationHandler.Attempts).IsEqualTo(1);
    }

    [Test]
    public async Task UserPatchSendsExactTriStateJson()
    {
        var handler = new RecordingHandler("""{"id":"usr_1","email":null,"email_verified":false,"username":null,"name":null,"banned":false,"ban_reason":null,"created_at":1,"last_sign_in_at":null,"public_metadata":null,"private_metadata":null}""");
        var client = CreateClient(handler);

        _ = await client.Users.UpdateAsync("usr_1", patch => patch
            .Name("New Name")
            .Username(null)
            .Banned(true)
            .BanReason("abuse"));

        using var document = JsonDocument.Parse(handler.Body!);
        var root = document.RootElement;
        await Assert.That(root.GetProperty("name").GetString()).IsEqualTo("New Name");
        await Assert.That(root.GetProperty("username").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(root.GetProperty("banned").GetBoolean()).IsTrue();
        await Assert.That(root.GetProperty("ban_reason").GetString()).IsEqualTo("abuse");
        await Assert.That(root.TryGetProperty("email", out _)).IsFalse();
        await Assert.That(root.TryGetProperty("password", out _)).IsFalse();
    }

    [Test]
    public async Task EmptyUserPatchIsRejected()
    {
        using var handler = new RecordingHandler("{}");
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => client.Users.UpdateAsync("usr_1", static patch => { }));
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

    [Test]
    public async Task NonNoContentResponseToNoContentCallFails()
    {
        var handler = new RecordingHandler("{}");
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<TerraScaleAuthException>(() => client.Users.DeleteAsync("usr_1"));

        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task ManagementCallWithoutApiKeyFailsFast()
    {
        using var handler = new RecordingHandler("{}");
        var client = CreateClient(handler, new TerraScaleAuthClientOptions
        {
            BaseAddress = new Uri("https://auth.example.test/"),
            InstanceSlug = "acme",
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.Users.ListAsync());
    }

    [Test]
    public async Task WebhookVerifierAcceptsDispatcherSignaturesAndRejectsTampering()
    {
        const string secret = "whsign_secret";
        const string timestamp = "1700000000";
        const string payload = """{"id":"evt_1","type":"user.created","created_at":1700000000,"data":{"user_id":"usr_1"}}""";
        var signature = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(timestamp + "." + payload))).ToLowerInvariant();

        await Assert.That(TerraScaleAuthWebhookVerifier.Verify(secret, timestamp, signature, Encoding.UTF8.GetBytes(payload))).IsTrue();
        await Assert.That(TerraScaleAuthWebhookVerifier.Verify(secret, timestamp, signature, Encoding.UTF8.GetBytes(payload + " "))).IsFalse();
        await Assert.That(TerraScaleAuthWebhookVerifier.Verify("other", timestamp, signature, Encoding.UTF8.GetBytes(payload))).IsFalse();
        await Assert.That(TerraScaleAuthWebhookVerifier.Verify(secret, null, signature, Encoding.UTF8.GetBytes(payload))).IsFalse();
        await Assert.That(TerraScaleAuthWebhookVerifier.Verify(secret, timestamp, "sha256=" + new string('a', 64), Encoding.UTF8.GetBytes(payload))).IsFalse();

        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var tolerance = TimeSpan.FromMinutes(5);
        await Assert.That(TerraScaleAuthWebhookVerifier.Verify(secret, timestamp, signature, Encoding.UTF8.GetBytes(payload), tolerance, now)).IsTrue();
        await Assert.That(TerraScaleAuthWebhookVerifier.Verify(
            secret, timestamp, signature, Encoding.UTF8.GetBytes(payload), tolerance, now.AddMinutes(6))).IsFalse();
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
