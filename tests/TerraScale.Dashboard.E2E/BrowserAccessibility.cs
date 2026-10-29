using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Playwright;

namespace TerraScale.Dashboard.E2E
{
    // Run the official pinned browser engine directly; no alternate JSON serializer is needed.
    internal static class BrowserAccessibility
    {
        public static async Task<AccessibilityResult> RunAsync(IPage page, string[]? tags = null, string? selector = null)
        {
            await page.AddScriptTagAsync(new() { Path = Path.Combine(AppContext.BaseDirectory, "Support", "axe-core", "axe.min.js") });
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
