namespace TerraScale.Dashboard;

#if TERRABASE_SDK
using System.Text;
using System.Text.Json;
using TerraScale.DotNet;

/// <summary>
/// Live data plane: dashboard databases are provisioned for real in TerraBase
/// through the TerraScale SDK. Shares the state adapter's Terrabase endpoint
/// and API key (the local dev member serves one region, so live creates are
/// placed in GRU / br-only — widen here when the platform grows regions).
/// </summary>
public sealed class TerrabaseDataPlane(DashboardSecurityOptions security)
{
    private readonly TerraScaleClient _client = new(
        new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            BaseAddress = security.State!.ApiUrl,
            Timeout = TimeSpan.FromSeconds(10),
        },
        new TerraScaleClientOptions { BaseAddress = security.State.ApiUrl, ApiKey = security.State.ApiKey, MaxRetryAttempts = 2 });

    /// <summary>Provisions a real Terrabase database and returns its name.</summary>
    public async Task<string> CreateDatabaseAsync(string name, string operationId, CancellationToken ct)
    {
        var result = await _client.Databases.CreateAsync(name, [TerraScaleRegions.Gru], operationId, "br-only", ct);
        return result switch
        {
            TerraScaleDatabaseCreated created => created.Database.Name,
            TerraScaleDatabaseAccepted accepted => accepted.Operation.Name,
            _ => name,
        };
    }

    /// <summary>Issues a real Terrabase credential for the project through the
    /// platform control plane; the secret is returned exactly once.</summary>
    public async Task<(string KeyId, string Secret)> IssueCredentialAsync(string name, string[] scopes, string? database, CancellationToken ct)
    {
        using var http = CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(security.State!.ApiUrl, "/api/v1/credentials"))
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                name,
                scopes,
                database,
            }), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {security.State.ApiKey}");
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: ct);
        return (document.RootElement.GetProperty("id").GetString()!, document.RootElement.GetProperty("secret").GetString()!);
    }

    private HttpClient CreateHttpClient() =>
        new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>True when the database exists and is not deleted.</summary>
    public async Task<bool> DatabaseExistsAsync(string name, CancellationToken ct)
    {
        try
        {
            var record = await _client.Databases.GetAsync(name, ct);
            return record is not { Deleted: true };
        }
        catch (TerraScaleApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}
#else
/// <summary>Stub without the sibling ts-dotnet checkout; live mode requires it.</summary>
public sealed class TerrabaseDataPlane
{
    public Task<string> CreateDatabaseAsync(string name, string operationId, CancellationToken ct) =>
        throw new InvalidOperationException("The live dashboard requires the TerraScale SDK data plane.");

    public Task<(string KeyId, string Secret)> IssueCredentialAsync(string name, string[] scopes, string? database, CancellationToken ct) =>
        throw new InvalidOperationException("The live dashboard requires the TerraScale SDK data plane.");
}
#endif
