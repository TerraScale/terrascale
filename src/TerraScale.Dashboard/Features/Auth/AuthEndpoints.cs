using System.Net.Mail;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.UI;

namespace TerraScale.Dashboard.Features.Auth;

public static class AuthEndpoints
{
    public static void MapAuth(this WebApplication app)
    {
        app.MapEnrollmentAuth();
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
            if (!context.Request.HasFormContentType) return Results.BadRequest();
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (!Csrf.Validate(context, form)) return Results.StatusCode(403);
            var session = await sessions.ReadAsync(context);
            if (session is not null) await auth.RevokeAsync(session, context.RequestAborted);
            await sessions.EndAsync(context);
            var target = await context.RequestServices.GetRequiredService<IAuthLogoutGateway>().EndSessionUrlAsync(
                $"{context.Request.Scheme}://{context.Request.Host}", context.RequestAborted);
            return SeeOther(target);
        });
    }

    private static RazorSlices.RazorSlice View(HttpContext context, string mode) => AuthView.Create(Page(context, mode));

    private static AuthPage Page(HttpContext context, string mode)
    {
        var query = context.Request.Query;
        var returnTo = query["returnTo"].ToString();
        if (returnTo.Length == 0) returnTo = query["next"].ToString();
        if (returnTo.Length == 0) returnTo = query["redirectTo"].ToString();
        return new AuthPage(
            new DocumentModel(mode.StartsWith("reset", StringComparison.Ordinal) ? "Reset password" : mode.StartsWith("code", StringComparison.Ordinal) ? "Sign in with a code" : "Sign in", DocumentModel.NonceFor(context)),
            mode, Csrf.Token(context), Email: query["email"].ToString(), ReturnTo: Csrf.LocalRedirect(returnTo),
            Notice: mode == "login" && query["notice"] == "password-reset" ? "Your password was changed. Sign in with your new password." : "");
    }

    private static async Task<IResult> SubmitAsync(HttpContext context, IAuthGateway auth, DashboardSessions sessions, string mode)
    {
        if (!context.Request.HasFormContentType) return Results.BadRequest();
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!Csrf.Validate(context, form)) return Results.StatusCode(403);
        var email = form["email"].ToString().Trim();
        var model = Page(context, mode) with { Email = email, ReturnTo = Csrf.LocalRedirect(form["redirectTo"]) };
        SignUpFlow? signUp = null;
        if (mode == "code-verify" && form["signUp"] == "1")
        {
            var flows = context.RequestServices.GetRequiredService<AuthFlows>();
            signUp = await flows.ReadSignUpAsync(context);
            if (signUp is null || signUp.Email != email)
                return Failure(model with { Mode = "signup-expired" }, StatusCodes.Status400BadRequest);
            model = model with { SignUp = true, Name = signUp.Name, ReturnTo = signUp.ReturnTo, ResendSeconds = flows.Remaining(signUp) };
        }
        if (!MailAddress.TryCreate(email, out var address) || address.Address != email)
        {
            return Failure(model with { EmailError = "Enter a valid email address." });
        }
        var password = form["password"].ToString();
        if (mode == "login" && string.IsNullOrEmpty(password))
        {
            return Failure(model with { PasswordError = "Enter your password." });
        }
        var code = string.Concat(form["code"].ToString().Where(character => !char.IsWhiteSpace(character)));
        if ((mode is "code-verify" or "reset-complete") && (code.Length == 0 || code.Length > 64))
        {
            return Failure(model with { CodeError = "Enter the code from the email." });
        }
        if (mode == "reset-complete")
        {
            if (password.Length == 0) return Failure(model with { PasswordError = "Enter a password." });
            if (password.EnumerateRunes().Count() < 12) return Failure(model with { PasswordError = "Use a password with at least 12 characters." });
            if (System.Text.Encoding.UTF8.GetByteCount(password) > 1024) return Failure(model with { PasswordError = "Use a password with 1024 bytes or fewer." });
            if (form["confirmPassword"].ToString().Length == 0) return Failure(model with { ConfirmPasswordError = "Enter your password again." });
            if (password != form["confirmPassword"]) return Failure(model with { ConfirmPasswordError = "Passwords do not match." });
        }
        // Fly Proxy is the only production reverse proxy; client-supplied headers elsewhere are ignored.
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "";
        if (Environment.GetEnvironmentVariable("FLY_APP_NAME") is not null &&
            System.Net.IPAddress.TryParse(context.Request.Headers["Fly-Client-IP"], out var flyIp)) ip = flyIp.ToString();
        try
        {
            var budget = context.RequestServices.GetRequiredService<IAuthAttemptBudget>();
            await budget.ReserveAsync(context, email, mode is "code" or "reset" ? AuthBudgetKind.Mail : AuthBudgetKind.SignIn, context.RequestAborted);
            switch (mode)
            {
                case "code":
                    await auth.StartEmailCodeAsync(email, ip, context.RequestAborted);
                    return AuthView.Create(model with { Mode = "code-verify" });
                case "reset":
                    await auth.StartPasswordResetAsync(email, ip, context.RequestAborted);
                    return AuthView.Create(model with { Mode = "reset-complete" });
                case "reset-complete":
                    await auth.CompletePasswordResetAsync(email, code, password, ip, context.RequestAborted);
                    await budget.ForgiveAsync(context, email, context.RequestAborted);
                    return SeeOther("/login?email=" + Uri.EscapeDataString(email) + "&notice=password-reset");
                default:
                    var session = signUp is not null
                        ? await context.RequestServices.GetRequiredService<IAuthEnrollmentGateway>().VerifySignUpAsync(email, code, signUp.PendingToken, ip, context.RequestAborted)
                        : mode == "code-verify"
                        ? await auth.EmailCodeAsync(email, code, ip, context.RequestAborted)
                        : await auth.PasswordAsync(email, password, ip, context.RequestAborted);
                    await budget.ForgiveAsync(context, email, context.RequestAborted);
                    if (signUp is not null)
                    {
                        if (session.Name == session.Email) session = session with { Name = signUp.Name };
                        await context.RequestServices.GetRequiredService<AuthFlows>().EndSignUpAsync(context);
                    }
                    var account = await EnsureAccountAsync(context, auth, session);
                    await sessions.StartAsync(context, session);
                    return SeeOther(model.ReturnTo == "/" && !account.OnboardingCompleted ? "/onboarding" : model.ReturnTo);
            }
        }
        catch (AuthRequestRejectedException error)
        {
            if (error.RetryAfter > 0) context.Response.Headers.RetryAfter = error.RetryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Failure(error.Field == "password" ? model with { PasswordError = error.Message } : model with { Error = error.Message }, error.StatusCode);
        }
        catch (SignInRejectedException)
        {
            return mode switch
            {
                "login" => Failure(model with { Error = "Incorrect email or password." }, StatusCodes.Status401Unauthorized),
                "code-verify" => Failure(model with { CodeError = "That code is wrong or has expired. Check the latest email, or send a new code." }, StatusCodes.Status401Unauthorized),
                "reset-complete" => Failure(model with { CodeError = "That code is wrong or has expired. Check the latest email, or request a new code." }, StatusCodes.Status401Unauthorized),
                _ => Failure(model with { Error = "We couldn't verify those details. Check them and try again." })
            };
        }
    }

    internal static IResult SeeOther(string location) => new SeeOtherResult(location);

    internal static async Task<AccountRecord> EnsureAccountAsync(HttpContext context, IAuthGateway auth, SessionRecord session)
    {
        try { return await context.RequestServices.GetRequiredService<AccountService>().EnsureAsync(session, context.RequestAborted); }
        catch
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await auth.RevokeAsync(session, cleanup.Token); }
            catch (Exception error)
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("DashboardAuth")
                    .LogWarning("Rejected account grant cleanup failed ({ErrorType})", error.GetType().Name);
            }
            throw;
        }
    }

    private sealed record SeeOtherResult(string Location) : IResult
    {
        public Task ExecuteAsync(HttpContext context)
        {
            context.Response.StatusCode = StatusCodes.Status303SeeOther;
            context.Response.Headers.Location = Location;
            return Task.CompletedTask;
        }
    }

    private static RazorSlices.RazorSlice Failure(AuthPage model, int statusCode = StatusCodes.Status422UnprocessableEntity)
    {
        var view = AuthView.Create(model);
        view.StatusCode = statusCode;
        return view;
    }
}
