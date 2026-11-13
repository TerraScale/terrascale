namespace TerraBase.Features.UserAuth;

internal sealed class TokenKeysPending
{
    public const string Code = "auth_unavailable";

    public const string Message = "this node has not loaded the keys that verify access tokens yet; retry";

    public static readonly TokenKeysPending Instance = new();

    private TokenKeysPending()
    {
    }

    public static bool On(HttpContext context) => context.Features.Get<TokenKeysPending>() is not null;
}
