namespace TerraScale.Dashboard.Features.Auth;

// ts-auth serves no change-own-password route: the current password requests a
// reset code for the session's email, and the code sets the new password.
public static class ProfileAuthEndpoints
{
    public static void MapProfileIdentity(this WebApplication app)
    {
        app.MapPost("/api/v1/dashboard/profile/password", StartAsync);
        app.MapPost("/api/v1/dashboard/profile/password/complete", CompleteAsync);
    }

    private static Task<IResult> StartAsync(HttpContext context, DashboardSessions sessions, IProfileIdentityGateway identity) =>
        SubmitAsync(context, sessions, async (session, form) =>
        {
            await identity.StartPasswordChangeAsync(session, form["currentPassword"].ToString(), EnrollmentEndpoints.ClientIp(context), context.RequestAborted);
            return HttpRedirects.SeeOther("/profile?tab=security&status=code-sent");
        });

    private static Task<IResult> CompleteAsync(HttpContext context, DashboardSessions sessions, IProfileIdentityGateway identity) =>
        SubmitAsync(context, sessions, async (session, form) =>
        {
            await identity.CompletePasswordChangeAsync(session, form["code"].ToString(), form["newPassword"].ToString(),
                form["confirmPassword"].ToString(), EnrollmentEndpoints.ClientIp(context), context.RequestAborted);
            // ts-auth signed the user out everywhere; remove persistent browser records as well as the current cookie.
            await sessions.RevokeUserAsync(session.UserId, context.RequestAborted);
            await sessions.EndAsync(context);
            return HttpRedirects.SeeOther("/login");
        });

    private static async Task<IResult> SubmitAsync(HttpContext context, DashboardSessions sessions, Func<SessionRecord, IFormCollection, Task<IResult>> action)
    {
        if (!context.Request.HasFormContentType) return Results.BadRequest("Invalid password form.");
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!Csrf.Validate(context, form)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        var session = await sessions.ReadAsync(context);
        if (session is null) return HttpRedirects.SeeOther("/login");
        var budget = context.RequestServices.GetRequiredService<IAuthAttemptBudget>();
        try
        {
            await budget.ReserveAsync(context, session.Email, AuthBudgetKind.SignIn, context.RequestAborted);
            var result = await action(session, form);
            await budget.ForgiveAsync(context, session.Email, context.RequestAborted);
            return result;
        }
        catch (AuthRequestRejectedException error)
        {
            if (error.RetryAfter > 0) context.Response.Headers.RetryAfter = error.RetryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return HttpRedirects.SeeOther("/profile?tab=security&error=" + Uri.EscapeDataString(error.Message));
        }
    }
}
