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

internal sealed class VerifyPasscodeEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/ui/{slug}/passcode/verify", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, HttpContext http, AuthStore store, AuthOptions options, SessionService sessions, CancellationToken ct)
    {
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null)
            return ApiResults.NotFound("instance_not_found", "no such instance");

        var body = await http.Request.ReadFromJsonAsync<PasscodeVerifyRequest>(ct);
        if (body is null || string.IsNullOrWhiteSpace(body.Email) || string.IsNullOrWhiteSpace(body.Code))
            return ApiResults.BadRequest("invalid_request", "email and code are required");

        var user = await store.FindUserByEmailAsync(instance.Id, SessionService.NormalizeEmail(body.Email), ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // The consume is atomic (If-Match): of two concurrent verifies of one
        // code only the first wins, so a double POST mints at most one session.
        var record = user is { Banned: false }
            ? await store.ConsumeVerificationTokenAsync(
                SecureTokens.Sha256Hex(body.Code.Trim()),
                r => r.InstanceId == instance.Id && r.Purpose == "passcode" && r.UserId == user.Id
                    && !r.Consumed && r.ExpiresAt > now,
                ct)
            : null;
        if (record is null || user is null)
            return await InvalidCodeAsync(store, instance.Id, user is { Banned: false } ? user.Id : null, ct);

        if (await MfaGate.RequiredForUserAsync(store, user.Id, ct))
        {
            var (mfaToken, _) = await MfaGate.BeginPendingAsync(store, instance.Id, user.Id, ct);
            return Results.Ok(new { mfa_required = true, mfa_token = mfaToken });
        }

        await SignInAsync(store, sessions, http, instance.Id, user.Id, "passcode", ct);
        return Results.Ok(new { ok = true });
    }

    /// <summary>Counts consecutive failures per user; at the cap the user's pending
    /// passcode tokens are marked consumed. Always delayed to blunt brute force.</summary>
    private static async Task<IResult> InvalidCodeAsync(AuthStore store, string instanceId, string? userId, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        var attempts = 0;
        if (userId is not null)
        {
            var gate = FailureGates[unchecked((uint)userId.GetHashCode()) % (uint)FailureGates.Length];
            await gate.WaitAsync(ct);
            try
            {
                attempts = await store.RecordVerificationFailureAsync(instanceId, userId, "passcode", ct);
            }
            finally
            {
                gate.Release();
            }
        }
        if (userId is not null && attempts == MaxVerifyAttempts)
        {
            await store.InvalidateVerificationTokensAsync(userId, "passcode", ct);
        }
        return Results.Json(new { ok = false, error = "invalid_code" }, statusCode: 401);
    }

    private static readonly SemaphoreSlim[] FailureGates =
        Enumerable.Range(0, 64).Select(static _ => new SemaphoreSlim(1, 1)).ToArray();

    private const int MaxVerifyAttempts = 5;
}
