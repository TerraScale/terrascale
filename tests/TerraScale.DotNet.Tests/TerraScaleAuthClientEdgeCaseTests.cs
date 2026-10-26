using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

/// <summary>
/// Edge-case coverage for the ts-auth client, numbered after the cross-SDK
/// edge-case matrix: A = response decoding, B = status/error mapping,
/// C = request construction, D = retry/deadline/cancellation, E = pagination,
/// F = webhook verification, G = configuration.
/// </summary>
public sealed class TerraScaleAuthClientEdgeCaseTests
{
    // Astral emoji (surrogate pairs), CJK, and a combining mark: the default
    // serializer emits these escaped as uppercase \uXXXX.
    private const string UnicodeName = "👩🚀日本語éclair";

    private static TerraScaleAuthClient CreateClient(
        HttpMessageHandler handler,
        int maxRetryAttempts = 0,
        TimeSpan? retryDelay = null,
        string apiKey = "sk_key_secret",
        string slug = "acme") =>
        new(new HttpClient(handler), new TerraScaleAuthClientOptions
        {
            BaseAddress = new Uri("https://auth.example.test/"),
            ApiKey = apiKey,
            InstanceSlug = slug,
            MaxRetryAttempts = maxRetryAttempts,
            RetryDelay = retryDelay ?? TimeSpan.FromMilliseconds(250),
        });

    private static TerraScaleAuthClient CreateClient(Uri baseAddress, string apiKey = "sk_key", string slug = "acme")
    {
        using var handler = new ScriptedHandler(Always(HttpStatusCode.OK, "{}"));
        return new TerraScaleAuthClient(new HttpClient(handler), new TerraScaleAuthClientOptions
        {
            BaseAddress = baseAddress,
            ApiKey = apiKey,
            InstanceSlug = slug,
        });
    }

    private static string UserJson(string id) =>
        $$"""{"id":"{{id}}","email":"u@x.test","email_verified":true,"username":null,"name":null,"banned":false,"ban_reason":null,"created_at":1,"last_sign_in_at":null,"public_metadata":null,"private_metadata":null}""";

    private static string PageJson(params string[] users) => "{\"data\":[" + string.Join(",", users) + "]}";

    private static string ErrorEnvelopeJson(string code, string message) =>
        "{\"error\":{\"code\":\"" + code + "\",\"message\":\"" + message + "\"}}";

    private static string Sign(string secret, string timestamp, string payload) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(timestamp + "." + payload))).ToLowerInvariant();

    // A. Response-decode edges.

    [Test]
    public async Task A1_MissingOrNullRequiredIdDecodesToNullId()
    {
        const string missingId =
            """{"email":"u@x.test","email_verified":true,"username":null,"name":null,"banned":false,"ban_reason":null,"created_at":1,"last_sign_in_at":null,"public_metadata":null,"private_metadata":null}""";
        var nullId = UserJson("usr_1").Replace("\"id\":\"usr_1\"", "\"id\":null");
        var missing = CreateClient(new ScriptedHandler(Always(HttpStatusCode.OK, missingId)));
        var nullIdClient = CreateClient(new ScriptedHandler(Always(HttpStatusCode.OK, nullId)));

        var missingUser = await missing.Users.GetAsync("usr_1");
        var nullIdUser = await nullIdClient.Users.GetAsync("usr_1");

        // pins current behavior: absent/null id binds to null without a decode
        // error (finding, see report).
        await Assert.That(missingUser.Id).IsNull();
        await Assert.That(nullIdUser.Id).IsNull();
    }

    [Test]
    public async Task A2_PresentNullAndAbsentOptionalBothDecodeToNull()
    {
        var presentNull = CreateClient(new ScriptedHandler(Always(HttpStatusCode.OK, UserJson("usr_1"))));
        const string absentBanReason =
            """{"id":"usr_1","email":"u@x.test","email_verified":true,"username":null,"name":null,"banned":false,"created_at":1,"last_sign_in_at":null,"public_metadata":null,"private_metadata":null}""";
        var absent = CreateClient(new ScriptedHandler(Always(HttpStatusCode.OK, absentBanReason)));

        var withField = await presentNull.Users.GetAsync("usr_1");
        var withoutField = await absent.Users.GetAsync("usr_1");

        await Assert.That(withField.BanReason).IsNull();
        await Assert.That(withoutField.BanReason).IsNull();
    }

    [Test]
    public async Task A3_UnknownExtraFieldsAreIgnored()
    {
        var body = UserJson("usr_1")[..^1] + ""","future_field":{"x":1},"nested":{"deep":[1,2]}}""";
        var client = CreateClient(new ScriptedHandler(Always(HttpStatusCode.OK, body)));

        var user = await client.Users.GetAsync("usr_1");

        await Assert.That(user.Id).IsEqualTo("usr_1");
        await Assert.That(user.Email).IsEqualTo("u@x.test");
    }

    [Test]
    public async Task A4_WrongTypedFieldFailsDecode()
    {
        var body = UserJson("usr_1").Replace("\"created_at\":1", "\"created_at\":\"not-a-number\"");
        var client = CreateClient(new ScriptedHandler(Always(HttpStatusCode.OK, body)));

        var exception = await Assert.ThrowsAsync<TerraScaleAuthException>(() => client.Users.GetAsync("usr_1"));
        await Assert.That(exception!.ErrorCode).IsEqualTo("invalid_response");
    }

    [Test]
    public async Task A5_MalformedJsonFailsDecode()
    {
        var client = CreateClient(new ScriptedHandler(Always(HttpStatusCode.OK, "{\"data\": [")));

        var exception = await Assert.ThrowsAsync<TerraScaleAuthException>(() => client.Users.ListAsync());
        await Assert.That(exception!.ErrorCode).IsEqualTo("invalid_response");
    }

    [Test]
    public async Task A6_EmptyBodyOnJsonRouteThrowsDecodeError()
    {
        var client = CreateClient(new ScriptedHandler(Always(HttpStatusCode.OK, string.Empty)));

        var exception = await Assert.ThrowsAsync<TerraScaleAuthException>(() => client.Users.ListAsync());
        await Assert.That(exception!.ErrorCode).IsEqualTo("invalid_response");
    }

    [Test]
    public async Task A7_NonJsonBodyFailsDecode()
    {
        var client = CreateClient(new ScriptedHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body><h1>Bad Gateway</h1></body></html>", Encoding.UTF8, "text/html"),
        })));

        var exception = await Assert.ThrowsAsync<TerraScaleAuthException>(() => client.Users.ListAsync());
        await Assert.That(exception!.ErrorCode).IsEqualTo("invalid_response");
    }

    [Test]
    public async Task A8_UserEnvelopeRejectsBareArray()
    {
        // Bare array where the users envelope object is expected.
        var bareArray = CreateClient(new ScriptedHandler(Always(HttpStatusCode.OK, "[" + UserJson("usr_1") + "]")));
        var arrayError = await Assert.ThrowsAsync<TerraScaleAuthException>(() => bareArray.Users.ListAsync());
        await Assert.That(arrayError!.ErrorCode).IsEqualTo("invalid_response");

    }

    [Test]
    public async Task A9_NullDataArrayDecodesToNullData()
    {
        var client = CreateClient(new ScriptedHandler(Always(HttpStatusCode.OK, "{\"data\":null}")));

        var page = await client.Users.ListAsync();

        // pins current behavior: a null array silently decodes to Data == null
        // (finding, see report).
        await Assert.That(page.Data).IsNull();
    }

    [Test]
    public async Task A10_LargePageDecodesAllUsers()
    {
        var users = string.Join(",", Enumerable.Range(0, 100).Select(index => UserJson("usr_" + index.ToString("000", CultureInfo.InvariantCulture))));
        var client = CreateClient(new ScriptedHandler(Always(HttpStatusCode.OK, "{\"data\":[" + users + "]}")));

        var page = await client.Users.ListAsync();

        await Assert.That(page.Data.Count).IsEqualTo(100);
        await Assert.That(page.Data[0].Id).IsEqualTo("usr_000");
        await Assert.That(page.Data[^1].Id).IsEqualTo("usr_099");
    }

    [Test]
    public async Task A11_UnicodeRoundTripsInDecodeAndPatchBytes()
    {
        var body = UserJson("usr_1").Replace("\"name\":null", "\"name\":\"" + UnicodeName + "\"");
        var handler = new ScriptedHandler(Always(HttpStatusCode.OK, body));
        var client = CreateClient(handler);

        var user = await client.Users.GetAsync("usr_1");
        await Assert.That(user.Name).IsEqualTo(UnicodeName);

        _ = await client.Users.UpdateAsync("usr_1", patch => patch.Name(UnicodeName));
        // The GET recorded no body, so the PATCH is index 1. The escaped body
        // decodes back to the exact code units server-side.
        await Assert.That(handler.Bodies[1]).IsEqualTo("""{"name":"\uD83D\uDC69\uD83D\uDE80\u65E5\u672C\u8A9Ee\u0301clair"}""");
    }

    [Test]
    public async Task A12_DeadLetterDecodesAttemptsAndLastError()
    {
        const string body =
            """[{"id":"wh_1:evt_1","endpoint_id":"wh_1","event_id":"evt_1","attempts":3,"last_error":"HTTP 500","updated_at":1700000000}]""";
        var client = CreateClient(new ScriptedHandler(Always(HttpStatusCode.OK, body)));

        var deadLetter = await client.Webhooks.ListDeadLetterAsync();

        await Assert.That(deadLetter.Count).IsEqualTo(1);
        await Assert.That(deadLetter[0].Id).IsEqualTo("wh_1:evt_1");
        await Assert.That(deadLetter[0].EndpointId).IsEqualTo("wh_1");
        await Assert.That(deadLetter[0].Attempts).IsEqualTo(3);
        await Assert.That(deadLetter[0].LastError).IsEqualTo("HTTP 500");
    }

    // B. Status-code and error-mapping edges.

    [Test]
    public async Task B12_UnauthorizedEnvelopeSurfacesCodeAndMessage()
    {
        var client = CreateClient(new ScriptedHandler(Always(HttpStatusCode.Unauthorized,
            ErrorEnvelopeJson("unauthorized", "The management key is missing or invalid."))));

        var exception = await Assert.ThrowsAsync<TerraScaleAuthException>(() => client.Users.ListAsync());

        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(exception.ErrorCode).IsEqualTo("unauthorized");
        await Assert.That(exception.Message).IsEqualTo("ts-auth returned HTTP 401.");
    }

    [Test]
    public async Task B13_InsufficientScopeSurfacesCode()
    {
        var client = CreateClient(new ScriptedHandler(Always(HttpStatusCode.Forbidden,
            ErrorEnvelopeJson("insufficient_scope", "This key lacks the required scope."))));

        var exception = await Assert.ThrowsAsync<TerraScaleAuthException>(() => client.Users.ListAsync());

        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(exception.ErrorCode).IsEqualTo("insufficient_scope");
    }

    [Test]
    public async Task B14_UserNotFoundPreservesCode()
    {
        var userClient = CreateClient(new ScriptedHandler(Always(HttpStatusCode.NotFound,
            ErrorEnvelopeJson("user_not_found", "No such user."))));

        var userError = await Assert.ThrowsAsync<TerraScaleAuthException>(() => userClient.Users.GetAsync("usr_x"));

        await Assert.That(userError!.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(userError.ErrorCode).IsEqualTo("user_not_found");
    }

    [Test]
    public async Task B15_EmailTakenConflictSurfacesCode()
    {
        var client = CreateClient(new ScriptedHandler(Always(HttpStatusCode.Conflict,
            ErrorEnvelopeJson("email_taken", "an account with this email already exists"))));

        var exception = await Assert.ThrowsAsync<TerraScaleAuthException>(() => client.Users.CreateAsync(email: "a@b.test"));

        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(exception.ErrorCode).IsEqualTo("email_taken");
    }

    [Test]
    public async Task B16_RateLimitedRetriesOnlyWhenConfiguredAndExposesRetryAfter()
    {
        var rateLimited = ErrorEnvelopeJson("rate_limited", "too many requests");

        var retriedHandler = new ScriptedHandler(Always(HttpStatusCode.TooManyRequests, rateLimited, TimeSpan.Zero));
        var retried = CreateClient(retriedHandler, maxRetryAttempts: 1);
        var retriedError = await Assert.ThrowsAsync<TerraScaleAuthException>(() => retried.Users.ListAsync());
        await Assert.That(retriedError!.ErrorCode).IsEqualTo("rate_limited");
        await Assert.That(retriedHandler.Attempts).IsEqualTo(2);

        var immediateHandler = new ScriptedHandler(Always(HttpStatusCode.TooManyRequests, rateLimited, TimeSpan.FromSeconds(2)));
        var immediate = CreateClient(immediateHandler, maxRetryAttempts: 0);
        var immediateError = await Assert.ThrowsAsync<TerraScaleAuthException>(() => immediate.Users.ListAsync());
        await Assert.That(immediateHandler.Attempts).IsEqualTo(1);
        await Assert.That(immediateError!.ErrorCode).IsEqualTo("rate_limited");
        await Assert.That(immediateError.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task B18_OAuthFlatErrorWithChallengeHeaderMapsCode()
    {
        var handler = new ScriptedHandler((_, _) =>
        {
            var response = JsonResponse(HttpStatusCode.Unauthorized,
                """{"error":"invalid_client","error_description":"client authentication failed"}""");
            response.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Basic", "realm=\"ts-auth\""));
            return Task.FromResult(response);
        });
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<TerraScaleAuthException>(
            () => client.Oidc.RequestClientCredentialsTokenAsync("cid", "secret"));

        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(exception.ErrorCode).IsEqualTo("invalid_client");
        await Assert.That(exception.Message).IsEqualTo("ts-auth returned HTTP 401.");
    }

    [Test]
    public async Task B19_OAuthErrorWithoutDescriptionFallsBackToStatusMessage()
    {
        var client = CreateClient(new ScriptedHandler(Always(HttpStatusCode.Unauthorized, """{"error":"invalid_grant"}""")));

        var exception = await Assert.ThrowsAsync<TerraScaleAuthException>(
            () => client.Oidc.RefreshTokenAsync("cid", "secret", "rt_x"));

        await Assert.That(exception!.ErrorCode).IsEqualTo("invalid_grant");
        await Assert.That(exception.Message).IsEqualTo("ts-auth returned HTTP 401.");
    }

    [Test]
    public async Task B20_NullErrorFieldsFallBackToStatusMessage()
    {
        var client = CreateClient(new ScriptedHandler(Always(HttpStatusCode.InternalServerError, """{"error":{"code":null,"message":null}}""")));

        var exception = await Assert.ThrowsAsync<TerraScaleAuthException>(() => client.Users.ListAsync());

        await Assert.That(exception!.ErrorCode).IsNull();
        await Assert.That(exception.Message).IsEqualTo("ts-auth returned HTTP 500.");
    }

    [Test]
    public async Task B21_EmptyErrorBodyFallsBackToStatusMessage()
    {
        var client = CreateClient(new ScriptedHandler(Always(HttpStatusCode.InternalServerError, string.Empty)));

        var exception = await Assert.ThrowsAsync<TerraScaleAuthException>(() => client.Users.ListAsync());

        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(exception.Message).IsEqualTo("ts-auth returned HTTP 500.");
    }

    [Test]
    public async Task B22_DeleteRejects200WithBodyWhen204IsRequired()
    {
        var client = CreateClient(new ScriptedHandler(Always(HttpStatusCode.OK, "{}")));

        var exception = await Assert.ThrowsAsync<TerraScaleAuthException>(() => client.Users.DeleteAsync("usr_1"));

        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(exception.Message).IsEqualTo("ts-auth returned HTTP 200, but HTTP 204 was required.");
        await Assert.That(exception.RequestId).IsEqualTo("01JA0000000000000000000001");
    }

    // C. Request-construction edges.

    [Test]
    public async Task C23_PathSegmentEscapingAndDotSegments()
    {
        var handler = new ScriptedHandler(Always(HttpStatusCode.OK, UserJson("usr_1")));
        var client = CreateClient(handler);

        _ = await client.Users.GetAsync("a/b?c#d%e&f+g ü");
        await Assert.That(handler.Requests[0]).IsEqualTo("GET /v1/acme/users/a%2Fb%3Fc%23d%25e%26f%2Bg%20%C3%BC");

        // Bare dot segments would let a caller mutate the route (the Uri
        // layer collapses ".." up a level); they are rejected pre-flight.
        await Assert.ThrowsAsync<ArgumentException>(() => client.Users.GetAsync(".."));
        await Assert.ThrowsAsync<ArgumentException>(() => client.Users.GetAsync("."));
        await Assert.That(handler.Requests.Count).IsEqualTo(1);
    }

    [Test]
    public async Task C24_BlankIdentifiersRejectedPreflightWithoutHttp()
    {
        var handler = new ScriptedHandler(Always(HttpStatusCode.OK, "{}"));
        var client = CreateClient(handler);

        // The preflight guard lives in TerraScaleAuthPaths.Escape, so the SDK
        // names the parameter "value". pins current behavior.
        var id = await Assert.ThrowsAsync<ArgumentException>(() => client.Users.GetAsync(" "));
        await Assert.That(id!.ParamName).IsEqualTo("value");

        var session = await Assert.ThrowsAsync<ArgumentException>(() => client.Sessions.RevokeAsync(string.Empty));
        await Assert.That(session!.ParamName).IsEqualTo("value");

        var email = await Assert.ThrowsAsync<ArgumentException>(() => client.Users.UpdateAsync("usr_1", patch => patch.Email(" ")));
        await Assert.That(email!.ParamName).IsEqualTo("email");

        await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            _ = CreateClient(handler, slug: " ");
            return Task.CompletedTask;
        });
        await Assert.That(handler.Attempts).IsEqualTo(0);
    }

    [Test]
    public async Task C25_QueryStartingAfterEncodedExactlyOnce()
    {
        var handler = new ScriptedHandler(Always(HttpStatusCode.OK, PageJson()));
        var client = CreateClient(handler);

        _ = await client.Users.ListAsync(startingAfter: "a&b=c+d%e ü");

        await Assert.That(handler.Requests[0]).IsEqualTo("GET /v1/acme/users?starting_after=a%26b%3Dc%2Bd%25e%20%C3%BC");
    }

    [Test]
    public async Task C26_LimitBoundariesValidatedPreflight()
    {
        foreach (var limit in new[] { 1, 100 })
        {
            var handler = new ScriptedHandler(Always(HttpStatusCode.OK, PageJson()));
            var client = CreateClient(handler);
            _ = await client.Users.ListAsync(limit: limit);
            await Assert.That(handler.Requests[0]).IsEqualTo("GET /v1/acme/users?limit=" + limit);
        }

        // The contract fixes the range at 1..100; both ends are the SDK's to
        // reject pre-flight, without a request.
        foreach (var limit in new[] { 0, 101 })
        {
            var handler = new ScriptedHandler(Always(HttpStatusCode.OK, PageJson()));
            var client = CreateClient(handler);
            var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.Users.ListAsync(limit: limit));
            await Assert.That(exception!.ParamName).IsEqualTo("limit");
            await Assert.That(handler.Attempts).IsEqualTo(0);
        }
    }

    [Test]
    public async Task C27_SlugValidationRules()
    {
        foreach (var rejected in new[] { " ", "a b", "a\tb", "a/b", "a\\b", ".", "..", new string('a', 256) })
        {
            var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            {
                _ = CreateClient(new ScriptedHandler(Always(HttpStatusCode.OK, "{}")), slug: rejected);
                return Task.CompletedTask;
            });
            await Assert.That(exception!.ParamName).IsEqualTo("InstanceSlug");
        }
    }

    [Test]
    public async Task C28_ApiKeyControlCharactersRejectedAtConstruction()
    {
        foreach (var key in new[] { "sk_key\r\nX-Injected: true", "sk_key\tbad", "sk_key\u0007bell" })
        {
            var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            {
                _ = CreateClient(new ScriptedHandler(Always(HttpStatusCode.OK, "{}")), apiKey: key);
                return Task.CompletedTask;
            });
            await Assert.That(exception!.ParamName).IsEqualTo("ApiKey");
        }
    }

    [Test]
    public async Task C29AndC30_TriStatePatchSendsExactlyTheConfiguredFields()
    {
        var handler = new ScriptedHandler(Always(HttpStatusCode.OK, UserJson("usr_1")));
        var client = CreateClient(handler);

        _ = await client.Users.UpdateAsync("usr_1", patch => patch
            .Name("New")
            .Username(null)
            .Password("hunter2!")
            .Email("new@b.test")
            .Banned(true)
            .BanReason(null)
            .PublicMetadata(null)
            .PrivateMetadata(null));
        await Assert.That(handler.Bodies[0]).IsEqualTo(
            """{"name":"New","username":null,"password":"hunter2!","email":"new@b.test","banned":true,"ban_reason":null,"public_metadata":null,"private_metadata":null}""");

        _ = await client.Users.UpdateAsync("usr_1", patch => patch.Name("Solo"));
        await Assert.That(handler.Bodies[1]).IsEqualTo("""{"name":"Solo"}""");
    }

    [Test]
    public async Task C31_EmptyPatchRejectedPreflight()
    {
        var handler = new ScriptedHandler(Always(HttpStatusCode.OK, UserJson("usr_1")));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => client.Users.UpdateAsync("usr_1", static patch => { }));
        await Assert.That(handler.Attempts).IsEqualTo(0);
    }

    [Test]
    public async Task C32_CreateWithEveryOptionalOmittedSendsAllNulls()
    {
        var handler = new ScriptedHandler(Always(HttpStatusCode.OK, UserJson("usr_1")));
        var client = CreateClient(handler);

        _ = await client.Users.CreateAsync();

        // pins current behavior: omitted optionals serialize as explicit nulls.
        await Assert.That(handler.Bodies[0]).IsEqualTo("""{"email":null,"username":null,"password":null,"name":null}""");
    }

    [Test]
    public async Task C33_OAuthFormEncodingRoundTripsReservedCharacters()
    {
        var handler = new ScriptedHandler(Always(HttpStatusCode.OK,
            """{"access_token":"at","token_type":"Bearer","expires_in":60}"""));
        var client = CreateClient(handler);

        _ = await client.Oidc.ExchangeAuthorizationCodeAsync("cid", "s&=+% #x", "c", "https://a.test/cb");

        await Assert.That(handler.Bodies[0]).IsEqualTo(
            "grant_type=authorization_code&client_id=cid&client_secret=s%26%3D%2B%25+%23x&code=c&redirect_uri=https%3A%2F%2Fa.test%2Fcb");
    }

    [Test]
    public async Task C34_MetadataPatchSerializesNestedValues()
    {
        var handler = new ScriptedHandler(Always(HttpStatusCode.OK, UserJson("usr_1")));
        var client = CreateClient(handler);
        using var metadata = JsonDocument.Parse("""{"nested":{"a":[1,2,{"b":true}],"n":1.5,"t":true,"z":null}}""");

        var dictionary = metadata.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone());
        _ = await client.Users.UpdateAsync("usr_1", patch => patch
            .Name("M")
            .PublicMetadata(dictionary));

        await Assert.That(handler.Bodies[0]).IsEqualTo(
            """{"name":"M","public_metadata":{"nested":{"a":[1,2,{"b":true}],"n":1.5,"t":true,"z":null}}}""");
    }

    [Test]
    public async Task C35_OidcDiscoveryAndJwksRequestsCarryNoAuthorization()
    {
        var handler = new ScriptedHandler((request, _) => Task.FromResult(JsonResponse(
            HttpStatusCode.OK,
            request.RequestUri!.AbsolutePath.EndsWith("jwks.json", StringComparison.Ordinal)
                ? """{"keys":[]}"""
                : """{"issuer":"https://auth.example.test/acme","authorization_endpoint":"https://auth.example.test/acme/authorize","token_endpoint":"https://auth.example.test/acme/oauth/token","userinfo_endpoint":"https://auth.example.test/acme/oauth/userinfo","jwks_uri":"https://auth.example.test/acme/.well-known/jwks.json","response_types_supported":["code"],"grant_types_supported":["client_credentials"],"scopes_supported":["openid"]}""")));
        var client = CreateClient(handler);

        _ = await client.Oidc.GetDiscoveryDocumentAsync();
        _ = await client.Oidc.GetJwksAsync();

        // The public OIDC documents are anonymous: the management key must
        // never leak onto them, even though the client holds one.
        await Assert.That(handler.Authorizations.Count).IsEqualTo(2);
        await Assert.That(handler.Authorizations.All(authorization => authorization is null)).IsTrue();
    }

    // D. Retry / deadline / cancellation edges.

    [Test]
    public async Task D35_GetRetriesExhaustedAfterExactlyThreeAttempts()
    {
        var handler = new ScriptedHandler(Always(HttpStatusCode.ServiceUnavailable, "{}"));
        var client = CreateClient(handler, maxRetryAttempts: 2, retryDelay: TimeSpan.Zero);

        var exception = await Assert.ThrowsAsync<TerraScaleAuthException>(() => client.Users.ListAsync());

        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(handler.Attempts).IsEqualTo(3);
    }

    [Test]
    public async Task D36_RetryAfterZeroRetriesImmediately()
    {
        var handler = new ScriptedHandler(Always(HttpStatusCode.ServiceUnavailable, "{}", TimeSpan.Zero));
        var client = CreateClient(handler, maxRetryAttempts: 1, retryDelay: TimeSpan.FromSeconds(10));

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TerraScaleAuthException>(() => client.Users.ListAsync());
        stopwatch.Stop();

        await Assert.That(handler.Attempts).IsEqualTo(2);
        // A 10s backoff would run if Retry-After: 0 were ignored.
        await Assert.That(stopwatch.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task D37_Post503NeverRetried()
    {
        var handler = new ScriptedHandler(Always(HttpStatusCode.ServiceUnavailable, "{}"));
        var client = CreateClient(handler, maxRetryAttempts: 5, retryDelay: TimeSpan.Zero);

        var exception = await Assert.ThrowsAsync<TerraScaleAuthException>(() => client.Users.CreateAsync(email: "a@b.test"));

        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(handler.Attempts).IsEqualTo(1);
    }

    [Test]
    public async Task D38_ClientErrorsNeverRetriedForGet()
    {
        foreach (var (status, code) in new[]
        {
            (HttpStatusCode.BadRequest, "invalid_request"),
            (HttpStatusCode.Conflict, "email_taken"),
            (HttpStatusCode.Unauthorized, "unauthorized"),
            (HttpStatusCode.Forbidden, "insufficient_scope"),
        })
        {
            var handler = new ScriptedHandler(Always(status, ErrorEnvelopeJson(code, "boom")));
            var client = CreateClient(handler, maxRetryAttempts: 3, retryDelay: TimeSpan.Zero);

            var exception = await Assert.ThrowsAsync<TerraScaleAuthException>(() => client.Users.ListAsync());

            await Assert.That(exception!.ErrorCode).IsEqualTo(code);
            await Assert.That(handler.Attempts).IsEqualTo(1);
        }
    }

    [Test]
    public async Task D39_TransportErrorRetriedOnGetOnly()
    {
        var readHandler = new ScriptedHandler(ThrowConnectionRefused());
        var readClient = CreateClient(readHandler, maxRetryAttempts: 1, retryDelay: TimeSpan.Zero);
        await Assert.ThrowsAsync<HttpRequestException>(() => readClient.Users.ListAsync());
        await Assert.That(readHandler.Attempts).IsEqualTo(2);

        var mutationHandler = new ScriptedHandler(ThrowConnectionRefused());
        var mutationClient = CreateClient(mutationHandler, maxRetryAttempts: 1, retryDelay: TimeSpan.Zero);
        await Assert.ThrowsAsync<HttpRequestException>(() => mutationClient.Users.CreateAsync(email: "a@b.test"));
        await Assert.That(mutationHandler.Attempts).IsEqualTo(1);
    }

    [Test]
    public async Task D40_PreCancelledTokenThrowsWithoutHttp()
    {
        var handler = new ScriptedHandler(Always(HttpStatusCode.OK, PageJson()));
        var client = CreateClient(handler);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => client.Users.ListAsync(cancellationToken: cancelled.Token));
        // Cancellation is checked before credentials or the request reach the handler.
        await Assert.That(handler.Attempts).IsEqualTo(0);
    }

    [Test]
    public async Task D41_CancelDuringRetrySleepThrowsOperationCanceled()
    {
        var handler = new ScriptedHandler(Always(HttpStatusCode.ServiceUnavailable, "{}", TimeSpan.FromSeconds(10)));
        var client = CreateClient(handler, maxRetryAttempts: 1);
        using var cancelSoon = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAsync<OperationCanceledException>(() => client.Users.ListAsync(cancellationToken: cancelSoon.Token));
        await Assert.That(handler.Attempts).IsEqualTo(1);
    }

    [Test]
    public async Task D42_CallerTokenComposesWithDeadline()
    {
        var handler = new ScriptedHandler(static async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return null!;
        });
        var client = CreateClient(handler);
        using var cancelSoon = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<OperationCanceledException>(() => client.Users.ListAsync(cancellationToken: cancelSoon.Token));
        stopwatch.Stop();
        await Assert.That(stopwatch.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task D43_RetryAfterAtTheCapSurfacesWithoutSleeping()
    {
        // A Retry-After equal to the 30s cap could never elapse before the
        // request deadline fires, so it surfaces after exactly one attempt.
        var handler = new ScriptedHandler(Always(HttpStatusCode.ServiceUnavailable, "{}", TimeSpan.FromSeconds(30)));
        var client = CreateClient(handler, maxRetryAttempts: 1, retryDelay: TimeSpan.Zero);

        var stopwatch = Stopwatch.StartNew();
        var exception = await Assert.ThrowsAsync<TerraScaleAuthException>(() => client.Users.ListAsync());
        stopwatch.Stop();

        await Assert.That(exception!.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(handler.Attempts).IsEqualTo(1);
        // A 30s sleep would run if the cap were exclusive.
        await Assert.That(stopwatch.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
    }

    // E. Pagination edges.

    [Test]
    public async Task E43_WalkStopsOnEmptyPage()
    {
        var handler = new UserPagesHandler(["usr_1", "usr_2"], ["usr_3", "usr_4"], []);
        var client = CreateClient(handler);

        var users = new List<TerraScaleAuthUser>();
        await foreach (var user in client.Users.EnumerateAsync(pageSize: 2))
        {
            users.Add(user);
        }

        await Assert.That(users.Select(user => user.Id).SequenceEqual(["usr_1", "usr_2", "usr_3", "usr_4"])).IsTrue();
        await Assert.That(handler.RequestUris.SequenceEqual(
        [
            "/v1/acme/users?limit=2",
            "/v1/acme/users?limit=2&starting_after=usr_2",
            "/v1/acme/users?limit=2&starting_after=usr_4",
        ])).IsTrue();
    }

    [Test]
    public async Task E44_ShortFinalPageTerminatesWithoutDuplicate()
    {
        var handler = new UserPagesHandler(["usr_1", "usr_2"], ["usr_3"], []);
        var client = CreateClient(handler);

        var users = new List<TerraScaleAuthUser>();
        await foreach (var user in client.Users.EnumerateAsync(pageSize: 2))
        {
            users.Add(user);
        }

        await Assert.That(users.Select(user => user.Id).SequenceEqual(["usr_1", "usr_2", "usr_3"])).IsTrue();
        // The short page is terminal: no follow-up request is sent.
        await Assert.That(handler.RequestUris.Count).IsEqualTo(2);
    }

    [Test]
    public async Task E46_RepeatedCursorPageStopsTheWalk()
    {
        // The server keeps returning the same full page; the repeated cursor
        // must end the walk instead of looping forever.
        var handler = new ScriptedHandler((_, _) => Task.FromResult(JsonResponse(
            HttpStatusCode.OK,
            PageJson(UserJson("usr_1"), UserJson("usr_2")))));
        var client = CreateClient(handler);

        var users = new List<TerraScaleAuthUser>();
        await foreach (var user in client.Users.EnumerateAsync(pageSize: 2))
        {
            users.Add(user);
        }

        // The hostile page is surfaced once (the cursor check runs after the
        // page is yielded, like the TS client), then the walk stops.
        await Assert.That(users.Count).IsEqualTo(4);
        await Assert.That(handler.Attempts).IsEqualTo(2);
    }

    [Test]
    public async Task E47_BackwardsCursorOnFullPageStopsTheWalk()
    {
        // The second page is full but its cursor (usr_a) regresses below
        // usr_b; an equality-only guard would keep walking (alternating
        // cursors loop forever), monotonicity ends the walk.
        var handler = new UserPagesHandler(["usr_a", "usr_b"], ["usr_0", "usr_a"]);
        var client = CreateClient(handler);

        var users = new List<TerraScaleAuthUser>();
        await foreach (var user in client.Users.EnumerateAsync(pageSize: 2))
        {
            users.Add(user);
        }

        await Assert.That(users.Select(user => user.Id).SequenceEqual(["usr_a", "usr_b", "usr_0", "usr_a"])).IsTrue();
        await Assert.That(handler.RequestUris.Count).IsEqualTo(2);
    }

    [Test]
    public async Task E45_UnknownCursorResumingFromTopDecodesWithoutCrash()
    {
        var handler = new ScriptedHandler((request, _) => Task.FromResult(JsonResponse(
            HttpStatusCode.OK,
            request.RequestUri!.Query.Contains("zzz_unknown") ? PageJson(UserJson("usr_1"), UserJson("usr_2")) : PageJson())));
        var client = CreateClient(handler);

        var page = await client.Users.ListAsync(limit: 2, startingAfter: "zzz_unknown");

        await Assert.That(handler.Requests[0]).IsEqualTo("GET /v1/acme/users?limit=2&starting_after=zzz_unknown");
        await Assert.That(page.Data.Select(user => user.Id).SequenceEqual(["usr_1", "usr_2"])).IsTrue();
    }

    // F. Webhook verifier edges.

    [Test]
    public async Task F47_UppercaseHexSignatureRejected()
    {
        const string payload = """{"id":"evt_1"}""";
        const string timestamp = "1700000000";
        var signature = Sign("whsign_secret", timestamp, payload).ToUpperInvariant();

        await Assert.That(TerraScaleAuthWebhookVerifier.Verify("whsign_secret", timestamp, signature, Encoding.UTF8.GetBytes(payload))).IsFalse();
    }

    [Test]
    public async Task F48_SignatureWithoutPrefixRejected()
    {
        const string payload = """{"id":"evt_1"}""";
        const string timestamp = "1700000000";
        var signature = Sign("whsign_secret", timestamp, payload)["sha256=".Length..];

        await Assert.That(TerraScaleAuthWebhookVerifier.Verify("whsign_secret", timestamp, signature, Encoding.UTF8.GetBytes(payload))).IsFalse();
    }

    [Test]
    public async Task F49_WrongLengthSignatureRejected()
    {
        const string payload = """{"id":"evt_1"}""";
        const string timestamp = "1700000000";
        var valid = Sign("whsign_secret", timestamp, payload);

        await Assert.That(TerraScaleAuthWebhookVerifier.Verify("whsign_secret", timestamp, valid[..^1], Encoding.UTF8.GetBytes(payload))).IsFalse();
        await Assert.That(TerraScaleAuthWebhookVerifier.Verify("whsign_secret", timestamp, valid + "0", Encoding.UTF8.GetBytes(payload))).IsFalse();
    }

    [Test]
    public async Task F50_NonNumericTimestampRejectedInBothToleranceVariants()
    {
        const string payload = """{"id":"evt_1"}""";
        const string timestamp = "not-a-number";
        var signature = Sign("whsign_secret", timestamp, payload);

        await Assert.That(TerraScaleAuthWebhookVerifier.Verify("whsign_secret", timestamp, signature, Encoding.UTF8.GetBytes(payload))).IsFalse();
        await Assert.That(TerraScaleAuthWebhookVerifier.Verify(
            "whsign_secret", timestamp, signature, Encoding.UTF8.GetBytes(payload), TimeSpan.FromMinutes(5), DateTimeOffset.UnixEpoch)).IsFalse();

        // Negative and far-future timestamps pass without a replay window and
        // fail with one.
        var farFromBothExtremes = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        foreach (var extreme in new[] { "-1", "9999999999" })
        {
            var extremeSignature = Sign("whsign_secret", extreme, payload);
            await Assert.That(TerraScaleAuthWebhookVerifier.Verify("whsign_secret", extreme, extremeSignature, Encoding.UTF8.GetBytes(payload))).IsTrue();
            await Assert.That(TerraScaleAuthWebhookVerifier.Verify(
                "whsign_secret", extreme, extremeSignature, Encoding.UTF8.GetBytes(payload), TimeSpan.FromMinutes(5), farFromBothExtremes)).IsFalse();
        }

        // Outside DateTimeOffset's representable unix-second range the guard
        // rejects before the MAC, so the signature content is irrelevant.
        const string outOfRange = "99999999999999";
        await Assert.That(TerraScaleAuthWebhookVerifier.Verify(
            "whsign_secret", outOfRange, "sha256=" + new string('a', 64), Encoding.UTF8.GetBytes(payload), TimeSpan.FromMinutes(5), farFromBothExtremes)).IsFalse();
    }

    [Test]
    public async Task F51_EmptyPayloadVerifies()
    {
        const string timestamp = "1700000000";
        var signature = Sign("whsign_secret", timestamp, string.Empty);

        await Assert.That(TerraScaleAuthWebhookVerifier.Verify("whsign_secret", timestamp, signature, ReadOnlySpan<byte>.Empty)).IsTrue();
    }

    [Test]
    public async Task F52_EmptySecretReturnsFalse()
    {
        const string timestamp = "1700000000";
        var signature = "sha256=" + new string('a', 64);

        // Cross-SDK parity: a missing secret is malformed input, not a throw.
        await Assert.That(TerraScaleAuthWebhookVerifier.Verify(string.Empty, timestamp, signature, ReadOnlySpan<byte>.Empty)).IsFalse();
        await Assert.That(TerraScaleAuthWebhookVerifier.Verify("   ", timestamp, signature, ReadOnlySpan<byte>.Empty)).IsFalse();
    }

    [Test]
    public async Task F53_ToleranceBoundaryExactAndOneSecondPast()
    {
        const string payload = """{"id":"evt_1"}""";
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var tolerance = TimeSpan.FromMinutes(5);

        foreach (var offset in new[] { -tolerance, tolerance })
        {
            var atEdge = now + offset;
            var edgeTimestamp = atEdge.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var edgeSignature = Sign("whsign_secret", edgeTimestamp, payload);
            await Assert.That(TerraScaleAuthWebhookVerifier.Verify(
                "whsign_secret", edgeTimestamp, edgeSignature, Encoding.UTF8.GetBytes(payload), tolerance, now)).IsTrue();

            var onePast = atEdge + (offset < TimeSpan.Zero ? TimeSpan.FromSeconds(-1) : TimeSpan.FromSeconds(1));
            var pastTimestamp = onePast.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var pastSignature = Sign("whsign_secret", pastTimestamp, payload);
            await Assert.That(TerraScaleAuthWebhookVerifier.Verify(
                "whsign_secret", pastTimestamp, pastSignature, Encoding.UTF8.GetBytes(payload), tolerance, now)).IsFalse();
        }
    }

    [Test]
    public async Task F54_UnicodePayloadBytesVerify()
    {
        const string payload = "日本語 👩‍🚀 check";
        const string timestamp = "1700000000";
        var signature = Sign("whsign_secret", timestamp, payload);

        await Assert.That(TerraScaleAuthWebhookVerifier.Verify("whsign_secret", timestamp, signature, Encoding.UTF8.GetBytes(payload))).IsTrue();
        await Assert.That(TerraScaleAuthWebhookVerifier.Verify("whsign_secret", timestamp, signature, Encoding.UTF8.GetBytes(payload + "!"))).IsFalse();
    }

    [Test]
    public async Task F55_DistinctSecretsProduceDistinctSignatures()
    {
        const string payload = """{"id":"evt_1"}""";
        const string timestamp = "1700000000";
        var signatureA = Sign("secret_a", timestamp, payload);
        var signatureB = Sign("secret_b", timestamp, payload);

        await Assert.That(string.Equals(signatureA, signatureB, StringComparison.Ordinal)).IsFalse();
        await Assert.That(TerraScaleAuthWebhookVerifier.Verify("secret_a", timestamp, signatureA, Encoding.UTF8.GetBytes(payload))).IsTrue();
        await Assert.That(TerraScaleAuthWebhookVerifier.Verify("secret_b", timestamp, signatureA, Encoding.UTF8.GetBytes(payload))).IsFalse();
    }

    // G. Configuration edges.

    [Test]
    public async Task G56_BaseAddressMustBeBareOrigin()
    {
        foreach (var baseAddress in new Uri?[]
        {
            null,
            new Uri("https://auth.example.test/path"),
            new Uri("https://auth.example.test/?q=1"),
            new Uri("https://auth.example.test/#fragment"),
            new Uri("https://user:pass@auth.example.test/"),
            new Uri("auth.example.test", UriKind.Relative),
        })
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
            {
                _ = CreateClient(baseAddress!);
                return Task.CompletedTask;
            });
        }
    }

    [Test]
    public async Task G57_HttpAllowedOnlyForLoopbackOrTestHosts()
    {
        foreach (var allowed in new[] { "http://localhost", "http://127.0.0.1", "http://auth.test/", "https://auth.example.com/" })
        {
            _ = CreateClient(new Uri(allowed));
        }

        await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            _ = CreateClient(new Uri("http://auth.example.com/"));
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task G58_TrailingSlashIrrelevant()
    {
        foreach (var baseAddress in new[] { "https://auth.example.test", "https://auth.example.test/" })
        {
            var handler = new ScriptedHandler(Always(HttpStatusCode.OK,
                """{"keys":[]}"""));
            var client = new TerraScaleAuthClient(new HttpClient(handler), new TerraScaleAuthClientOptions
            {
                BaseAddress = new Uri(baseAddress),
                ApiKey = "sk_key",
                InstanceSlug = "acme",
            });

            _ = await client.Oidc.GetJwksAsync();
            await Assert.That(handler.Requests[0]).IsEqualTo("GET /acme/.well-known/jwks.json");
        }
    }

    [Test]
    public async Task G59_ConflictingHttpClientBaseAddressThrows()
    {
        var httpClient = new HttpClient(new ScriptedHandler(Always(HttpStatusCode.OK, "{}")))
        {
            BaseAddress = new Uri("https://other.example.test/"),
        };

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            _ = new TerraScaleAuthClient(httpClient, new TerraScaleAuthClientOptions
            {
                BaseAddress = new Uri("https://auth.example.test/"),
                ApiKey = "sk_key",
                InstanceSlug = "acme",
            });
            return Task.CompletedTask;
        });

        await Assert.That(exception!.ParamName).IsEqualTo("options");
    }

    [Test]
    public async Task G60_MutatingOptionsAfterConstructionDoesNotAffectClient()
    {
        var handler = new ScriptedHandler(Always(HttpStatusCode.ServiceUnavailable, "{}"));
        var options = new TerraScaleAuthClientOptions
        {
            BaseAddress = new Uri("https://auth.example.test/"),
            ApiKey = "sk_original",
            InstanceSlug = "acme",
            MaxRetryAttempts = 0,
        };
        var client = new TerraScaleAuthClient(new HttpClient(handler), options);

        options.MaxRetryAttempts = 5;
        options.ApiKey = "sk_mutated";
        options.InstanceSlug = "evil";

        await Assert.ThrowsAsync<TerraScaleAuthException>(() => client.Users.ListAsync());

        // The client snapshotted the options at construction: no retries, the
        // original key, and the original instance slug.
        await Assert.That(handler.Attempts).IsEqualTo(1);
        await Assert.That(handler.Authorizations[0]).IsEqualTo("Bearer sk_original");
        await Assert.That(handler.Requests[0]).IsEqualTo("GET /v1/acme/users");
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string body, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        response.Headers.TryAddWithoutValidation("X-Request-ID", "01JA0000000000000000000001");
        if (retryAfter is { } delay)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        }

        return response;
    }

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Always(
        HttpStatusCode statusCode,
        string body,
        TimeSpan? retryAfter = null) =>
        (_, _) => Task.FromResult(JsonResponse(statusCode, body, retryAfter));

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> ThrowConnectionRefused() =>
        (_, _) => throw new HttpRequestException("connection refused");

    private sealed class ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public List<string?> Bodies { get; } = [];

        public List<string?> Authorizations { get; } = [];

        public int Attempts => Requests.Count;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.Method.Method + " " + request.RequestUri!.PathAndQuery);
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            Authorizations.Add(request.Headers.Authorization?.ToString());
            return await respond(request, cancellationToken);
        }
    }

    private sealed class UserPagesHandler(params string[][] pages) : HttpMessageHandler
    {
        public List<string> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!.PathAndQuery);
            var page = pages[Math.Min(RequestUris.Count - 1, pages.Length - 1)];
            var users = string.Join(",", page.Select(UserJson));
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, "{\"data\":[" + users + "]}"));
        }
    }
}
