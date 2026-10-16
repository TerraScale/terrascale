using TerraScale.Dashboard.Shared.UI;

namespace TerraScale.Dashboard.Features.Auth;

public sealed record AuthPage(
    DocumentModel Document, string Mode, string CsrfToken, string Email = "", string ReturnTo = "/",
    string Error = "", string EmailError = "", string PasswordError = "", string Notice = "",
    string CodeError = "", string ConfirmPasswordError = "",
    bool SignUp = false, string Name = "", int ResendSeconds = 0, bool ResendUnavailable = false)
{
    public string Title => Mode switch
    {
        "reset" => "Reset password",
        "reset-complete" => "Choose a new password",
        "code" => "Sign in with a code",
        "code-verify" when SignUp => "Verify your email",
        "signup-expired" => "Verify your email",
        "code-verify" => "Check your email",
        _ => "Sign in"
    };
    public string Subtitle => Mode switch
    {
        "reset" => "We email you a code to choose a new password.",
        "reset-complete" => $"If {Email} has a TerraScale account, we emailed it a code. It expires in 10 minutes.",
        "code" => "We email you a one-time code instead of asking for your password.",
        "code-verify" when SignUp => $"Enter the code we emailed to {Email} to verify the address. It expires in 10 minutes.",
        "signup-expired" => "Your verification session has expired. Create your account again to request a new code.",
        "code-verify" => $"If {Email} has a TerraScale account, we emailed it a code. It expires in 10 minutes.",
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

    public string LoginUrl => ReturnTo == "/" ? "/login" : "/login?returnTo=" + Uri.EscapeDataString(ReturnTo);
    public string CodeUrl => "/login/code" + Query(("email", Email), ("returnTo", ReturnTo == "/" ? "" : ReturnTo));
    public string SsoUrl => "/login/sso" + Query(("returnTo", ReturnTo));
    public string SignUpUrl => "/onboarding" + Query(("returnTo", ReturnTo == "/" ? "" : ReturnTo));

    private static string Query(params (string Key, string Value)[] fields)
    {
        var values = fields.Where(field => field.Value.Length != 0).Select(field => field.Key + "=" + Uri.EscapeDataString(field.Value));
        var query = string.Join("&", values);
        return query.Length == 0 ? "" : "?" + query;
    }
}
