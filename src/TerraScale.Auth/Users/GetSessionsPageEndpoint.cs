namespace TerraScale.Auth.Users;

using System.Net;
using TerraScale.Auth.Billing;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Email;
using TerraScale.Auth.Mfa;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.DotNet;
using TerraScale.Endpoints;
using static TerraScale.Auth.Users.UserFlow;

internal sealed class GetSessionsPageEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/ui/{slug}/account/sessions", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, HttpRequest request, AuthStore store, SessionService sessions, CancellationToken ct)
    {
        var instance = await PageInstanceAsync(store, slug, ct);
        if (instance is null)
            return NotFoundPage();

        if (await ResolvePageSessionAsync(request, sessions, instance.Id, ct) is not { } current)
            return RedirectToSignIn(slug, $"/ui/{slug}/account/sessions");

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var active = (await store.ListSessionsByUserAsync(current.User.Id, ct))
            .Where(s => s.Status == "active" && s.ExpiresAt > now)
            .OrderByDescending(s => s.LastActiveAt ?? s.CreatedAt)
            .ToList();
        return SessionsPage(slug, current.Session.Id, active);
    }

    private static IResult SessionsPage(string slug, string currentSessionId, IReadOnlyList<Session> sessions)
    {
        var rows = string.Join("\n", sessions.Select(session =>
        {
            var device = Ui.Esc(string.IsNullOrWhiteSpace(session.UserAgent) ? "Unknown device" : session.UserAgent);
            var current = session.Id == currentSessionId ? " · <strong>this device</strong>" : "";
            return $"""
                <div class="row">
                <div>
                <div>{device}</div>
                <div class="muted">created {Stamp(session.CreatedAt)} · last active {Stamp(session.LastActiveAt ?? session.CreatedAt)}{current}</div>
                </div>
                <form class="inline" method="post" action="/ui/{slug}/account/sessions/{session.Id}/revoke"><button type="submit" class="secondary">Revoke</button></form>
                </div>
                """;
        }));
        return Ui.Page("Active sessions", $"""
            <h1>Active sessions</h1>
            <div class="list">{rows}</div>
            <p class="hint"><a href="/ui/{slug}/account">Back to account</a></p>
            """);
    }

    private static string Stamp(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.ToString("yyyy-MM-dd HH:mm") + " UTC";
}
