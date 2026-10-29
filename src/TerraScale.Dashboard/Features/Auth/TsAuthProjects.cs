using TerraScale.DotNet;

namespace TerraScale.Dashboard.Features.Auth;

// A project's own end-user tenant at ts-auth: settings, users, sessions and OAuth clients.
// Every call carries the signed-in user's own token; ts-auth checks the project role each time.
public interface ITsAuthProjects
{
    Task<TerraScaleAuthProjectSettings> GetSettingsAsync(SessionRecord session, string projectId, CancellationToken cancellationToken);
    Task<TerraScaleAuthProjectSettings> UpdateSettingsAsync(SessionRecord session, string projectId, TerraScaleAuthProjectSettingsUpdate update, CancellationToken cancellationToken);
    // The update carries the provider's client secret; never log or store it.
    Task<TerraScaleAuthProjectSettings> SetProviderAsync(SessionRecord session, string projectId, string provider, TerraScaleAuthProjectProviderUpdate update, CancellationToken cancellationToken);
    Task<TerraScaleAuthProjectSettings> DeleteProviderAsync(SessionRecord session, string projectId, string provider, CancellationToken cancellationToken);
    Task<TerraScaleAuthPlatformPage<TerraScaleAuthProjectUser>> ListUsersAsync(SessionRecord session, string projectId, string? query, string? after, CancellationToken cancellationToken);
    Task<TerraScaleAuthUserExportPage> ExportUsersAsync(SessionRecord session, string projectId, string? after, CancellationToken cancellationToken);
    Task<TerraScaleAuthProjectUser> SetUserDisabledAsync(SessionRecord session, string projectId, string userId, bool disabled, CancellationToken cancellationToken);
    Task DeleteUserAsync(SessionRecord session, string projectId, string userId, CancellationToken cancellationToken);
    Task<TerraScaleAuthPlatformPage<TerraScaleAuthProjectSession>> ListSessionsAsync(SessionRecord session, string projectId, string? userId, string? after, CancellationToken cancellationToken);
    Task RevokeSessionAsync(SessionRecord session, string projectId, string sessionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TerraScaleAuthProjectOAuthClient>> ListClientsAsync(SessionRecord session, string projectId, CancellationToken cancellationToken);
    // A confidential client's secret is in this answer only; never log or store it.
    Task<TerraScaleAuthProjectOAuthClient> CreateClientAsync(SessionRecord session, string projectId, TerraScaleAuthProjectCreateOAuthClient request, string proof, CancellationToken cancellationToken);
    Task DeleteClientAsync(SessionRecord session, string projectId, string clientId, CancellationToken cancellationToken);
}

public sealed partial class TsAuthGateway : ITsAuthProjects
{
    public const int ProjectPageSize = 25;

    public Task<TerraScaleAuthProjectSettings> GetSettingsAsync(SessionRecord session, string projectId, CancellationToken cancellationToken) =>
        ProjectAsync(() => Client().Projects.GetSettingsAsync(Bearer(session), Id(projectId), cancellationToken));

    public Task<TerraScaleAuthProjectSettings> UpdateSettingsAsync(SessionRecord session, string projectId, TerraScaleAuthProjectSettingsUpdate update, CancellationToken cancellationToken) =>
        ProjectAsync(() => Client().Projects.UpdateSettingsAsync(Bearer(session), Id(projectId), update, cancellationToken));

    public Task<TerraScaleAuthProjectSettings> SetProviderAsync(SessionRecord session, string projectId, string provider, TerraScaleAuthProjectProviderUpdate update, CancellationToken cancellationToken) =>
        ProjectAsync(() => Client().Projects.SetProviderAsync(Bearer(session), Id(projectId), provider, update, cancellationToken));

    public Task<TerraScaleAuthProjectSettings> DeleteProviderAsync(SessionRecord session, string projectId, string provider, CancellationToken cancellationToken) =>
        ProjectAsync(() => Client().Projects.DeleteProviderAsync(Bearer(session), Id(projectId), provider, cancellationToken));

    public Task<TerraScaleAuthPlatformPage<TerraScaleAuthProjectUser>> ListUsersAsync(SessionRecord session, string projectId, string? query, string? after, CancellationToken cancellationToken) =>
        ProjectAsync(() => Client().Projects.ListUsersAsync(Bearer(session), Id(projectId), Blank(query), ProjectPageSize, Blank(after), cancellationToken));

    public Task<TerraScaleAuthProjectUser> SetUserDisabledAsync(SessionRecord session, string projectId, string userId, bool disabled, CancellationToken cancellationToken) =>
        ProjectAsync(() => disabled
            ? Client().Projects.DisableUserAsync(Bearer(session), Id(projectId), Id(userId), cancellationToken)
            : Client().Projects.EnableUserAsync(Bearer(session), Id(projectId), Id(userId), cancellationToken));

    public Task<TerraScaleAuthUserExportPage> ExportUsersAsync(SessionRecord session, string projectId, string? after, CancellationToken cancellationToken) =>
        ProjectAsync(() => Client().Projects.ExportUsersAsync(Bearer(session), Id(projectId), 100, after, cancellationToken));

    public Task DeleteUserAsync(SessionRecord session, string projectId, string userId, CancellationToken cancellationToken) =>
        ProjectAsync(async () => { await Client().Projects.DeleteUserAsync(Bearer(session), Id(projectId), Id(userId), cancellationToken); return true; });

    public Task<TerraScaleAuthPlatformPage<TerraScaleAuthProjectSession>> ListSessionsAsync(SessionRecord session, string projectId, string? userId, string? after, CancellationToken cancellationToken) =>
        ProjectAsync(() => Client().Projects.ListSessionsAsync(Bearer(session), Id(projectId), Blank(userId) is { } user ? Id(user) : null, ProjectPageSize, Blank(after), cancellationToken));

    public Task RevokeSessionAsync(SessionRecord session, string projectId, string sessionId, CancellationToken cancellationToken) =>
        ProjectAsync(async () => { await Client().Projects.RevokeSessionAsync(Bearer(session), Id(projectId), Id(sessionId), cancellationToken); return true; });

    public async Task<IReadOnlyList<TerraScaleAuthProjectOAuthClient>> ListClientsAsync(SessionRecord session, string projectId, CancellationToken cancellationToken) =>
        (await ProjectAsync(() => Client().Projects.ListClientsAsync(Bearer(session), Id(projectId), cancellationToken))).Items ?? throw new IOException("ts-auth returned an invalid page.");

    public Task<TerraScaleAuthProjectOAuthClient> CreateClientAsync(SessionRecord session, string projectId, TerraScaleAuthProjectCreateOAuthClient request, string proof, CancellationToken cancellationToken) =>
        string.IsNullOrEmpty(proof) ? throw StepUpFlow.Required() :
        ProjectAsync(() => Client().Projects.CreateClientAsync(Bearer(session), Id(projectId), request, proof, cancellationToken));

    public Task DeleteClientAsync(SessionRecord session, string projectId, string clientId, CancellationToken cancellationToken) =>
        ProjectAsync(async () => { await Client().Projects.DeleteClientAsync(Bearer(session), Id(projectId), Id(clientId), cancellationToken); return true; });

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ts-auth refusals become account errors with a fixed dashboard message; server and transport failures stay
    // HttpRequestException so the console reports ts-auth as unreachable.
    private static async Task<T> ProjectAsync<T>(Func<Task<T>> call)
    {
        try { return await call(); }
        catch (TerraScaleAuthException error) when ((int?)error.StatusCode is >= 400 and < 500)
        {
            if (error.ErrorCode == "step_up_required") throw StepUpFlow.Required();
            var status = (int)error.StatusCode!;
            throw new AccountAccessException(status switch
            {
                400 => "ts-auth rejected these values. Check them and try again.",
                401 => "Sign in again to continue.",
                403 => "Your project role does not allow this change.",
                404 => "Authentication for this project, or the requested record, is unavailable.",
                409 => "The change conflicts with the current state.",
                429 => "Too many requests. Try again shortly.",
                _ => "The request was rejected."
            }, status == 400 ? StatusCodes.Status422UnprocessableEntity : status, error.ErrorCode ?? "");
        }
    }
}
