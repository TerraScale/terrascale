using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.ApiKeys;

public sealed class CreateApiKeyEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
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
            return Results.Redirect("/api-keys?create=key&error=" + Uri.EscapeDataString("Enter a name and at least one scope."), false, false);
        }

        var secret = store.CreateApiKey(name, [.. scopes], databaseId.Length is 0 ? null : databaseId);
        var created = store.ApiKeys.Last(); // CreateApiKey appends the new key
        store.SavePendingSecret(created.KeyId, created.Name, secret);
        return Results.Redirect($"/api-keys?created={created.KeyId}&toast={Uri.EscapeDataString("API key created.")}", false, false);
    }
}
