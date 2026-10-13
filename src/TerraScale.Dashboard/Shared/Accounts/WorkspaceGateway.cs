using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Shared.Accounts;

public interface IWorkspaceGateway
{
    Task<OrganizationRecord> EnsurePersonalAsync(SessionRecord session, CancellationToken cancellationToken);
    Task<OrganizationRecord> CreateOrganizationAsync(SessionRecord session, string name, CancellationToken cancellationToken);
    Task<ProjectRecord> CreateProjectAsync(SessionRecord session, string organizationId, string name, CancellationToken cancellationToken);
    Task<string> RoleAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken);
    Task RenameOrganizationAsync(SessionRecord session, string organizationId, string name, CancellationToken cancellationToken);
    Task RenameProjectAsync(SessionRecord session, string organizationId, string projectId, string name, CancellationToken cancellationToken);
}

public sealed class TsWorkspaceGateway(IHttpClientFactory http) : IWorkspaceGateway
{
    private TerraBaseManagementClient Client(SessionRecord session)
    {
        var client = http.CreateClient("platform");
        return new(client, new TerraBaseManagementClientOptions
        {
            EndpointResolver = new TerraBaseDiscovery(),
            AccessTokenProvider = _ => ValueTask.FromResult(session.AccessToken)
        });
    }

    public Task<OrganizationRecord> EnsurePersonalAsync(SessionRecord session, CancellationToken cancellationToken) => EnsurePersonalAsync(session, cancellationToken, 0);

    private async Task<OrganizationRecord> EnsurePersonalAsync(SessionRecord session, CancellationToken cancellationToken, int attempt)
    {
        var client = Client(session);
        TerraBaseOrganization? remote = null;
        string? after = null;
        do
        {
            var page = await client.ListOrganizationsAsync(session.UserId, after, 100, cancellationToken);
            remote = page.Items.FirstOrDefault(org => org.Name == "Personal" && org.CreatedBy == session.UserId);
            after = page.Next;
        } while (remote is null && after is not null);
        if (remote is null)
        {
            try { remote = await client.CreateOrganizationAsync(session.UserId, "Personal", cancellationToken); }
            catch (TerraBaseConflictException) when (attempt < 2)
            {
                // A concurrent enrollment may have committed its organization before ours.
                return await EnsurePersonalAsync(session, cancellationToken, attempt + 1);
            }
        }
        var result = Organization(remote, session);
        TerraBaseProject? project = null;
        after = null;
        do
        {
            var page = await client.ListProjectsAsync(session.UserId, remote.Id, after, 100, cancellationToken);
            project = page.Items.FirstOrDefault(item => item.Name == "Personal");
            after = page.Next;
        } while (project is null && after is not null);
        if (project is null)
        {
            try { project = await client.CreateProjectAsync(session.UserId, remote.Id, "Personal", cancellationToken); }
            catch (TerraBaseConflictException) when (attempt < 2) { return await EnsurePersonalAsync(session, cancellationToken, attempt + 1); }
        }
        var personal = Project(project);
        personal.IsDefault = true;
        result.Projects[personal.Id] = personal;
        return result;
    }

    public async Task<OrganizationRecord> CreateOrganizationAsync(SessionRecord session, string name, CancellationToken cancellationToken)
        => Organization(await Client(session).CreateOrganizationAsync(session.UserId, name, cancellationToken), session);

    public async Task<ProjectRecord> CreateProjectAsync(SessionRecord session, string organizationId, string name, CancellationToken cancellationToken)
    {
        var client = Client(session);
        try { return Project(await client.CreateProjectAsync(session.UserId, organizationId, name, cancellationToken)); }
        catch (TerraBaseConflictException)
        {
            // The producer guarantees unique project names within an
            // organization. A prior create may have succeeded before regional
            // registration or dashboard CAS completed. Resolve that same real
            // resource through the caller's authenticated management API.
            TerraBaseProject? found = null;
            string? after = null;
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                var page = await client.ListProjectsAsync(session.UserId, organizationId, after, 100, cancellationToken);
                foreach (var candidate in page.Items.Where(project => project.Name == name))
                {
                    if (candidate.Id.Length == 0 || candidate.OrganizationId != organizationId || found is not null)
                        throw new IOException("Existing project identity is ambiguous.");
                    found = candidate;
                }
                after = page.Next;
                if (!string.IsNullOrEmpty(after) && !cursors.Add(after)) throw new IOException("Project pagination did not advance.");
            } while (!string.IsNullOrEmpty(after));
            if (found is null) throw;
            return Project(found);
        }
    }

    public async Task<string> RoleAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken)
    {
        var client = Client(session);
        try
        {
            string? after = null;
            do
            {
                var page = await client.ListMembersAsync(session.UserId, organizationId, after, 100, cancellationToken);
                var member = page.Items.FirstOrDefault(item => item.UserId == session.UserId);
                if (member is not null) return member.Role.ToString().ToLowerInvariant();
                after = page.Next;
            } while (after is not null);
            return "";
        }
        catch (TerraBaseNotFoundException) { return ""; }
        catch (TerraBaseForbiddenException) { return ""; }
    }

    public async Task RenameOrganizationAsync(SessionRecord session, string organizationId, string name, CancellationToken cancellationToken)
        => _ = await Client(session).RenameOrganizationAsync(session.UserId, organizationId, name, cancellationToken);

    public async Task RenameProjectAsync(SessionRecord session, string organizationId, string projectId, string name, CancellationToken cancellationToken)
        => _ = await Client(session).RenameProjectAsync(session.UserId, organizationId, projectId, name, cancellationToken);

    private static OrganizationRecord Organization(TerraBaseOrganization remote, SessionRecord session) => new()
    {
        Id = remote.Id,
        Name = remote.Name,
        Slug = AccountService.Slug(remote.Name),
        CreatedAt = remote.CreatedAt,
        OwnerUserId = remote.CreatedBy,
        Members = new() { [session.UserId] = new() { UserId = session.UserId, Email = session.Email, Name = session.Name, Role = "owner", CreatedAt = remote.CreatedAt } }
    };

    private static ProjectRecord Project(TerraBaseProject remote) => new()
    {
        Id = remote.Id,
        OrganizationId = remote.OrganizationId,
        Name = remote.Name,
        Slug = AccountService.Slug(remote.Name),
        CreatedAt = remote.CreatedAt
    };
}
