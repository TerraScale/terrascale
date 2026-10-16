namespace TerraScale.Dashboard.Shared.UI;

public sealed record DocumentModel(string Title, string Nonce, string BodyClass = "ts-auth-body")
{
    public static string NonceFor(HttpContext context) => (string)context.Items["nonce"]!;
}
