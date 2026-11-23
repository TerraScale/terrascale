using TerraScale.Authentication.Features.Email;

namespace TerraScale.Authentication.Features.Ui;

public sealed record EmailChangeCancelPageModel(string User, string Token, string? Done, string Nonce, string? Locale = null)
{
    public string ResolvedLocale => Locale == AuthEmails.Portuguese ? AuthEmails.Portuguese : AuthEmails.English;

    public HostedPageCopy Copy => HostedCopies.For(this.ResolvedLocale);
}
