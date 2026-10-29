using Microsoft.AspNetCore.Html;
namespace TerraScale.Dashboard.Common.UI
{
    public static class ShellIcons
    {
        public static HtmlString Svg(string name, int size = 16)
        {
            return Icons.Render(name.Replace("-", ""), size);
        }
    }
}
