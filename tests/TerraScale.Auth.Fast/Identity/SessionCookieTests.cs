using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TerraScale.Auth.Features.Identity;
using TerraScale.Auth.Features.Ui;
using TerraScale.Auth.Hosting;
using TerraScale.Auth.Tests;
using TerraScale.DotNet.Testing;

namespace TerraScale.Auth.Fast.Identity;

/// <summary>
/// Drives passcode sign-in, reauthentication, session rotation and sign-out over real HTTP against the real TerraBase binary.
/// The routes exist only in this test process; hosted pages serve these flows in a later stream.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class SessionCookieTests(TerraBaseFixture fixture)
{
    private const string Instance = "cookie-instance";

    [Test]
    public async Task Passcode_sign_in_sets_a_secure_cookie_that_reauthenticates_rotates_and_signs_out_server_side()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        await using var auth = await StartAsync(tenant);
        var passcodes = auth.App.Services.GetRequiredService<Passcodes>();
        await Assert.That(auth.App.Services.GetServices<IHostedService>().OfType<SessionPruner>().Count()).IsEqualTo(1);
        await auth.App.Services.GetRequiredService<Users>().CreateAsync(Instance, "cookie@example.test");

        var first = await SignInAsync(auth, passcodes, presented: null);
        var cookie = first.Headers.GetValues("Set-Cookie").Single();
        var token = cookie.Split(';')[0]["tsauth_session=".Length..];
        await Assert.That(cookie.StartsWith("tsauth_session=", StringComparison.Ordinal)).IsTrue();
        await Assert.That(token.Length).IsEqualTo(48);
        var attributes = cookie.Split(';').Skip(1).Select(part => part.Trim().ToLowerInvariant()).ToArray();
        await Assert.That(attributes).IsEquivalentTo(["path=/", "secure", "samesite=lax", "httponly"]);
        await Assert.That((await SessionAsync(auth, token)).Status).IsEqualTo(HttpStatusCode.OK);
        var stored = (await database.ListDocumentsAsync("sessions")).Items.Single().Document.GetRawText();
        await Assert.That(stored).DoesNotContain(token);

        // Signing in again over an existing session replaces its identifier.
        var second = await SignInAsync(auth, passcodes, presented: token);
        var rotated = second.Headers.GetValues("Set-Cookie").Single().Split(';')[0]["tsauth_session=".Length..];
        await Assert.That(rotated).IsNotEqualTo(token);
        await Assert.That((await SessionAsync(auth, token)).Status).IsEqualTo(HttpStatusCode.Unauthorized);
        var (status, expiry) = await SessionAsync(auth, rotated);
        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);

        // A privilege change rotates the identifier without extending the absolute expiry.
        using var rotate = new HttpRequestMessage(HttpMethod.Post, "/__test/rotate");
        rotate.Headers.Add("Cookie", $"tsauth_session={rotated}");
        using var rotation = await auth.Client.SendAsync(rotate);
        await Assert.That(rotation.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        var successor = rotation.Headers.GetValues("Set-Cookie").Single().Split(';')[0]["tsauth_session=".Length..];
        await Assert.That((await SessionAsync(auth, rotated)).Status).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await SessionAsync(auth, successor)).IsEqualTo((HttpStatusCode.OK, expiry));

        using var signOut = new HttpRequestMessage(HttpMethod.Post, "/__test/sign-out");
        signOut.Headers.Add("Cookie", $"tsauth_session={successor}");
        using var signedOut = await auth.Client.SendAsync(signOut);
        await Assert.That(signedOut.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        var cleared = signedOut.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant();
        await Assert.That(cleared).StartsWith("tsauth_session=;");
        await Assert.That(cleared).Contains("expires=thu, 01 jan 1970 00:00:00 gmt");
        // A browser that ignores the deletion still holds a dead token.
        await Assert.That((await SessionAsync(auth, successor)).Status).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    private static async Task<HttpResponseMessage> SignInAsync(AuthTestServer auth, Passcodes passcodes, string? presented)
    {
        var issued = (await passcodes.RequestAsync(Instance, "cookie@example.test"))!;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/__test/sign-in")
        {
            Content = new FormUrlEncodedContent([new("email", "Cookie@Example.test"), new("code", issued.Code)]),
        };
        if (presented is not null)
        {
            request.Headers.Add("Cookie", $"tsauth_session={presented}");
        }

        var response = await auth.Client.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        return response;
    }

    private static async Task<(HttpStatusCode Status, string Expiry)> SessionAsync(AuthTestServer auth, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/__test/session");
        request.Headers.Add("Cookie", $"tsauth_session={token}");
        using var response = await auth.Client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private Task<AuthTestServer> StartAsync(TerraBaseTestTenant tenant) =>
        AuthTestServer.StartAsync(
            settings: values =>
            {
                values["terrascale-api-url"] = fixture.Server.ClientAddress.GetLeftPart(UriPartial.Authority);
                values["terrascale-api-key"] = tenant.ApiKey;
                values["database"] = $"{tenant.Project.Id}/auth";
            },
            services: services => services.AddSingleton(fixture.Server.Http),
            routes: app =>
            {
                app.MapPost("/__test/sign-in", async (HttpContext context, Passcodes passcodes, Sessions sessions) =>
                {
                    var form = await context.Request.ReadFormAsync();
                    if (await passcodes.VerifyAsync(Instance, form["email"].ToString(), form["code"].ToString()) is not { } issued)
                    {
                        return Results.Unauthorized();
                    }

                    await sessions.RevokeAsync(Instance, context.Request.Cookies[SecuritySchemes.SessionCookieName]);
                    BrowserSecurity.SetSessionCookie(context.Response, issued.Token);
                    return Results.NoContent();
                });
                app.MapGet("/__test/session", async (HttpContext context, Sessions sessions) =>
                    await sessions.ResolveAsync(Instance, context.Request.Cookies[SecuritySchemes.SessionCookieName]) is { } live
                        ? Results.Text(live.Session.ExpiresAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture))
                        : Results.Unauthorized());
                app.MapPost("/__test/rotate", async (HttpContext context, Sessions sessions) =>
                {
                    if (await sessions.RotateAsync(Instance, context.Request.Cookies[SecuritySchemes.SessionCookieName]) is not { } rotated)
                    {
                        return Results.Unauthorized();
                    }

                    BrowserSecurity.SetSessionCookie(context.Response, rotated.Token);
                    return Results.NoContent();
                });
                app.MapPost("/__test/sign-out", async (HttpContext context, Sessions sessions) =>
                {
                    await sessions.RevokeAsync(Instance, context.Request.Cookies[SecuritySchemes.SessionCookieName]);
                    BrowserSecurity.DeleteSessionCookie(context.Response);
                    return Results.NoContent();
                });
            });
}
