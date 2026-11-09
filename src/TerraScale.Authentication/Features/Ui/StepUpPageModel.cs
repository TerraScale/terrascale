using TerraScale.Authentication.Features.Email;

namespace TerraScale.Authentication.Features.Ui;

/// <summary>Hosted step-up display state; never contains the backend's bearer proof.</summary>
public sealed record StepUpPageModel(string Id, string Email, string Purpose, bool Mfa, bool Password, bool MailRequested, string? Notice, string Nonce, string? Locale = null)
{
    public string ResolvedLocale => Locale == AuthEmails.Portuguese ? AuthEmails.Portuguese : AuthEmails.English;

    public HostedPageCopy Copy => HostedCopies.For(this.ResolvedLocale);

    /// <summary>The operation the account is approving.</summary>
    public string Operation => this.Purpose switch
    {
        "delete_account" => this.Copy.OpDeleteAccount,
        "create_pat" => this.Copy.OpCreatePat,
        "create_api_key" => this.Copy.OpCreateApiKey,
        "rotate_api_key" => this.Copy.OpRotateApiKey,
        "create_oauth_client" => this.Copy.OpCreateOauthClient,
        "update_oauth_client" => this.Copy.OpUpdateOauthClient,
        "rotate_oauth_client" => this.Copy.OpRotateOauthClient,
        "change_email" => this.Copy.OpChangeEmail,
        _ => this.Copy.OpVerifyAccount,
    };
}
