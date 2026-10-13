using System.Text;

namespace TerraScale.Dashboard.UI;

public sealed record ShellModel(AccountContext Context, string Title, string Nonce, string CsrfToken, string ReturnUrl)
{
    public static ShellModel For(HttpContext http, AccountContext context, string title) =>
        new(context, title, DocumentModel.NonceFor(http), Csrf.Token(http), http.Request.Path + http.Request.QueryString);
    public int Unread => Context.Account.Notifications.Count(item => item.VisibleTo(Context) && item.Unread);
    public string Initials => string.Concat(Context.Account.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(word => word[0])).ToUpperInvariant();
    public static string Initial(string value) => value.Length == 0 ? "?" : char.ToUpperInvariant(value[0]).ToString();
    public static string Tone(string value)
    {
        string[] tones = ["violet", "blue", "cyan", "teal", "amber", "pink", "emerald", "orange", "fuchsia", "indigo"];
        if (value.Length == 0) return "slate";
        uint hash = 2166136261;
        foreach (var item in Encoding.UTF8.GetBytes(value)) hash = unchecked((hash ^ item) * 16777619);
        return tones[hash % tones.Length];
    }
    public static string Count(int value) => value > 99 ? "99+" : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string NotificationsLabel => "Notifications" + (Unread > 0 ? ", " + Count(Unread) + " unread" : "");
}
