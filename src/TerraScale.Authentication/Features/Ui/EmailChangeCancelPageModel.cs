using TerraScale.Authentication.Features.Email;

namespace TerraScale.Authentication.Features.Ui;

/// <summary>Cancel-link display state: the link's own user and token, or the outcome once posted.</summary>
public sealed record EmailChangeCancelPageModel(string User, string Token, string? Done, string Nonce, string? Locale = null)
{
    public string ResolvedLocale => Locale == AuthEmails.Portuguese ? AuthEmails.Portuguese : AuthEmails.English;

    public HostedPageCopy Copy => HostedCopies.For(this.ResolvedLocale);
}
