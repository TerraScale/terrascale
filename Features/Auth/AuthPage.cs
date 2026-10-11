using TerraScale.Dashboard.UI;

namespace TerraScale.Dashboard.Features.Auth;

public sealed record AuthPage(
    DocumentModel Document, string Mode, string CsrfToken, string Email = "", string ReturnTo = "/",
    string Error = "", string EmailError = "", string PasswordError = "", string Notice = "")
{
    public string Title => Mode switch
    {
        "reset" => "Reset password",
        "reset-complete" => "Choose a new password",
        "code" => "Sign in with a code",
        "code-verify" => "Check your email",
        _ => "Sign in"
    };
    public string Subtitle => Mode switch
    {
        "reset" => "We email you a code to choose a new password.",
        "reset-complete" => $"If {Email} has a TerraScale account, we emailed it a code. It expires in 10 minutes.",
        "code" => "We email you a one-time code instead of asking for your password.",
        "code-verify" => $"Enter the code we emailed to {Email}.",
        _ => "Access your databases and cloud operations."
    };
    public string Action => Mode switch
    {
        "reset" => "/reset-password",
        "reset-complete" => "/reset-password/complete",
        "code" => "/login/code",
        "code-verify" => "/login/code/verify",
        _ => "/login"
    };
}
