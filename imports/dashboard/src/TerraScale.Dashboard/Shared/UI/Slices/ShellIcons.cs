using Microsoft.AspNetCore.Html;
namespace TerraScale.Dashboard.Shared.UI;

public static class ShellIcons
{
    public static HtmlString Svg(string name, int size = 16) => Icons.Render(name.Replace("-", ""), size);
}
