using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Features.Profile
{
    public static class EmailChange
    {
        public static async Task<IResult> SubmitAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, IAuthEmailChangeGateway gateway, bool confirm)
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts, organizationScoped: false);
            if (context is null)
            {
                return AccountRequest.LoginRedirect(http);
            }

            if (!http.Request.HasFormContentType)
            {
                return Results.BadRequest();
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!StepUpFlow.IsResuming(http) && !Csrf.Validate(http, form))
            {
                return Results.BadRequest("Invalid form token.");
            }

            string? pendingEmail = null;
            var status = "";
            try
            {
                if (confirm)
                {
                    var code = form["code"].ToString().Trim();
                    if (code.Length != 6 || !code.All(char.IsAsciiDigit))
                    {
                        throw new AccountAccessException("Enter the six-digit code sent to your new address.", 422, "invalid_request");
                    }

                    await gateway.ConfirmEmailChangeAsync(context.Session, code, http.RequestAborted);
                }
                else
                {
                    var email = form["new_email"].ToString().Trim();
                    if (email.Length > 254 || !System.Net.Mail.MailAddress.TryCreate(email, out var address) || address.Address != email)
                    {
                        throw new AccountAccessException("Enter a valid new email address.", 422, "invalid_request");
                    }

                    var intent = StepUpFlow.Intent(http, context, "change_email");
                    if (intent is null)
                    {
                        return await StepUpFlow.StartAsync(http, context, "change_email", "ts-auth");
                    }

                    var pending = await gateway.RequestEmailChangeAsync(context.Session, email, intent.Challenge.Proof, http.RequestAborted);
                    pendingEmail = pending.NewEmail;
                    status = "Code sent to your new address. Enter it within ten minutes. Your current address received a cancel link.";
                }
            }
            catch (AccountAccessException error)
            {
                var message = error.Code switch
                {
                    "email_taken" => "That email address is already used by another account. Choose another address.",
                    "email_change_denied" => "The code is incorrect or the change expired, was canceled or was replaced. Check the code or start a new change.",
                    "rate_limited" => "Too many email change requests. Wait before starting again.",
                    _ => error.Message
                };
                return await ViewAsync(message, error.StatusCode);
            }
            catch (Exception error) when (error is HttpRequestException or IOException or TimeoutException)
            {
                return await ViewAsync("The email change could not be confirmed. If you already submitted a code, try signing in with the new address before starting again.", 503);
            }
            if (!confirm)
            {
                return await ViewAsync("", 200);
            }
            // Auth has invalidated every grant. Do not attempt to revoke the old bearer again.
            await sessions.RevokeUserAsync(context.UserId, http.RequestAborted);
            await sessions.EndAsync(http);
            return HttpRedirects.SeeOther("/login?emailChanged=1");

            async Task<IResult> ViewAsync(string error, int statusCode)
            {
                var view = ProfileView.Create(new(ShellModel.For(http, context, "Profile"), Error: error, Status: status,
                    Sessions: await sessions.ListAsync(http, context.Session))
                { PendingEmail = pendingEmail, EmailCodeEntry = confirm || pendingEmail is not null });
                view.StatusCode = statusCode;
                return view;
            }
        }
    }
}
