using TerraScale.Dashboard.Features.Onboarding;
using TerraScale.Dashboard.Shared.Validation;

namespace TerraScale.Dashboard.Features.Databases;

public sealed class OnboardingProjectProvisioner(IWorkspaceGateway workspace, IDatabaseBackend backend) : IOnboardingProjectProvisioner
{
    public async Task<ProjectRecord> CompleteProjectAsync(AccountContext context, string name, string region, CancellationToken cancellationToken)
    {
        name = AccountService.ValidName(name);
        if (context.Organization is null || !context.CanManage || context.Organization.Kind != "personal")
            throw new AccountAccessException("Select your personal organization to complete onboarding.");
        var placement = (await backend.RegionsAsync(cancellationToken)).FirstOrDefault(item => item.Code == region && item.Healthy)
            ?? throw new OnboardingValidationException("Choose a serving project region.");
        var project = context.Organization.Projects.Values.FirstOrDefault(item => item.IsDefault)
            ?? throw new AccountAccessException("The personal project is unavailable.", 404);
        if (project.Name != name) await workspace.RenameProjectAsync(context.Session, context.Organization.Id, project.Id, name, cancellationToken);
        await backend.RegisterAsync(new(context.UserId, context.Session.AccessToken, context.Organization.Id, context.Organization.Name, project.Id, name), placement, cancellationToken);
        return new ProjectRecord
        {
            Id = project.Id,
            OrganizationId = project.OrganizationId,
            Name = name,
            Slug = AccountService.Slug(name),
            Region = placement.Code,
            BackupRegion = project.BackupRegion,
            Regions = [placement.Code],
            Status = project.Status,
            IsDefault = true,
            Databases = project.Databases,
            CreatedAt = project.CreatedAt
        };
    }
}
