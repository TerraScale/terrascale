using System.Net;
using System.Text.Json;

namespace TerraScale.Dashboard.E2E.Platform;

[ClassDataSource<DashboardE2EFixture>]
public sealed class McpOutputSchemaJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public async Task NullableBillingAndAuthenticationResultsSatisfyAdvertisedRequiredProperties()
    {
        using var client = fixture.CreateClient();
        using var login = await HttpJourney.LoginAsync(client);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var secret = await HttpJourney.CreatePatAsync(client, "MCP nullable output regression");
        var context = await HttpJourney.McpAsync(client, secret, "tools/call", new { name = "get_context", arguments = new { } });
        var organization = context.GetProperty("result").GetProperty("structuredContent").GetProperty("selection").GetProperty("organizationId").GetString()!;
        await fixture.PlanAsync(organization, OrganizationRecord.FreePlanId);
        var discovery = await HttpJourney.McpAsync(client, secret, "tools/list", new { });
        var schemas = discovery.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Where(tool => tool.TryGetProperty("outputSchema", out _))
            .ToDictionary(tool => tool.GetProperty("name").GetString()!, tool => tool.GetProperty("outputSchema"));

        var plans = await Call("list_plans", new { });
        await Assert.That(plans.GetProperty("currentPlanKey").GetString()).IsEqualTo("free");
        var billing = await Call("get_billing", new { });
        await Assert.That(billing.GetProperty("hasSubscription").GetBoolean()).IsFalse();
        await Assert.That(billing.TryGetProperty("interval", out _)).IsFalse();
        var users = await Call("auth_list_users", new { });
        await Assert.That(users.GetProperty("next").GetString()).IsNotNull();
        await Call("auth_list_users", new { after = users.GetProperty("next").GetString() });
        var empty = await Call("auth_list_users", new { search = "no-matching-user.invalid" });
        await Assert.That(empty.GetProperty("items").GetArrayLength()).IsEqualTo(0);
        await Call("auth_list_sessions", new { });
        var sessions = await Call("auth_list_sessions", new { userId = "00000000000000000000000001" });
        await Assert.That(sessions.GetProperty("items").GetArrayLength()).IsEqualTo(0);
        var cleared = await Call("auth_update_settings", new { name = "", logoUrl = "", themeColor = "" });
        await Assert.That(cleared.GetProperty("branding").TryGetProperty("name", out _)).IsFalse();
        await Call("auth_get_overview", new { });

        // The fix must retain non-nullable requirements at the root and in nested objects.
        await Assert.That(Required(schemas["get_billing"]).Contains("organizationId")).IsTrue();
        await Assert.That(Required(schemas["get_billing"]).Contains("canManage")).IsTrue();
        await Assert.That(Required(schemas["auth_list_users"]).Contains("items")).IsTrue();
        var settings = schemas["auth_update_settings"].GetProperty("properties");
        await Assert.That(Required(settings.GetProperty("methods")).Contains("passcode")).IsTrue();
        await Assert.That(Required(schemas["auth_update_settings"]).Contains("branding")).IsTrue();
        await Assert.That(settings.GetProperty("branding").GetProperty("properties").TryGetProperty("name", out _)).IsTrue();

        async Task<JsonElement> Call(string name, object arguments)
        {
            var response = await HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments });
            var result = response.GetProperty("result");
            await Assert.That(result.TryGetProperty("isError", out var error) && error.GetBoolean()).IsFalse().Because(name);
            var value = result.GetProperty("structuredContent");
            await AssertRequired(schemas[name], value, name);
            return value;
        }
    }

    private static IEnumerable<string> Required(JsonElement schema) => schema.TryGetProperty("required", out var required)
        ? required.EnumerateArray().Select(field => field.GetString()!) : [];

    // Check the exact interoperability failure recursively on real HTTP results, including array items.
    private static async Task AssertRequired(JsonElement schema, JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var field in Required(schema))
                await Assert.That(value.TryGetProperty(field, out _)).IsTrue().Because(path + "." + field + " is advertised as required");
            if (schema.TryGetProperty("properties", out var properties))
                foreach (var property in properties.EnumerateObject())
                    if (value.TryGetProperty(property.Name, out var child)) await AssertRequired(property.Value, child, path + "." + property.Name);
        }
        else if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
            foreach (var item in value.EnumerateArray()) await AssertRequired(items, item, path + "[]");
    }
}
