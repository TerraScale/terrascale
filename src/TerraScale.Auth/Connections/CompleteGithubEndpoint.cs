namespace TerraScale.Auth.Connections;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
using TerraScale.Auth.Users;
using TerraScale.Endpoints;

/// <summary>Completes GitHub OAuth and accepts only a verified email returned by GitHub.</summary>
internal sealed class CompleteGithubEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/ui/{slug}/connections/{connectionId}/github/callback", HandleAsync);

    private static async Task<IResult> HandleAsync(
        string slug, string connectionId, HttpRequest request, AuthOptions options, AuthStore store, SessionService sessions, CancellationToken ct)
    {
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null)
            return ConnectionFlow.NotFoundPage();
        var state = request.Query["state"].ToString();
        var expected = request.Cookies[AuthorizeGithubEndpoint.StateCookieName];
        request.HttpContext.Response.Cookies.Delete(AuthorizeGithubEndpoint.StateCookieName,
            AuthorizeGithubEndpoint.StateCookieOptions(slug, connectionId));
        var actual = SecureTokens.Sha256Hex(state);
        if (expected is null || expected.Length != actual.Length
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual)))
            return ConnectionFlow.ErrorPage(slug, "This sign-in attempt is invalid, expired, or already used.");
        if (await ConnectionFlow.ConsumeStateAsync(store, instance, connectionId, state, "github.state", ct) is null)
            return ConnectionFlow.ErrorPage(slug, "This sign-in attempt is invalid, expired, or already used.");
        var connection = await ConnectionFlow.FindConnectionAsync(store, instance.Id, connectionId, "github", ct);
        if (connection is null)
            return ConnectionFlow.NotFoundPage();
        if (connection.TokenEndpoint is null || connection.UserinfoEndpoint is null)
            return ConnectionFlow.ErrorPage(slug, "This GitHub connection is incomplete.");
        var code = request.Query["code"].ToString();
        if (code.Length == 0)
            return ConnectionFlow.ErrorPage(slug, "GitHub did not return an authorization code.");

        try
        {
            var secret = Encoding.UTF8.GetString(SecretBox.Decrypt(options.MasterKey, connection.ClientSecretBox!));
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = connection.ClientId!,
                ["client_secret"] = secret,
                ["code"] = code,
                ["redirect_uri"] = AuthorizeGithubEndpoint.GithubCallbackUrl(options, slug, connection.Id),
            });
            using var exchange = new HttpRequestMessage(HttpMethod.Post, connection.TokenEndpoint) { Content = content };
            exchange.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            exchange.Headers.UserAgent.ParseAdd("TerraScale-Auth/1.0");
            using var tokenResponse = await ConnectionFlow.Upstream.SendAsync(exchange, ct);
            if (!tokenResponse.IsSuccessStatusCode)
                return ConnectionFlow.ErrorPage(slug, "Exchanging the GitHub authorization code failed.", 502);
            using var token = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(ct));
            if (!token.RootElement.TryGetProperty("access_token", out var accessToken)
                || accessToken.ValueKind != JsonValueKind.String || accessToken.GetString() is not { Length: > 0 } bearer)
                return ConnectionFlow.ErrorPage(slug, "GitHub did not return an access token.", 502);

            using var emailRequest = new HttpRequestMessage(HttpMethod.Get, connection.UserinfoEndpoint);
            emailRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            emailRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            emailRequest.Headers.UserAgent.ParseAdd("TerraScale-Auth/1.0");
            using var emailResponse = await ConnectionFlow.Upstream.SendAsync(emailRequest, ct);
            if (!emailResponse.IsSuccessStatusCode)
                return ConnectionFlow.ErrorPage(slug, "Fetching the GitHub email address failed.", 502);
            using var emails = JsonDocument.Parse(await emailResponse.Content.ReadAsStringAsync(ct));
            if (emails.RootElement.ValueKind != JsonValueKind.Array)
                return ConnectionFlow.ErrorPage(slug, "GitHub did not return email addresses.", 502);
            string? email = null;
            foreach (var item in emails.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("verified", out var verified) || verified.ValueKind != JsonValueKind.True
                    || !item.TryGetProperty("email", out var address) || address.ValueKind != JsonValueKind.String)
                    continue;
                if (item.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True)
                {
                    email = address.GetString();
                    break;
                }
                email ??= address.GetString();
            }
            if (email is null)
                return ConnectionFlow.ErrorPage(slug, "GitHub did not provide a verified email address.");
            var user = await ConnectionFlow.FindOrCreateUserAsync(store, sessions, instance, email, true, null, ct);
            if (user is null)
                return ConnectionFlow.ErrorPage(slug, "Could not create or access an account for this identity.");
            await ConnectionFlow.SignInAsync(store, sessions, request.HttpContext, instance, user, $"github:{connection.Id}", ct);
            return Results.Redirect($"/ui/{slug}/account");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or CryptographicException)
        {
            return ConnectionFlow.ErrorPage(slug, "Contacting GitHub failed.", 502);
        }
    }
}
