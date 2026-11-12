using System.Text.Encodings.Web;
using System.Text.Json;

namespace TerraScale.Dashboard.Common.UI
{
    public sealed record McpClientConfig(string Id)
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public string Json { get; } = JsonSerializer.Serialize(new { mcpServers = new { terrascale = new { type = "http", url = "https://mcp.terrascale.tech/", headers = new { Authorization = "Bearer <your-access-token>" } } } }, JsonOptions);
    }
}
