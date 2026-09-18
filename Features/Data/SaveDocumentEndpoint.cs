using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Data;

/// <summary>Applies one document mutation (create/update/patch/delete) from the
/// document editor view, mirroring the DataWorkspace mutate flow.</summary>
public sealed class SaveDocumentEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/dashboard/data/documents");
        Summary("Applies a document create, replace, patch, or delete");
        Tags("Data");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var operation = form["operation"].ToString() is { Length: > 0 } op ? op : "create";
        var id = form["id"].ToString().Trim();
        var json = form["document"].ToString();
        var database = form["database"].ToString();
        var collection = form["collection"].ToString();

        if (database.Length == 0 || collection.Length == 0)
        {
            return Results.Redirect("/data?view=editor&error=" + Uri.EscapeDataString("Choose a database and collection first."), false, false);
        }

        if (operation != "create" && id.Length == 0)
        {
            return Results.Redirect("/data?view=editor&error=" + Uri.EscapeDataString("A document id is required."), false, false);
        }

        var error = store.MutateExplorerDocument(operation, id, json);
        if (error is not null)
        {
            return Results.Redirect("/data?view=editor&error=" + Uri.EscapeDataString(error), false, false);
        }

        return Results.Redirect($"/data?view=table&database={Uri.EscapeDataString(database)}&collection={Uri.EscapeDataString(collection)}&notice=" + Uri.EscapeDataString("Document operation completed."), false, false);
    }
}
