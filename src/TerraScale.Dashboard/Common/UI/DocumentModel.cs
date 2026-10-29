namespace TerraScale.Dashboard.Common.UI
{
    public sealed record DocumentModel(string Title, string Nonce, string BodyClass = "", string Theme = "dark")
    {
        public static string NonceFor(HttpContext context)
        {
            return (string)context.Items["nonce"]!;
        }
    }
}
