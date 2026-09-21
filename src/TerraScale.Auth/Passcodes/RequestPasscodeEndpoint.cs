namespace TerraScale.Auth.Passcodes;

using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Email;
using TerraScale.Auth.Mfa;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.Auth.Users;
using TerraScale.Endpoints;
using static TerraScale.Auth.Passcodes.PasscodeSupport;

internal sealed class RequestPasscodeEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/ui/{slug}/passcode/request", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, HttpContext http, AuthStore store, IEmailSender emailSender, CancellationToken ct)
    {
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null)
            return ApiResults.NotFound("instance_not_found", "no such instance");

        var body = await http.Request.ReadFromJsonAsync<PasscodeRequest>(ct);
        if (body is null || string.IsNullOrWhiteSpace(body.Email))
            return ApiResults.BadRequest("invalid_request", "email is required");

        var user = await store.FindUserByEmailAsync(instance.Id, SessionService.NormalizeEmail(body.Email), ct);
        if (user is { Banned: false } && !await ThrottledAsync(store, user.Id, ct))
        {
            var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            await store.CreateVerificationTokenAsync(new VerificationToken
            {
                Id = "vtok_" + SecureTokens.RandomToken(20),
                InstanceId = instance.Id,
                UserId = user.Id,
                Purpose = "passcode",
                TokenKey = SecureTokens.Sha256Hex(code),
                ExpiresAt = DateTimeOffset.UtcNow.Add(CodeLifetime).ToUnixTimeSeconds(),
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            }, ct);
            await emailSender.SendAsync(user.Email ?? "", "Your ts-auth sign-in code", $"Your ts-auth sign-in code is {code}", ct);
        }
        // Same body whether the user exists, the email is unknown, or sending was
        // throttled — no account enumeration.
        return Results.Ok(new { ok = true });
    }

    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    /// <summary>True when the user already received a passcode within the resend window.</summary>
    private static async Task<bool> ThrottledAsync(AuthStore store, string userId, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)ResendThrottle.TotalSeconds;
        return (await store.ListUserVerificationTokensAsync(userId, "passcode", ct)).Any(t => t.CreatedAt > cutoff);
    }

    private static readonly TimeSpan ResendThrottle = TimeSpan.FromSeconds(30);
}
