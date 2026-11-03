using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E
{
    // Run the official pinned browser engine directly; no alternate JSON serializer is needed.
    internal static class BrowserAccessibility
    {
        private static readonly Lazy<string> Engine = new(() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Support", "axe-core", "axe.min.js")));

        public static async Task<AccessibilityResult> RunAsync(IPage page, string[]? tags = null, string? selector = null)
        {
            // A <script> tag would be inline script that the dashboard's nonce CSP correctly blocks. Evaluating the
            // engine through the DevTools protocol is outside page CSP, so the audit runs against the real policy.
            if (!await page.EvaluateAsync<bool>("() => typeof window.axe === 'object'"))
            {
                await page.EvaluateAsync(Engine.Value);
            }
            var result = await page.EvaluateAsync<JsonElement>("""
                async ({ tags, selector }) => {
                    const options = tags ? { runOnly: { type: 'tag', values: tags } } : {};
                    return await axe.run(selector || document, options);
                }
                """, new { tags, selector });
            return result.Deserialize(AccessibilityJsonContext.Default.AccessibilityResult)
                ?? throw new InvalidDataException("The accessibility engine returned no result.");
        }
    }

    internal sealed record AccessibilityResult(AccessibilityViolation[] Violations);
    internal sealed record AccessibilityViolation(string Id, string Help, string? Impact, AccessibilityNode[] Nodes);
    internal sealed record AccessibilityNode(JsonElement Target);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(AccessibilityResult))]
    internal sealed partial class AccessibilityJsonContext : JsonSerializerContext;
}
