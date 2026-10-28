namespace TerraScale.Authentication.Features.Ui;

/// <summary>Cancel-link display state: the link's own user and token, or the outcome once posted.</summary>
public sealed record EmailChangeCancelPageModel(string User, string Token, string? Done, string Nonce);
