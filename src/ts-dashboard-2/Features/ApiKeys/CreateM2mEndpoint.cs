using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.ApiKeys;

public sealed class CreateM2mEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/credentials/m2m");
        Summary("Creates a machine-to-machine OAuth client");
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
            return Results.Redirect("/api-keys?create=m2m&error=" + Uri.EscapeDataString("Enter a name and at least one scope."), false, false);
        }

        var secret = store.CreateM2mCredential(name, [.. scopes], databaseId, null);
        var created = store.M2mCredentials.Last(); // CreateM2mCredential appends the new client
        store.SavePendingSecret(created.ClientId, created.Name, secret);
        return Results.Redirect($"/api-keys?created={created.ClientId}&toast={Uri.EscapeDataString("M2M client created.")}", false, false);
    }
}
