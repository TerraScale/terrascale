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

internal sealed class VerifyMfaEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/ui/{slug}/passcode/mfa", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, HttpContext http, AuthStore store, AuthOptions options, SessionService sessions, CancellationToken ct)
    {
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null)
            return ApiResults.NotFound("instance_not_found", "no such instance");

        var (token, code) = await ReadMfaSubmissionAsync(http, ct);
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(code))
            return ApiResults.BadRequest("invalid_request", "mfa_token and code are required");

        var userId = await PeekPendingAsync(store, instance.Id, token, ct);
        if (userId is null)
            return Results.Json(new { ok = false, error = "invalid_mfa_token" }, statusCode: 401);

        if (!await MfaGate.VerifyCodeAsync(store, options.MasterKey, userId, code.Trim(), ct))
            return await DelayedInvalidCodeAsync();

        // The consume is atomic; a concurrent replay loses here and gets no session.
        if (await MfaGate.ConsumePendingAsync(store, instance.Id, token, ct) is null)
            return Results.Json(new { ok = false, error = "invalid_mfa_token" }, statusCode: 401);
        await SignInAsync(store, sessions, http, instance.Id, userId, "passcode+mfa", ct);
        return http.Request.HasFormContentType
            ? Results.Redirect($"/ui/{slug}/account")
            : Results.Ok(new { ok = true });
    }

    private static async Task<IResult> DelayedInvalidCodeAsync()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        return Results.Json(new { ok = false, error = "invalid_code" }, statusCode: 401);
    }

    /// <summary>The MFA step accepts the hosted form (urlencoded) or a JSON fetch.</summary>
    private static async Task<(string? Token, string? Code)> ReadMfaSubmissionAsync(HttpContext http, CancellationToken ct)
    {
        if (http.Request.HasFormContentType)
        {
            var form = await http.Request.ReadFormAsync(ct);
            return (FormValue(form, "mfa_token"), FormValue(form, "code"));
        }
        var body = await http.Request.ReadFromJsonAsync<PasscodeMfaRequest>(ct);
        return (body?.MfaToken, body?.Code);
    }

    private static string? FormValue(IFormCollection form, string key) =>
        form[key].Count == 0 ? null : form[key].ToString();
}
