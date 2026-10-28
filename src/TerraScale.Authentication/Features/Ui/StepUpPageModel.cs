namespace TerraScale.Authentication.Features.Ui;

/// <summary>Hosted step-up display state; never contains the backend's bearer proof.</summary>
public sealed record StepUpPageModel(string Id, string Email, string Purpose, bool Mfa, bool Password, bool MailRequested, string? Notice, string Nonce)
{
    /// <summary>The operation the account is approving.</summary>
    public string Operation => this.Purpose switch
    {
        "delete_account" => "Delete your account",
        "create_pat" => "Create a personal access token",
        "create_api_key" => "Create an API key",
        "rotate_api_key" => "Rotate an API key",
        "create_oauth_client" => "Register an OAuth client",
        "change_email" => "Change your sign-in email",
        _ => "Verify your account",
    };
}
