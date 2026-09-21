namespace TerraScale.Auth.Connections;

using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Billing;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.Auth.Users;
using TerraScale.DotNet;
using TerraScale.Endpoints;
using static TerraScale.Auth.Connections.ConnectionFlow;

internal sealed class GetConnectionsPageEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/ui/{slug}/connections/list", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, AuthStore store, CancellationToken ct)
    {
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null)
            return NotFoundPage();

        var enabled = (await store.ListConnectionsAsync(instance.Id, ct)).Where(c => c.Enabled).OrderBy(c => c.CreatedAt).ToList();
        var buttons = string.Join("\n", enabled.Select(c => $"""
            <form method="get" action="{LoginPath(slug, c)}">
            <button type="submit">Continue with {Ui.Esc(c.Name)}</button>
            </form>
            """));
        var body = enabled.Count == 0
            ? """<p class="muted">No sign-in options are configured.</p>"""
            : $"<div class=\"list\">\n{buttons}\n</div>";
        return Ui.Page("Sign in", $"""
            <h1>Sign in to {Ui.Esc(instance.Name)}</h1>
            {body}
            <p class="hint"><a href="/ui/{slug}/sign-in">Back to sign in</a></p>
            """);
    }
}
