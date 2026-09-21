using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.ApiKeys;

/// <summary>Creates a project API key. Demo mode mints the seeded secret; live
/// mode issues a REAL Terrabase credential through the platform control plane
/// (the secret is returned exactly once and shown to the user the same way).</summary>
public sealed class CreateApiKeyEndpoint(DashboardStore store, DashboardSecurityOptions security, TerrabaseDataPlane? dataPlane = null) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/credentials/key");
        Summary("Creates a project API key");
        Tags("ApiKeys");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var name = form["name"].ToString().Trim();
        var scopes = form["scopes"].ToString().Split([',', ' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var databaseId = form["databaseId"].ToString().Trim();

        if (name.Length == 0 || scopes.Length == 0)
        {
            return Results.Redirect("/api-keys?create=key&error=" + Uri.EscapeDataString("Enter at least one scope."), false, false);
        }

        if (!security.DemoMode)
        {
            try
            {
                var (keyId, secret) = await dataPlane!.IssueCredentialAsync(name, [.. scopes], databaseId.Length is 0 ? null : databaseId, ct);
                store.CreateIssuedApiKey(keyId, name, [.. scopes], databaseId.Length is 0 ? null : databaseId);
                store.SavePendingSecret(keyId, name, secret);
                return Results.Redirect($"/api-keys?created={keyId}&toast={Uri.EscapeDataString("API key created.")}", false, false);
            }
            catch (HttpRequestException exception)
            {
                return Results.Redirect("/api-keys?create=key&error=" + Uri.EscapeDataString($"TerraBase rejected the credential: {exception.Message}"), false, false);
            }
        }

        var demoSecret = store.CreateApiKey(name, [.. scopes], databaseId.Length is 0 ? null : databaseId);
        var created = store.ApiKeys.Last(); // CreateApiKey appends the new key
        store.SavePendingSecret(created.KeyId, created.Name, demoSecret);
        return Results.Redirect($"/api-keys?created={created.KeyId}&toast={Uri.EscapeDataString("API key created.")}", false, false);
    }
}
