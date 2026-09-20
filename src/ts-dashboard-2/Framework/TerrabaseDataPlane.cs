namespace TerraScale.Dashboard2;

#if TERRABASE_SDK
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
}
#endif
