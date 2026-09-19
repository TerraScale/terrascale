namespace TerraScale.Auth.Tests;

using System.Net;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Users;

/// <summary>Hosted user-facing UI flows over the real service, engine, and SDK.</summary>
[NotInParallel]
public sealed class UsersE2eTests
{
    private static async Task<AuthServiceFixture> ServiceAsync() => await AuthServiceFixture.GetAsync();

    private static async Task<(string InstanceId, string Slug)> InstanceAsync()
    {
        var service = await ServiceAsync();
        var (id, slug, _) = await service.CreateInstanceAsync("users-" + Guid.NewGuid().ToString("N")[..8]);
        return (id, slug);
    }

    private static string Email() => $"u{Guid.NewGuid().ToString("N")[..10]}@example.com";

    private static HttpRequestMessage Post(string path, Dictionary<string, string> fields, string? cookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(fields) };
        if (cookie is not null)
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return request;
    }

    private static HttpRequestMessage Get(string path, string? cookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (cookie is not null)
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return request;
    }

    /// <summary>Extracts the "tsauth_session=token" pair from a Set-Cookie header.</summary>
    private static async Task<string> SessionCookieAsync(HttpResponseMessage response)
    {
        var header = (response.Headers.TryGetValues("Set-Cookie", out var values) ? values.FirstOrDefault() : null)
            ?? throw new InvalidOperationException("expected a session cookie");
        return header.Split(';')[0];
    }

    private static async Task<string> BodyAsync(HttpResponseMessage response) => await response.Content.ReadAsStringAsync();

    /// <summary>Signs in over HTTP and returns (cookie header, session id from the store).</summary>
    private static async Task<(string Cookie, string SessionId)> SignInAsync(AuthServiceFixture service, string slug, string email, string password = "password123")
    {
        using var response = await service.Http.SendAsync(Post($"/ui/{slug}/sign-in", new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = password,
        }));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Found);
        var cookie = await SessionCookieAsync(response);
        var session = await service.Store.FindSessionByTokenAsync(SecureTokens.Sha256Hex(cookie["tsauth_session=".Length..]))
            ?? throw new InvalidOperationException("session missing from store");
        return (cookie, session.Id);
    }

    /// <summary>Signs up over HTTP and returns (cookie header, session id from the store).</summary>
    private static async Task<(string Cookie, string SessionId)> SignUpAsync(AuthServiceFixture service, string slug, string email, string password = "password123")
    {
        using var response = await service.Http.SendAsync(Post($"/ui/{slug}/sign-up", new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = password,
            ["name"] = "Test User",
        }));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Found);
        var cookie = await SessionCookieAsync(response);
        var session = await service.Store.FindSessionByTokenAsync(SecureTokens.Sha256Hex(cookie["tsauth_session=".Length..]))
            ?? throw new InvalidOperationException("session missing from store");
        return (cookie, session.Id);
    }

    /// <summary>Creates a verification token with a known plaintext token value so tests can hit consume endpoints.</summary>
    private static async Task<string> KnownTokenAsync(AuthStore store, string instanceId, string? userId, string purpose, TimeSpan ttl)
    {
        var token = "e2e" + SecureTokens.RandomToken(32);
        await store.CreateVerificationTokenAsync(new VerificationToken
        {
            Id = "vrf_" + SecureTokens.RandomToken(20),
            InstanceId = instanceId,
            UserId = userId,
            Purpose = purpose,
            TokenKey = SecureTokens.Sha256Hex(token),
            ExpiresAt = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds(),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        return token;
    }

    [Test]
    public async Task UnknownInstanceSlugShows404()
    {
        var service = await ServiceAsync();
        using var response = await service.Http.SendAsync(Get($"/ui/does-not-exist-{Guid.NewGuid().ToString("N")[..6]}/sign-in"));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await BodyAsync(response)).Contains("Page not found");
    }

    [Test]
    public async Task SignUpSignInPageRenders()
    {
        var service = await ServiceAsync();
        var (_, slug) = await InstanceAsync();

        using var signUp = await service.Http.SendAsync(Get($"/ui/{slug}/sign-up"));
        await Assert.That(signUp.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var signUpHtml = await BodyAsync(signUp);
        await Assert.That(signUpHtml).Contains("name=\"password\"");
        await Assert.That(signUpHtml).Contains("name=\"name\"");

        using var signIn = await service.Http.SendAsync(Get($"/ui/{slug}/sign-in"));
        await Assert.That(signIn.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var signInHtml = await BodyAsync(signIn);
        await Assert.That(signInHtml).Contains($"action=\"/ui/{slug}/magic-link\"");
        await Assert.That(signInHtml).Contains($"href=\"/ui/{slug}/passkeys/assert\"");
    }

    [Test]
    public async Task SignUpSignsInAndIssuesVerificationToken()
    {
        var service = await ServiceAsync();
        var (instanceId, slug) = await InstanceAsync();
        var email = Email();

        var (cookie, sessionId) = await SignUpAsync(service, slug, email);

        using var account = await service.Http.SendAsync(Get($"/ui/{slug}/account", cookie));
        await Assert.That(account.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var html = await BodyAsync(account);
        await Assert.That(html).Contains(email);
        await Assert.That(html).Contains("email not verified");

        var user = await service.Store.FindUserByEmailAsync(instanceId, email);
        await Assert.That(user!.EmailVerified).IsFalse();
        await Assert.That(user!.LastSignInAt).IsNotNull();

        var tokens = await service.Store.ListVerificationTokensAsync(instanceId, "verify_email", 100);
        await Assert.That(tokens.Any(t => t.InstanceId == instanceId && t.UserId == user.Id && !t.Consumed)).IsTrue();

        var audits = await service.Store.ListAuditEventsAsync(instanceId, 100);
        await Assert.That(audits.Any(e => e.Type == "user.signed_in" && e.Data.TryGetValue("method", out var method) && method.ToString() == "password")).IsTrue();
        await Assert.That(audits.Any(e => e.Type == "user.signed_up")).IsTrue();
    }

    [Test]
    public async Task DuplicateSignUpShowsFriendlyError()
    {
        var service = await ServiceAsync();
        var (_, slug) = await InstanceAsync();
        var email = Email();
        await SignUpAsync(service, slug, email);

        using var response = await service.Http.SendAsync(Post($"/ui/{slug}/sign-up", new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = "password123",
        }));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await BodyAsync(response)).Contains("already registered");
    }

    [Test]
    public async Task InvalidSignUpInputReRendersWithError()
    {
        var service = await ServiceAsync();
        var (_, slug) = await InstanceAsync();

        using var badEmail = await service.Http.SendAsync(Post($"/ui/{slug}/sign-up", new Dictionary<string, string>
        {
            ["email"] = "not-an-email",
            ["password"] = "password123",
        }));
        await Assert.That(badEmail.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await BodyAsync(badEmail)).Contains("valid email");

        using var shortPassword = await service.Http.SendAsync(Post($"/ui/{slug}/sign-up", new Dictionary<string, string>
        {
            ["email"] = Email(),
            ["password"] = "short",
        }));
        await Assert.That(shortPassword.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await BodyAsync(shortPassword)).Contains("at least 8 characters");

        using var doubleAt = await service.Http.SendAsync(Post($"/ui/{slug}/sign-up", new Dictionary<string, string>
        {
            ["email"] = "a@b.example.com@c",
            ["password"] = "password123",
        }));
        await Assert.That(doubleAt.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await BodyAsync(doubleAt)).Contains("valid email");
    }

    [Test]
    public async Task WrongPasswordOrUnknownEmailIsGenericAndSetsNoCookie()
    {
        var service = await ServiceAsync();
        var (_, slug) = await InstanceAsync();
        var email = Email();
        await SignUpAsync(service, slug, email);

        using var wrongPassword = await service.Http.SendAsync(Post($"/ui/{slug}/sign-in", new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = "wrongpassword",
        }));
        await Assert.That(wrongPassword.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await BodyAsync(wrongPassword)).Contains("Invalid email or password");
        await Assert.That(wrongPassword.Headers.Contains("Set-Cookie")).IsFalse();

        using var unknownEmail = await service.Http.SendAsync(Post($"/ui/{slug}/sign-in", new Dictionary<string, string>
        {
            ["email"] = "nobody@example.com",
            ["password"] = "password123",
        }));
        await Assert.That(unknownEmail.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await BodyAsync(unknownEmail)).Contains("Invalid email or password");
        await Assert.That(unknownEmail.Headers.Contains("Set-Cookie")).IsFalse();
    }

    [Test]
    public async Task PasswordlessUsersCannotSignInWithAnyPassword()
    {
        var service = await ServiceAsync();
        var (instanceId, slug) = await InstanceAsync();
        var noPassword = Email();
        var emptyPassword = Email();

        // SSO/management style creation. An explicit empty string must mean
        // password-less too — a hash of "" would let anyone sign in with no credential.
        var sessions = new SessionService(service.Store);
        await sessions.CreateUserAsync(instanceId, noPassword, username: null, password: null, name: null);
        var createdEmpty = await sessions.CreateUserAsync(instanceId, emptyPassword, username: null, password: "", name: null);
        await Assert.That(createdEmpty.PasswordHash).IsNull();
        await Assert.That((await service.Store.FindUserByEmailAsync(instanceId, noPassword))!.PasswordHash).IsNull();

        foreach (var (email, password) in new[] { (noPassword, "password123"), (emptyPassword, ""), (emptyPassword, "password123") })
        {
            using var response = await service.Http.SendAsync(Post($"/ui/{slug}/sign-in", new Dictionary<string, string>
            {
                ["email"] = email,
                ["password"] = password,
            }));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await BodyAsync(response)).Contains("Invalid email or password");
            await Assert.That(response.Headers.Contains("Set-Cookie")).IsFalse();
        }
    }

    [Test]
    public async Task SignInPasswordCheckBurnsArgon2idWorkOnEveryFailurePath()
    {
        // Response timing must not reveal which emails are registered: every
        // failure path burns one full Argon2id verification, like a real mismatch.
        var known = new User
        {
            Id = "usr_x",
            InstanceId = "inst",
            PasswordHash = PasswordHasher.Hash("password123"),
            CreatedAt = 0,
            UpdatedAt = 0,
        };
        var cases = new (User? User, string Password)[]
        {
            (null, "password123"),                          // unknown email
            (known with { Deleted = true }, "password123"), // deleted user
            (known with { PasswordHash = null }, "whatever"), // password-less user
            (known, "wrongpassword"),                       // wrong password
        };
        foreach (var (user, password) in cases)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var ok = SessionService.VerifySignInPassword(user, password);
            watch.Stop();
            await Assert.That(ok).IsFalse();
            await Assert.That(watch.ElapsedMilliseconds).IsGreaterThanOrEqualTo(10);
        }

        await Assert.That(SessionService.VerifySignInPassword(known, "password123")).IsTrue();
    }

    [Test]
    public async Task BannedAccountShowsSuspended()
    {
        var service = await ServiceAsync();
        var (instanceId, slug) = await InstanceAsync();
        var email = Email();
        await SignUpAsync(service, slug, email);

        var user = await service.Store.FindUserByEmailAsync(instanceId, email);
        await service.Store.UpsertUserAsync(user! with { Banned = true });

        using var response = await service.Http.SendAsync(Post($"/ui/{slug}/sign-in", new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = "password123",
        }));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await BodyAsync(response)).Contains("suspended");
        await Assert.That(response.Headers.Contains("Set-Cookie")).IsFalse();
    }

    [Test]
    public async Task SignInOnlyRedirectsToSafeNextTargets()
    {
        var service = await ServiceAsync();
        var (_, slug) = await InstanceAsync();
        var email = Email();
        await SignUpAsync(service, slug, email);

        foreach (var next in new[]
        {
            "https://evil.example/home",
            "//evil.example",
            "https://auth.test.evil.example",
            // Browsers treat "\" as "/" in URLs, so "/\evil.example" reaches "//evil.example".
            "/\\evil.example",
            "/\\/evil.example",
            "\\/evil.example",
            // Browsers strip control characters before parsing, so "/<TAB>/…" also becomes "//…".
            "/\t/evil.example",
        })
        {
            using var response = await service.Http.SendAsync(Post($"/ui/{slug}/sign-in", new Dictionary<string, string>
            {
                ["email"] = email,
                ["password"] = "password123",
                ["next"] = next,
            }));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Found);
            await Assert.That(response.Headers.Location?.ToString()).IsEqualTo($"/ui/{slug}/account");
        }

        using var sameOrigin = await service.Http.SendAsync(Post($"/ui/{slug}/sign-in", new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = "password123",
            ["next"] = $"https://auth.test/ui/{slug}/account",
        }));
        await Assert.That(sameOrigin.Headers.Location?.ToString()).IsEqualTo($"https://auth.test/ui/{slug}/account");

        using var safe = await service.Http.SendAsync(Post($"/ui/{slug}/sign-in", new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = "password123",
            ["next"] = $"/ui/{slug}/account/sessions",
        }));
        await Assert.That(safe.Headers.Location?.ToString()).IsEqualTo($"/ui/{slug}/account/sessions");
    }

    [Test]
    public async Task SignOutRevokesSessionAndClearsCookie()
    {
        var service = await ServiceAsync();
        var (_, slug) = await InstanceAsync();
        var email = Email();
        var (cookie, sessionId) = await SignUpAsync(service, slug, email);

        using var signOut = await service.Http.SendAsync(Post($"/ui/{slug}/sign-out", new Dictionary<string, string>(), cookie));
        await Assert.That(signOut.StatusCode).IsEqualTo(HttpStatusCode.Found);
        await Assert.That(signOut.Headers.Location?.ToString()).IsEqualTo($"/ui/{slug}/sign-in");
        var cleared = signOut.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        await Assert.That(cleared).IsEqualTo("tsauth_session=");

        var session = await service.Store.GetSessionAsync(sessionId);
        await Assert.That(session!.Status).IsEqualTo("revoked");

        using var account = await service.Http.SendAsync(Get($"/ui/{slug}/account", cookie));
        await Assert.That(account.StatusCode).IsEqualTo(HttpStatusCode.Found);
        await Assert.That(account.Headers.Location?.ToString()).Contains("sign-in");
    }

    [Test]
    public async Task AccountRequiresSession()
    {
        var service = await ServiceAsync();
        var (_, slug) = await InstanceAsync();

        using var anonymous = await service.Http.SendAsync(Get($"/ui/{slug}/account"));
        await Assert.That(anonymous.StatusCode).IsEqualTo(HttpStatusCode.Found);
        await Assert.That(anonymous.Headers.Location?.ToString())
            .IsEqualTo($"/ui/{slug}/sign-in?next={Uri.EscapeDataString($"/ui/{slug}/account")}");

        using var garbageCookie = await service.Http.SendAsync(Get($"/ui/{slug}/account", "tsauth_session=garbage"));
        await Assert.That(garbageCookie.StatusCode).IsEqualTo(HttpStatusCode.Found);
    }

    [Test]
    public async Task ChangePasswordRejectsWrongCurrentAndRotates()
    {
        var service = await ServiceAsync();
        var (instanceId, slug) = await InstanceAsync();
        var email = Email();
        var (cookie, _) = await SignUpAsync(service, slug, email);

        using var wrongCurrent = await service.Http.SendAsync(Post($"/ui/{slug}/account/password", new Dictionary<string, string>
        {
            ["current"] = "not-the-password",
            ["new"] = "newpassword9",
        }, cookie));
        await Assert.That(wrongCurrent.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await BodyAsync(wrongCurrent)).Contains("current password is incorrect");

        using var changed = await service.Http.SendAsync(Post($"/ui/{slug}/account/password", new Dictionary<string, string>
        {
            ["current"] = "password123",
            ["new"] = "newpassword9",
        }, cookie));
        await Assert.That(changed.StatusCode).IsEqualTo(HttpStatusCode.Found);
        await Assert.That(changed.Headers.Location?.ToString()).IsEqualTo($"/ui/{slug}/account");

        var user = await service.Store.FindUserByEmailAsync(instanceId, email);
        await Assert.That(SessionService.VerifyPassword(user!, "newpassword9")).IsTrue();
        await Assert.That(SessionService.VerifyPassword(user!, "password123")).IsFalse();

        using var oldPassword = await service.Http.SendAsync(Post($"/ui/{slug}/sign-in", new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = "password123",
        }));
        await Assert.That(await BodyAsync(oldPassword)).Contains("Invalid email or password");

        await SignInAsync(service, slug, email, "newpassword9");

        var audits = await service.Store.ListAuditEventsAsync(instanceId, 100);
        await Assert.That(audits.Any(e => e.Type == "user.password_changed")).IsTrue();
    }

    [Test]
    public async Task ChangePasswordRevokesOtherSessionsButKeepsCurrent()
    {
        var service = await ServiceAsync();
        var (_, slug) = await InstanceAsync();
        var email = Email();
        var (cookie, currentSessionId) = await SignUpAsync(service, slug, email);
        var (_, otherSessionId) = await SignInAsync(service, slug, email);

        using var changed = await service.Http.SendAsync(Post($"/ui/{slug}/account/password", new Dictionary<string, string>
        {
            ["current"] = "password123",
            ["new"] = "newpassword9",
        }, cookie));
        await Assert.That(changed.StatusCode).IsEqualTo(HttpStatusCode.Found);

        await Assert.That((await service.Store.GetSessionAsync(otherSessionId))!.Status).IsEqualTo("revoked");
        await Assert.That((await service.Store.GetSessionAsync(currentSessionId))!.Status).IsEqualTo("active");

        using var stillSignedIn = await service.Http.SendAsync(Get($"/ui/{slug}/account", cookie));
        await Assert.That(stillSignedIn.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task RevokeAllSessionsSignsOutEverywhere()
    {
        var service = await ServiceAsync();
        var (_, slug) = await InstanceAsync();
        var email = Email();
        var (cookie1, session1) = await SignUpAsync(service, slug, email);
        var (_, session2) = await SignInAsync(service, slug, email);
        var (_, session3) = await SignInAsync(service, slug, email);

        using var response = await service.Http.SendAsync(Post($"/ui/{slug}/account/sessions/revoke-all", new Dictionary<string, string>(), cookie1));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Found);
        await Assert.That(response.Headers.Location?.ToString()).IsEqualTo($"/ui/{slug}/sign-in");

        await Assert.That((await service.Store.GetSessionAsync(session1))!.Status).IsEqualTo("revoked");
        await Assert.That((await service.Store.GetSessionAsync(session2))!.Status).IsEqualTo("revoked");
        await Assert.That((await service.Store.GetSessionAsync(session3))!.Status).IsEqualTo("revoked");
    }

    [Test]
    public async Task SessionsPageListsDevicesAndRevokesOne()
    {
        var service = await ServiceAsync();
        var (_, slug) = await InstanceAsync();
        var email = Email();
        var (cookie, _) = await SignUpAsync(service, slug, email);
        var (_, otherSessionId) = await SignInAsync(service, slug, email);

        using var page = await service.Http.SendAsync(Get($"/ui/{slug}/account/sessions", cookie));
        await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var html = await BodyAsync(page);
        await Assert.That(html).Contains("this device");
        await Assert.That(html).Contains("Revoke");

        using var revoke = await service.Http.SendAsync(Post($"/ui/{slug}/account/sessions/{otherSessionId}/revoke", new Dictionary<string, string>(), cookie));
        await Assert.That(revoke.StatusCode).IsEqualTo(HttpStatusCode.Found);
        await Assert.That(revoke.Headers.Location?.ToString()).IsEqualTo($"/ui/{slug}/account/sessions");

        var other = await service.Store.GetSessionAsync(otherSessionId);
        await Assert.That(other!.Status).IsEqualTo("revoked");

        using var stillWorks = await service.Http.SendAsync(Get($"/ui/{slug}/account/sessions", cookie));
        await Assert.That(stillWorks.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task SessionCookieUsesStrictFlags()
    {
        var service = await ServiceAsync();
        var (_, slug) = await InstanceAsync();
        var email = Email();

        using var response = await service.Http.SendAsync(Post($"/ui/{slug}/sign-up", new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = "password123",
            ["name"] = "Test User",
        }));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Found);
        var header = response.Headers.GetValues("Set-Cookie").First().ToLowerInvariant();
        await Assert.That(header).Contains("httponly");
        await Assert.That(header).Contains("secure");
        await Assert.That(header).Contains("samesite=lax");
        await Assert.That(header).Contains("path=/");
        await Assert.That(header.Contains("domain=")).IsFalse();
    }

    [Test]
    public async Task ExpiredSessionIsRejected()
    {
        var service = await ServiceAsync();
        var (instanceId, slug) = await InstanceAsync();
        var email = Email();
        var (cookie, sessionId) = await SignUpAsync(service, slug, email);

        var session = await service.Store.GetSessionAsync(sessionId);
        await service.Store.UpsertSessionAsync(session! with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds() });

        using var response = await service.Http.SendAsync(Get($"/ui/{slug}/account", cookie));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Found);
        await Assert.That(response.Headers.Location?.ToString()).Contains("sign-in");
    }

    [Test]
    public async Task BannedOrDeletedUserSessionsAreRejected()
    {
        var service = await ServiceAsync();
        var (instanceId, slug) = await InstanceAsync();
        var email = Email();
        var (cookie, _) = await SignUpAsync(service, slug, email);
        var user = await service.Store.FindUserByEmailAsync(instanceId, email);

        await service.Store.UpsertUserAsync(user! with { Banned = true });
        using var banned = await service.Http.SendAsync(Get($"/ui/{slug}/account", cookie));
        await Assert.That(banned.StatusCode).IsEqualTo(HttpStatusCode.Found);
        await Assert.That(banned.Headers.Location?.ToString()).Contains("sign-in");

        await service.Store.UpsertUserAsync(user with { Banned = false, Deleted = true });
        using var deleted = await service.Http.SendAsync(Get($"/ui/{slug}/account", cookie));
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.Found);
        await Assert.That(deleted.Headers.Location?.ToString()).Contains("sign-in");
    }

    [Test]
    public async Task SessionCookieDoesNotWorkAcrossInstances()
    {
        var service = await ServiceAsync();
        var (_, slugA) = await InstanceAsync();
        var (_, slugB) = await InstanceAsync();
        var (cookie, _) = await SignUpAsync(service, slugA, Email());

        using var response = await service.Http.SendAsync(Get($"/ui/{slugB}/account", cookie));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Found);
        await Assert.That(response.Headers.Location?.ToString())
            .IsEqualTo($"/ui/{slugB}/sign-in?next={Uri.EscapeDataString($"/ui/{slugB}/account")}");
    }

    [Test]
    public async Task ResolveThrottlesLastActiveAtWrites()
    {
        var service = await ServiceAsync();
        var (instanceId, slug) = await InstanceAsync();
        var email = Email();
        var (cookie, sessionId) = await SignUpAsync(service, slug, email);
        var sessions = new SessionService(service.Store);
        var token = cookie["tsauth_session=".Length..];

        await sessions.ResolveAsync(instanceId, token);
        var first = (await service.Store.GetSessionAsync(sessionId))!.LastActiveAt;
        await Task.Delay(1100);
        await sessions.ResolveAsync(instanceId, token);
        var second = (await service.Store.GetSessionAsync(sessionId))!.LastActiveAt;
        await Assert.That(second).IsEqualTo(first);

        var session = await service.Store.GetSessionAsync(sessionId);
        await service.Store.UpsertSessionAsync(session! with { LastActiveAt = DateTimeOffset.UtcNow.AddSeconds(-301).ToUnixTimeSeconds() });
        await sessions.ResolveAsync(instanceId, token);
        var refreshed = (await service.Store.GetSessionAsync(sessionId))!.LastActiveAt;
        await Assert.That(refreshed > first).IsTrue();
    }

    [Test]
    public async Task MagicLinkAlwaysShowsCheckEmailPage()
    {
        var service = await ServiceAsync();
        var (_, slug) = await InstanceAsync();
        var email = Email();
        await SignUpAsync(service, slug, email);

        using var known = await service.Http.SendAsync(Post($"/ui/{slug}/magic-link", new Dictionary<string, string> { ["email"] = email }));
        await Assert.That(known.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await BodyAsync(known)).Contains("Check your email");

        using var unknown = await service.Http.SendAsync(Post($"/ui/{slug}/magic-link", new Dictionary<string, string> { ["email"] = "nobody@example.com" }));
        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await BodyAsync(unknown)).Contains("Check your email");
    }

    [Test]
    public async Task MagicLinkConsumeSignsInAndIsSingleUse()
    {
        var service = await ServiceAsync();
        var (instanceId, slug) = await InstanceAsync();
        var email = Email();
        await SignUpAsync(service, slug, email);
        var user = await service.Store.FindUserByEmailAsync(instanceId, email);
        var token = await KnownTokenAsync(service.Store, instanceId, user!.Id, "magic_link", TimeSpan.FromMinutes(10));

        using var consume = await service.Http.SendAsync(Get($"/ui/{slug}/magic-link/consume?token={token}"));
        await Assert.That(consume.StatusCode).IsEqualTo(HttpStatusCode.Found);
        await Assert.That(consume.Headers.Location?.ToString()).IsEqualTo($"/ui/{slug}/account");
        var cookie = await SessionCookieAsync(consume);

        using var account = await service.Http.SendAsync(Get($"/ui/{slug}/account", cookie));
        await Assert.That(account.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await BodyAsync(account)).Contains(email);

        using var replay = await service.Http.SendAsync(Get($"/ui/{slug}/magic-link/consume?token={token}"));
        await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await BodyAsync(replay)).Contains("no longer valid");

        var audits = await service.Store.ListAuditEventsAsync(instanceId, 100);
        await Assert.That(audits.Any(e => e.Type == "user.signed_in" && e.Data.TryGetValue("method", out var method) && method.ToString() == "magic_link")).IsTrue();
    }

    [Test]
    public async Task MagicLinkConsumeIsAtomicUnderConcurrentDoubleRequest()
    {
        var service = await ServiceAsync();
        var (instanceId, slug) = await InstanceAsync();
        var email = Email();
        await SignUpAsync(service, slug, email);
        var user = await service.Store.FindUserByEmailAsync(instanceId, email);
        var token = await KnownTokenAsync(service.Store, instanceId, user!.Id, "magic_link", TimeSpan.FromMinutes(10));

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            service.Http.SendAsync(Get($"/ui/{slug}/magic-link/consume?token={token}"))));
        foreach (var response in responses)
            response.Dispose();

        await Assert.That(responses.Count(r => r.StatusCode == HttpStatusCode.Found)).IsEqualTo(1);
        await Assert.That(responses.Count(r => r.StatusCode == HttpStatusCode.BadRequest)).IsEqualTo(5);
    }

    [Test]
    public async Task BadTokensAreRejected()
    {
        var service = await ServiceAsync();
        var (instanceId, slug) = await InstanceAsync();
        var email = Email();
        await SignUpAsync(service, slug, email);
        var user = await service.Store.FindUserByEmailAsync(instanceId, email);

        using var garbage = await service.Http.SendAsync(Get($"/ui/{slug}/magic-link/consume?token=garbage"));
        await Assert.That(garbage.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        var expired = await KnownTokenAsync(service.Store, instanceId, user!.Id, "magic_link", TimeSpan.FromMinutes(-5));
        using var expiredResponse = await service.Http.SendAsync(Get($"/ui/{slug}/magic-link/consume?token={expired}"));
        await Assert.That(expiredResponse.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await BodyAsync(expiredResponse)).Contains("no longer valid");

        var wrongPurpose = await KnownTokenAsync(service.Store, instanceId, user.Id, "verify_email", TimeSpan.FromMinutes(10));
        using var wrongPurposeResponse = await service.Http.SendAsync(Get($"/ui/{slug}/magic-link/consume?token={wrongPurpose}"));
        await Assert.That(wrongPurposeResponse.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        var (otherInstanceId, _) = await InstanceAsync();
        var foreignToken = await KnownTokenAsync(service.Store, otherInstanceId, user.Id, "magic_link", TimeSpan.FromMinutes(10));
        using var foreign = await service.Http.SendAsync(Get($"/ui/{slug}/magic-link/consume?token={foreignToken}"));
        await Assert.That(foreign.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await BodyAsync(foreign)).Contains("no longer valid");
    }

    [Test]
    public async Task ForgotPasswordShowsCheckEmailAndResetForm()
    {
        var service = await ServiceAsync();
        var (instanceId, slug) = await InstanceAsync();
        var email = Email();
        await SignUpAsync(service, slug, email);
        var user = await service.Store.FindUserByEmailAsync(instanceId, email);

        using var request = await service.Http.SendAsync(Post($"/ui/{slug}/forgot-password", new Dictionary<string, string> { ["email"] = email }));
        await Assert.That(request.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await BodyAsync(request)).Contains("Check your email");

        using var unknown = await service.Http.SendAsync(Post($"/ui/{slug}/forgot-password", new Dictionary<string, string> { ["email"] = "nobody@example.com" }));
        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await BodyAsync(unknown)).Contains("Check your email");

        var token = await KnownTokenAsync(service.Store, instanceId, user!.Id, "reset_password", TimeSpan.FromMinutes(30));
        using var form = await service.Http.SendAsync(Get($"/ui/{slug}/reset-password?token={token}"));
        await Assert.That(form.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await BodyAsync(form)).Contains("name=\"password\"");
    }

    [Test]
    public async Task ResetPasswordUpdatesHashAndRevokesAllSessions()
    {
        var service = await ServiceAsync();
        var (instanceId, slug) = await InstanceAsync();
        var email = Email();
        var (_, sessionId) = await SignUpAsync(service, slug, email);
        var user = await service.Store.FindUserByEmailAsync(instanceId, email);
        var token = await KnownTokenAsync(service.Store, instanceId, user!.Id, "reset_password", TimeSpan.FromMinutes(30));

        using var tooShort = await service.Http.SendAsync(Post($"/ui/{slug}/reset-password", new Dictionary<string, string>
        {
            ["token"] = token,
            ["password"] = "short",
        }));
        await Assert.That(tooShort.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await BodyAsync(tooShort)).Contains("at least 8 characters");

        using var reset = await service.Http.SendAsync(Post($"/ui/{slug}/reset-password", new Dictionary<string, string>
        {
            ["token"] = token,
            ["password"] = "freshpassword1",
        }));
        await Assert.That(reset.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await BodyAsync(reset)).Contains("updated");

        var after = await service.Store.FindUserByEmailAsync(instanceId, email);
        await Assert.That(SessionService.VerifyPassword(after!, "freshpassword1")).IsTrue();
        await Assert.That((await service.Store.GetSessionAsync(sessionId))!.Status).IsEqualTo("revoked");

        using var replay = await service.Http.SendAsync(Get($"/ui/{slug}/reset-password?token={token}"));
        await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        using var replayPost = await service.Http.SendAsync(Post($"/ui/{slug}/reset-password", new Dictionary<string, string>
        {
            ["token"] = token,
            ["password"] = "anotherpassword1",
        }));
        await Assert.That(replayPost.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        await SignInAsync(service, slug, email, "freshpassword1");

        var audits = await service.Store.ListAuditEventsAsync(instanceId, 100);
        await Assert.That(audits.Any(e => e.Type == "password.reset")).IsTrue();
    }

    [Test]
    public async Task BannedUserCannotRequestOrUsePasswordReset()
    {
        var service = await ServiceAsync();
        var (instanceId, slug) = await InstanceAsync();
        var email = Email();
        await SignUpAsync(service, slug, email);
        var user = await service.Store.FindUserByEmailAsync(instanceId, email);
        var token = await KnownTokenAsync(service.Store, instanceId, user!.Id, "reset_password", TimeSpan.FromMinutes(30));

        await service.Store.UpsertUserAsync(user with { Banned = true });

        using var forgot = await service.Http.SendAsync(Post($"/ui/{slug}/forgot-password", new Dictionary<string, string> { ["email"] = email }));
        await Assert.That(forgot.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await BodyAsync(forgot)).Contains("Check your email");
        var unconsumed = (await service.Store.ListVerificationTokensAsync(instanceId, "reset_password", 100))
            .Where(t => t.InstanceId == instanceId && t.UserId == user.Id && !t.Consumed).ToList();
        await Assert.That(unconsumed.Count).IsEqualTo(1); // only the pre-ban KnownTokenAsync one

        using var reset = await service.Http.SendAsync(Post($"/ui/{slug}/reset-password", new Dictionary<string, string>
        {
            ["token"] = token,
            ["password"] = "attackerpassword1",
        }));
        await Assert.That(reset.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        var after = await service.Store.FindUserByEmailAsync(instanceId, email);
        await Assert.That(SessionService.VerifyPassword(after!, "password123")).IsTrue();
    }

    [Test]
    public async Task VerifyEmailMarksUserVerified()
    {
        var service = await ServiceAsync();
        var (instanceId, slug) = await InstanceAsync();
        var email = Email();
        await SignUpAsync(service, slug, email);
        var user = await service.Store.FindUserByEmailAsync(instanceId, email);
        var token = await KnownTokenAsync(service.Store, instanceId, user!.Id, "verify_email", TimeSpan.FromHours(24));

        using var verify = await service.Http.SendAsync(Get($"/ui/{slug}/verify-email?token={token}"));
        await Assert.That(verify.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await BodyAsync(verify)).Contains("verified");

        var after = await service.Store.GetUserAsync(user.Id);
        await Assert.That(after!.EmailVerified).IsTrue();

        using var replay = await service.Http.SendAsync(Get($"/ui/{slug}/verify-email?token={token}"));
        await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        var audits = await service.Store.ListAuditEventsAsync(instanceId, 100);
        await Assert.That(audits.Any(e => e.Type == "user.email_verified")).IsTrue();
    }
}
