using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.UI;
namespace TerraScale.Dashboard.Features.Auth;

public static class DeviceEndpoints
{
    public static void MapDeviceAuthorization(this WebApplication app)
    {
        app.MapPost("/api/v1/dashboard/cli/device-authorization", (HttpContext http, DeviceAuthorization device) => PublicAsync(http, device, false));
        app.MapPost("/api/v1/dashboard/cli/token", (HttpContext http, DeviceAuthorization device) => PublicAsync(http, device, true));
        app.MapGet("/cli/approve", async (HttpContext http, DeviceAuthorization device, DashboardSessions sessions) =>
        {
            var code = http.Request.Query["user_code"].ToString();
            if (code.Length != 0)
            {
                try { await http.RequestServices.GetRequiredService<IAuthAttemptBudget>().ReserveAsync(http, "", AuthBudgetKind.SignIn, http.RequestAborted); }
                catch (AuthRequestRejectedException error)
                {
                    http.Response.Headers.RetryAfter = error.RetryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return Results.Json(new { error = "slow_down" }, statusCode: 429);
                }
            }
            var grant = code.Length == 0 ? null : await device.LookupAsync(code, http.RequestAborted);
            var session = await sessions.ReadAsync(http);
            var pending = grant?.Pending(http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow()) == true;
            return (IResult)DeviceView.Create(new(new("Approve CLI access", DocumentModel.NonceFor(http)), Csrf.Token(http), code,
                session?.Email ?? "", code.Length != 0 && !pending ? "This code is invalid, expired, or already decided. Start a new CLI login." : "", Pending: pending, Authenticated: session is not null));
        });
        app.MapPost("/cli/approve/submit", async (HttpContext http, DeviceAuthorization device, DashboardSessions sessions, AccountService accounts) =>
        {
            if (!http.Request.HasFormContentType || http.Request.ContentLength > 4096) return (IResult)Results.BadRequest("Invalid approval form.");
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!Csrf.Validate(http, form)) return Results.StatusCode(403);
            var session = await sessions.ReadAsync(http);
            if (session is null) return AuthEndpoints.SeeOther("/login?returnTo=" + Uri.EscapeDataString("/cli/approve?user_code=" + Uri.EscapeDataString(form["user_code"].ToString())));
            await accounts.GetContextAsync(session, http.RequestAborted);
            var code = form["user_code"].ToString();
            var decision = form["decision"].ToString();
            if (decision is not ("approve" or "deny")) return Results.BadRequest("Invalid decision.");
            var model = new DevicePage(new("Approve CLI access", DocumentModel.NonceFor(http)), Csrf.Token(http), code, session.Email, Pending: true, Authenticated: true);
            try
            {
                var budget = http.RequestServices.GetRequiredService<IAuthAttemptBudget>();
                await budget.ReserveAsync(http, session.Email, AuthBudgetKind.SignIn, http.RequestAborted);
                var accepted = await device.DecideAsync(http, session, code, decision, form["password"].ToString());
                if (accepted && decision == "approve") await budget.ForgiveAsync(http, session.Email, http.RequestAborted);
                return DeviceView.Create(model with { Pending = false, Result = accepted ? decision : "", Error = accepted ? "" : "This code is invalid, expired, or already decided." });
            }
            catch (SignInRejectedException) { return DeviceView.Create(model with { Error = "Current password is incorrect." }); }
            catch (AuthRequestRejectedException error)
            {
                http.Response.Headers.RetryAfter = error.RetryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Results.Json(new { error = "slow_down" }, statusCode: 429);
            }
            catch (DeviceRejectedException error) { return Results.StatusCode(error.Status); }
        });
    }

    private static async Task<IResult> PublicAsync(HttpContext http, DeviceAuthorization device, bool poll)
    {
        http.Response.Headers.Pragma = "no-cache";
        try
        {
            var form = await DeviceAuthorization.PublicFormAsync(http);
            if (poll && form["grant_type"] != DeviceAuthorization.GrantType) throw new DeviceRejectedException("unsupported_grant_type");
            if (!poll) await http.RequestServices.GetRequiredService<IAuthAttemptBudget>().ReserveAsync(http, "", AuthBudgetKind.SignIn, http.RequestAborted);
            return Results.Json(poll ? await device.PollAsync(form["device_code"].ToString(), http.RequestAborted) : await device.IssueAsync(http, http.RequestAborted));
        }
        catch (DeviceRejectedException error)
        {
            if (error.Code == "invalid_grant")
            {
                try { await http.RequestServices.GetRequiredService<IAuthAttemptBudget>().ReserveAsync(http, "", AuthBudgetKind.SignIn, http.RequestAborted); }
                catch (AuthRequestRejectedException refused)
                { http.Response.Headers.RetryAfter = refused.RetryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture); return Results.Json(new { error = "slow_down" }, statusCode: 429); }
            }
            if (error.RetryAfter > 0) http.Response.Headers.RetryAfter = error.RetryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Json(new { error = error.Code }, statusCode: error.Status);
        }
        catch (AuthRequestRejectedException error)
        {
            http.Response.Headers.RetryAfter = error.RetryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Json(new { error = "slow_down" }, statusCode: 429);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or System.Text.Json.JsonException or TimeoutException or AccountAccessException)
        {
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("DeviceAuthorization").LogWarning("Device authorization dependency failed ({ErrorType})", error.GetType().Name);
            http.Response.Headers.RetryAfter = "5";
            return Results.Json(new { error = "temporarily_unavailable" }, statusCode: 503);
        }
    }
}
