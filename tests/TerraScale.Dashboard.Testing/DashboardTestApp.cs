using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Testing;

/// <summary>
/// The dashboard test host: boots the real endpoint pipeline in-memory
/// (demo mode, e2e shims on) and drives it with a cookie-handling client.
/// This is to Razor Slices dashboards what BUnit is to Blazor — the whole
/// app, no sockets, deterministic, milliseconds per page.
/// </summary>
public sealed class DashboardTestApp : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    public DashboardTestApp()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("DASHBOARD_E2E", "1");
        });
    }

    public HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
            BaseAddress = new Uri("http://localhost/"),
        });

    /// <summary>A client already signed in as the demo account (mario).</summary>
    public HttpClient CreateDemoClient()
    {
        var client = CreateClient();
        SignInDemoAsync(client).GetAwaiter().GetResult();
        return client;
    }

    public async Task SignInDemoAsync(HttpClient client)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = "mario@terrascale.tech",
            ["password"] = "terrascale",
        });
        using var response = await client.PostAsync("/api/v1/auth/password/login", form);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Redirect
            && response.StatusCode != HttpStatusCode.Found && response.StatusCode != HttpStatusCode.RedirectMethod)
        {
            throw new InvalidOperationException($"Demo sign-in failed: {(int)response.StatusCode}");
        }
    }

    /// <summary>Registers a fresh account through the onboarding form and returns
    /// the sign-in result (the session cookie lands in the client's jar).</summary>
    public async Task<HttpResponseMessage> RegisterAsync(HttpClient client, string email, string password, string displayName)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["displayName"] = displayName,
            ["email"] = email,
            ["password"] = password,
            ["confirmPassword"] = password,
            ["terms"] = "true",
        });
        return await client.PostAsync("/onboarding/register", form);
    }

    /// <summary>Walks the wizard after registration: optional payment bypass,
    /// then project — the same journey the browser makes.</summary>
    public async Task CompleteOnboardingAsync(HttpClient client)
    {
        await PostFormAsync(client, "/onboarding/step", new Dictionary<string, string> { ["step"] = "payment-method", ["bypass"] = "1" });
        await PostFormAsync(client, "/onboarding/step", new Dictionary<string, string> { ["step"] = "project", ["name"] = "Personal", ["region"] = "GRU" });
    }

    /// <summary>Registers + onboards + returns the signed-in client.</summary>
    public async Task<HttpClient> CreateMemberClientAsync(string email, string password = "Member-tests-123!", string displayName = "Member Tests")
    {
        var client = CreateClient();
        await RegisterAsync(client, email, password, displayName);
        await CompleteOnboardingAsync(client);
        return client;
    }

    public static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string path, IReadOnlyDictionary<string, string> fields)
    {
        using var form = new FormUrlEncodedContent(fields);
        return await client.PostAsync(path, form);
    }

    /// <summary>Fetches a page and parses it for HTML assertions.</summary>
    public async Task<HtmlPage> GetPageAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();
        return HtmlPage.Capture((int)response.StatusCode, html);
    }

    public void Dispose() => _factory.Dispose();
}
