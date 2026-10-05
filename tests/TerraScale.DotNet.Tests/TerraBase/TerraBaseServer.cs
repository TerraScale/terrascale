using TerraScale.DotNet.Testing;
using TerraScale.DotNet.TerraBase;
using TUnit.Core.Interfaces;

namespace TerraScale.DotNet.Tests.TerraBase;

/// <summary>
/// The TUnit session fixture over <see cref="TerraBaseTestServer"/>: the pinned real TerraBase release, downloaded and
/// cached on first use. Tests are skipped when the binary is unavailable locally, and fail under <c>CI=true</c>.
/// </summary>
public sealed class TerraBaseServer : IAsyncInitializer, IAsyncDisposable
{
    private TerraBaseTestServer? server;
    private string? unavailable;

    public TerraBaseTestServer Running => server!;

    public async Task InitializeAsync()
    {
        (server, unavailable) = await TerraBaseTestServer.StartAsync();
        VerifyCiAvailability(server is not null, unavailable, Environment.GetEnvironmentVariable("CI") == "true");
    }

    internal static void VerifyCiAvailability(bool available, string? unavailable, bool isCi)
    {
        if (!available && isCi)
        {
            throw new InvalidOperationException($"CI must run the TerraBase integration tests: {unavailable}.");
        }
    }

    /// <summary>Skips the calling test when no TerraBase binary is available here.</summary>
    public void Require() => Skip.When(server is null, unavailable ?? string.Empty);

    public TerraBaseManagementClient Management(string? credential = null) => Running.Management(credential);

    public TerraBaseDocumentsClient Documents(string apiKey) => Running.Documents(apiKey);

    public Task<string> CreateApiKeyAsync(string organizationId, params string[] scopes) =>
        Running.CreateApiKeyAsync(organizationId, scopes.Length == 0 ? null : scopes);

    public Task<TerraBaseTestTenant> CreateTenantAsync() => Running.CreateTenantAsync();

    public async ValueTask DisposeAsync()
    {
        if (server is not null)
        {
            await server.DisposeAsync();
        }
    }
}
