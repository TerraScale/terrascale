using System.Text;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.Features.Auth;

public interface IProfileIdentityGateway
{
    Task ChangePasswordAsync(SessionRecord session, string currentPassword, string newPassword,
        string confirmPassword, string ip, CancellationToken cancellationToken);
}

public sealed partial class TsAuthGateway : IProfileIdentityGateway
{
    public async Task ChangePasswordAsync(SessionRecord session, string currentPassword, string newPassword,
        string confirmPassword, string ip, CancellationToken cancellationToken)
    {
        SessionRecord fresh;
        try { fresh = await PasswordAsync(session.Email, currentPassword, ip, cancellationToken); }
        catch (SignInRejectedException) { throw new AuthRequestRejectedException("Current password is incorrect.", "currentPassword"); }
        try
        {
            // The management credential never substitutes for proof that this
            // browser knows the current password for its verified ts-auth user.
            if (fresh.UserId != session.UserId || fresh.Email != session.Email)
                throw new AuthRequestRejectedException("Current password is incorrect.", "currentPassword");
            if (newPassword.EnumerateRunes().Count() < 12)
                throw new AuthRequestRejectedException("Use at least 12 characters.", "newPassword");
            if (Encoding.UTF8.GetByteCount(newPassword) > 1024)
                throw new AuthRequestRejectedException("Use a password with 1024 bytes or fewer.", "newPassword");
            if (newPassword != confirmPassword)
                throw new AuthRequestRejectedException("Passwords do not match.", "confirmPassword");

            var credentials = await identity.ReadAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(credentials.AuthManagementKey) ||
                !credentials.AuthManagementKey.StartsWith("sk_", StringComparison.Ordinal) || credentials.AuthManagementKey.Any(char.IsControl))
                throw new IOException("Dashboard authentication management identity is unavailable.");
            var client = new TerraScaleAuthClient(http.CreateClient("platform"), new TerraScaleAuthClientOptions
            {
                BaseAddress = AuthOrigin,
                UseReverseProxy = true,
                InstanceSlug = credentials.AuthInstanceSlug,
                ApiKey = credentials.AuthManagementKey
            });
            try { await client.Users.UpdateAsync(fresh.UserId, patch => patch.Password(newPassword), cancellationToken); }
            catch (TerraScaleAuthException error) when (error.ErrorCode == "invalid_password")
            { throw new AuthRequestRejectedException("Choose a different password.", "newPassword"); }
            await client.Users.RevokeAllSessionsAsync(fresh.UserId, cancellationToken);
        }
        finally
        {
            // The extra grant is never persisted. Revoke its actual token family
            // even when validation fails or the browser disconnects.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await RevokeAsync(fresh, cleanup.Token); }
            catch (Exception error) when (error is HttpRequestException or IOException or TimeoutException or OperationCanceledException) { }
        }
    }
}

public static class ProfileAuthEndpoints
{
    public static void MapProfileIdentity(this WebApplication app) =>
        app.MapPost("/api/v1/dashboard/profile/password", ChangePasswordAsync);

    private static async Task<IResult> ChangePasswordAsync(HttpContext context, DashboardSessions sessions, IProfileIdentityGateway identity)
    {
        if (!context.Request.HasFormContentType) return Results.BadRequest("Invalid password form.");
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!Csrf.Validate(context, form)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        var session = await sessions.ReadAsync(context);
        if (session is null) return AuthEndpoints.SeeOther("/login");
        try
        {
            var budget = context.RequestServices.GetRequiredService<IAuthAttemptBudget>();
            await budget.ReserveAsync(context, session.Email, AuthBudgetKind.SignIn, context.RequestAborted);
            await identity.ChangePasswordAsync(session, form["currentPassword"].ToString(), form["newPassword"].ToString(),
                form["confirmPassword"].ToString(), EnrollmentEndpoints.ClientIp(context), context.RequestAborted);
            await budget.ForgiveAsync(context, session.Email, context.RequestAborted);
        }
        catch (AuthRequestRejectedException error)
        {
            if (error.RetryAfter > 0) context.Response.Headers.RetryAfter = error.RetryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return AuthEndpoints.SeeOther("/profile?tab=security&error=" + Uri.EscapeDataString(error.Message));
        }
        // Provider revocation is authoritative for every dashboard replica and
        // PAT; remove persistent browser records as well as the current cookie.
        await sessions.RevokeUserAsync(session.UserId, context.RequestAborted);
        await sessions.EndAsync(context);
        return AuthEndpoints.SeeOther("/login");
    }
}
