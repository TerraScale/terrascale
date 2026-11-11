namespace TerraScale.Dashboard.Common.UI;

public sealed record DialogError(string DialogId, string Message)
{
    public bool HasError => Message.Length > 0;
}
