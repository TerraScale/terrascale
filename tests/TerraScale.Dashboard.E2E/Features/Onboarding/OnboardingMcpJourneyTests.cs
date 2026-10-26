using System.Net;
using System.Text.Json;
using TUnit.Core;

namespace TerraScale.Dashboard.E2E.Features.Onboarding;

[ClassDataSource<DashboardRootFixture>]
public sealed class OnboardingMcpJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task NativePatCompletesFreeOnboardingAcrossRunningReplicas()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var browser = scenario.CreateClient();
        using var replica = scenario.CreateClient(replica: true);
        using var login = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me");
        Check(login.StatusCode == HttpStatusCode.SeeOther, "The owning signed provider grant starts a native account");
        var cookie = SessionCookie(login);
        var original = await Identity(browser);
        Check(!original.GetProperty("onboardingCompleted").GetBoolean(), "The new provider subject has unfinished account setup");
        var secret = await HttpJourney.CreatePatAsync(browser, "Native MCP onboarding");
        var status = await Tool(replica, secret, "get_onboarding", new { });
        Progress(status, "account-information", 0);
        Check(status.GetProperty("plans").GetArrayLength() == 3 && status.GetProperty("plans").EnumerateArray().All(plan =>
            plan.GetProperty("priceCents").GetInt64() > 0 &&
            (plan.GetProperty("billingInterval").GetString() == "year"
                ? plan.GetProperty("priceCents").GetInt64() == 3000 && plan.GetProperty("monthlyEquivalentCents").GetInt64() == 250 && plan.GetProperty("price").GetString() == "$30.00 / year"
                : plan.GetProperty("priceCents").GetInt64() == plan.GetProperty("monthlyEquivalentCents").GetInt64() && plan.GetProperty("price").GetString()!.EndsWith(" / month", StringComparison.Ordinal))),
            "The payment step advertises only the three paid onboarding plans with only their published cadence");
        await SafeError(replica, secret, new { step = "project", name = "Premature", region = "GRU" }, "Complete the current setup step first.");
        await SafeError(replica, secret, new { step = "account-information", displayName = "\u0001" }, "Enter a valid name.");
        Progress(await Tool(replica, secret, "get_onboarding", new { }), "account-information", 0);

        var account = await Tool(replica, secret, "save_onboarding_step", new { step = "account-information", displayName = "Native MCP account" });
        Saved(account, "payment-method");
        var changed = await Identity(replica, cookie);
        Check(changed.GetProperty("name").GetString() == "Native MCP account" &&
            changed.GetProperty("accountId").GetString() == original.GetProperty("accountId").GetString() &&
            changed.GetProperty("userId").GetString() == original.GetProperty("userId").GetString(),
            "The second native HTTP replica rereads the saved name while preserving the immutable public account and provider subject");
        Progress(await Tool(browser, secret, "get_onboarding", new { }), "payment-method", 1);
        Saved(await Tool(replica, secret, "save_onboarding_step", new { step = "payment-method", continueOnFree = true }), "project");
        Progress(await Tool(browser, secret, "get_onboarding", new { }), "project", 2);
        await SafeError(replica, secret, new { step = "project", name = "Native MCP project", region = "foreign-region" }, "Choose a supported project region.");
        Progress(await Tool(replica, secret, "get_onboarding", new { }), "project", 2);
        var finished = await Tool(replica, secret, "save_onboarding_step", new { step = "project", name = "Native MCP project", region = "gru" });
        Check(finished.GetProperty("completed").GetBoolean() && finished.GetProperty("step").GetString() == "" &&
            !finished.TryGetProperty("checkoutUrl", out _), "The same production setup service completes the Personal project without a checkout");
        var completed = await Identity(browser);
        Check(completed.GetProperty("onboardingCompleted").GetBoolean(), "The original native browser reads setup completion persisted by the other replica");
        var context = await Context(browser);
        var project = context.GetProperty("organizations").EnumerateArray().Single(org =>
            org.GetProperty("id").GetString() == completed.GetProperty("organizationId").GetString()).GetProperty("projects").EnumerateObject().Select(item => item.Value).Single(item =>
            item.GetProperty("id").GetString() == original.GetProperty("projectId").GetString());
        Check(project.GetProperty("name").GetString() == "Native MCP project" && project.GetProperty("region").GetString() == "GRU" &&
            project.GetProperty("isDefault").GetBoolean(), "Project completion updates placement and name without inventing a new provider project identity");
        var final = await Tool(replica, secret, "get_onboarding", new { });
        Check(final.GetProperty("completed").GetBoolean() && final.GetProperty("step").GetString() == "" &&
            final.GetProperty("steps").EnumerateArray().All(step => step.GetProperty("completed").GetBoolean() && !step.GetProperty("current").GetBoolean()),
            "Every step is complete in the original source contract");
        var repeated = await Tool(replica, secret, "save_onboarding_step", new { step = "account-information", displayName = "Cannot replace completed setup" });
        Check(repeated.GetProperty("completed").GetBoolean() && (await Identity(browser)).GetProperty("name").GetString() == "Native MCP account",
            "Saving completed setup is a no-op, as in the original Go service");
    }

    [Test]
    public async Task PaidMcpOnboardingWaitsForNativeOwningStripeCheckout()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var browser = scenario.CreateClient();
        using var replica = scenario.CreateClient(replica: true);
        using var login = await HttpJourney.LoginAsync(browser, returnTo: "/api/v1/auth/me");
        Check(login.StatusCode == HttpStatusCode.SeeOther, "The paid journey starts with native provider authentication");
        var original = await Identity(browser);
        var secret = await HttpJourney.CreatePatAsync(browser, "Paid native MCP onboarding");
        Saved(await Tool(replica, secret, "save_onboarding_step", new { step = "account-information", displayName = "Paid MCP account" }), "payment-method");
        await SafeError(replica, secret, new { step = "payment-method", plan = "unpublished-plan", interval = "monthly" }, "Choose an available plan and billing interval.");
        var checkout = await Tool(replica, secret, "save_onboarding_step", new { step = "payment-method", plan = "starter", interval = "annual" });
        Check(!checkout.GetProperty("completed").GetBoolean() && checkout.GetProperty("step").GetString() == "payment-method",
            "Issuing a checkout does not advance the payment step");
        var url = new Uri(checkout.GetProperty("checkoutUrl").GetString()!);
        Check(url.AbsolutePath == "/__test/checkout" && url.Query.StartsWith("?id=cs_", StringComparison.Ordinal),
            "The official Stripe SDK returns the external test producer's owning checkout URL");
        using (var unconfirmed = await browser.GetAsync("/onboarding?payment=success"))
        {
            Check(unconfirmed.StatusCode == HttpStatusCode.OK && (await unconfirmed.Content.ReadAsStringAsync()).Contains("Payment setup could not be confirmed.", StringComparison.Ordinal),
                "A forged browser success hint cannot confirm an unfinished provider checkout");
        }
        Progress(await Tool(replica, secret, "get_onboarding", new { }), "payment-method", 1);
        using var paid = await browser.GetAsync(url);
        Check(paid.StatusCode == HttpStatusCode.Redirect && paid.Headers.Location?.OriginalString.StartsWith("/billing/checkout/return?session_id=", StringComparison.Ordinal) == true,
            "The test-only external checkout producer completes payment and returns the original bound native receipt");
        using var receipt = await browser.GetAsync(paid.Headers.Location);
        Check(receipt.StatusCode == HttpStatusCode.SeeOther && receipt.Headers.Location?.OriginalString.StartsWith("/onboarding?payment=success", StringComparison.Ordinal) == true,
            "The native receipt rereads official Stripe state, ownership and the onboarding return binding");
        using (var resumed = await browser.GetAsync(receipt.Headers.Location))
            Check(resumed.StatusCode == HttpStatusCode.OK && (await resumed.Content.ReadAsStringAsync()).Contains("Create your first project", StringComparison.Ordinal),
                "Only the confirmed native receipt opens the project step");
        Progress(await Tool(replica, secret, "get_onboarding", new { }), "project", 2);
        var finished = await Tool(replica, secret, "save_onboarding_step", new { step = "project", name = "Paid MCP project", region = "SFO" });
        Check(finished.GetProperty("completed").GetBoolean() && finished.GetProperty("step").GetString() == "", "The paid project completes through the same feature service");
        var completed = await Identity(browser);
        Check(completed.GetProperty("projectId").GetString() == original.GetProperty("projectId").GetString() && completed.GetProperty("onboardingCompleted").GetBoolean(),
            "Paid setup preserves the canonical provider project and is visible to the original browser");
    }

    private static async Task<JsonElement> Tool(HttpClient client, string secret, string name, object arguments)
    {
        var response = await HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments });
        var result = response.GetProperty("result");
        Check(!result.TryGetProperty("isError", out var error) || !error.GetBoolean(), "The authenticated app MCP call succeeds: " + name);
        return result.GetProperty("structuredContent").Clone();
    }

    private static async Task SafeError(HttpClient client, string secret, object arguments, string expected)
    {
        var response = await HttpJourney.McpAsync(client, secret, "tools/call", new { name = "save_onboarding_step", arguments });
        var result = response.GetProperty("result");
        Check(result.GetProperty("isError").GetBoolean() && result.GetProperty("content")[0].GetProperty("text").GetString() == expected,
            "The native service's bounded onboarding validation reaches MCP without vendor details: " + expected);
    }

    private static void Saved(JsonElement result, string next) => Check(!result.GetProperty("completed").GetBoolean() &&
        result.GetProperty("step").GetString() == next && !result.TryGetProperty("checkoutUrl", out _), "The saved setup step returns the source progress contract");
    private static void Progress(JsonElement result, string current, int index)
    {
        Check(!result.GetProperty("completed").GetBoolean() && result.GetProperty("step").GetString() == current, "The original progress contract identifies the current setup step");
        var steps = result.GetProperty("steps").EnumerateArray().ToArray();
        Check(steps.Length == 3 && steps[index].GetProperty("key").GetString() == current && steps[index].GetProperty("current").GetBoolean() &&
            steps[index].GetProperty("state").GetString() == "CURRENT" && steps.Take(index).All(step => step.GetProperty("completed").GetBoolean()) &&
            steps.Skip(index + 1).All(step => step.GetProperty("state").GetString() == "NOT STARTED"), "Step ordering and completed/current states match the native form");
    }
    private static Task<JsonElement> Context(HttpClient client) => Json(client, "/api/v1/dashboard/context");
    private static Task<JsonElement> Identity(HttpClient client, string? cookie = null) => Json(client, "/api/v1/auth/me", cookie);
    private static async Task<JsonElement> Json(HttpClient client, string path, string? cookie = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        using var response = await client.SendAsync(request);
        Check(response.StatusCode == HttpStatusCode.OK, "The running native app endpoint succeeds: " + path);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
    private static string SessionCookie(HttpResponseMessage response) => response.Headers.GetValues("Set-Cookie")
        .Single(value => value.StartsWith("ts-dashboard-session=", StringComparison.Ordinal)).Split(';')[0];
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
