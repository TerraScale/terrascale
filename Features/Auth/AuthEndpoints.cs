using System.Net.Mail;
using TerraScale.Dashboard.UI;

namespace TerraScale.Dashboard.Features.Auth;

public static class AuthEndpoints
{
    public static void MapAuth(this WebApplication app)
    {
        app.MapGet("/login", (HttpContext context) => View(context, "login"));
        app.MapGet("/login/code", (HttpContext context) => View(context, "code"));
        app.MapGet("/reset-password", (HttpContext context) => View(context, "reset"));
        app.MapPost("/login", (HttpContext context, IAuthGateway auth, DashboardSessions sessions) => SubmitAsync(context, auth, sessions, "login"));
        app.MapPost("/login/code", (HttpContext context, IAuthGateway auth, DashboardSessions sessions) => SubmitAsync(context, auth, sessions, "code"));
        app.MapPost("/login/code/verify", (HttpContext context, IAuthGateway auth, DashboardSessions sessions) => SubmitAsync(context, auth, sessions, "code-verify"));
        app.MapPost("/reset-password", (HttpContext context, IAuthGateway auth, DashboardSessions sessions) => SubmitAsync(context, auth, sessions, "reset"));
        app.MapPost("/reset-password/complete", (HttpContext context, IAuthGateway auth, DashboardSessions sessions) => SubmitAsync(context, auth, sessions, "reset-complete"));
        app.MapPost("/api/v1/auth/logout", async (HttpContext context, DashboardSessions sessions, IAuthGateway auth) =>
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (!Csrf.Validate(context, form)) return Results.StatusCode(403);
            var session = await sessions.ReadAsync(context);
            await sessions.EndAsync(context);
            if (session is not null) await auth.RevokeAsync(session, context.RequestAborted);
            return Results.Redirect("/login");
        });
    }

    private static RazorSlices.RazorSlice View(HttpContext context, string mode) => AuthView.Create(Page(context, mode));

    private static AuthPage Page(HttpContext context, string mode) => new(
        new DocumentModel(mode.StartsWith("reset", StringComparison.Ordinal) ? "Reset password" : "Sign in", DocumentModel.NonceFor(context)),
        mode, Csrf.Token(context), ReturnTo: Csrf.LocalRedirect(context.Request.Query["redirectTo"]));

    private static async Task<IResult> SubmitAsync(HttpContext context, IAuthGateway auth, DashboardSessions sessions, string mode)
    {
        if (!context.Request.HasFormContentType) return Results.BadRequest();
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!Csrf.Validate(context, form)) return Results.StatusCode(403);
        var email = form["email"].ToString().Trim();
        var model = Page(context, mode) with { Email = email, ReturnTo = Csrf.LocalRedirect(form["redirectTo"]) };
        if (!MailAddress.TryCreate(email, out var address) || address.Address != email)
        {
            return Failure(model with { EmailError = "Enter a valid email address." });
        }
        var password = form["password"].ToString();
        if (mode == "login" && string.IsNullOrEmpty(password))
        {
            return Failure(model with { PasswordError = "Enter your password." });
        }
        if (mode == "reset-complete" && (password.Length < 12 || password != form["confirmPassword"]))
        {
            return Failure(model with { PasswordError = "Use at least 12 characters and enter the same password twice." });
        }
        // Fly Proxy is the only production reverse proxy; client-supplied headers elsewhere are ignored.
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "";
        if (Environment.GetEnvironmentVariable("FLY_APP_NAME") is not null &&
            System.Net.IPAddress.TryParse(context.Request.Headers["Fly-Client-IP"], out var flyIp)) ip = flyIp.ToString();
        try
        {
            switch (mode)
            {
                case "code":
                    await auth.StartEmailCodeAsync(email, ip, context.RequestAborted);
                    return AuthView.Create(model with { Mode = "code-verify" });
                case "reset":
                    await auth.StartPasswordResetAsync(email, ip, context.RequestAborted);
                    return AuthView.Create(model with { Mode = "reset-complete" });
                case "reset-complete":
                    await auth.CompletePasswordResetAsync(email, form["code"].ToString(), password, ip, context.RequestAborted);
                    return AuthView.Create(Page(context, "login") with { Notice = "Password changed. Sign in with your new password." });
                default:
                    var session = mode == "code-verify"
                        ? await auth.EmailCodeAsync(email, form["code"].ToString(), ip, context.RequestAborted)
                        : await auth.PasswordAsync(email, password, ip, context.RequestAborted);
                    await sessions.StartAsync(context, session);
                    return Results.Redirect(model.ReturnTo);
            }
        }
        catch (SignInRejectedException)
        {
            return Failure(model with { Error = "We couldn't verify those details. Check them and try again." });
        }
    }

    private static RazorSlices.RazorSlice Failure(AuthPage model)
    {
        var view = AuthView.Create(model);
        view.StatusCode = StatusCodes.Status422UnprocessableEntity;
        return view;
    }
}
