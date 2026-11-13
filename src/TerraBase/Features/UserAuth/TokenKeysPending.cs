namespace TerraBase.Features.UserAuth;

internal sealed class TokenKeysPending
{
    public const string Code = "auth_unavailable";

    public const string Message = "this node cannot obtain the signing keys needed to verify this token; retry";

    public static readonly TokenKeysPending Instance = new();

    private TokenKeysPending()
    {
    }

    public static bool On(HttpContext context) => context.Features.Get<TokenKeysPending>() is not null;
}
