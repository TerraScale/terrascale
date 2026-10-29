using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Onboarding;
using TerraScale.Dashboard.Shared.Validation;

namespace TerraScale.Dashboard.Features.Databases;

// The Personal project is ts-auth's; onboarding renames it there and records the
// chosen serving region as the dashboard's placement preference. TerraBase needs
// no project registration before the project's first database.
public sealed class OnboardingProjectProvisioner(ITsAuthPlatform platform, IDatabaseBackend backend) : IOnboardingProjectProvisioner
{
    public async Task<ProjectRecord> CompleteProjectAsync(AccountContext context, string name, string region, CancellationToken cancellationToken)
    {
        name = AccountService.ValidName(name);
        if (context.Organization is null || !context.CanManage || context.Organization.Kind != "personal")
            throw new AccountAccessException("Select your personal organization to complete onboarding.");
        var placement = (await backend.RegionsAsync(cancellationToken)).FirstOrDefault(item => item.Code == region && item.Healthy)
            ?? throw new OnboardingValidationException("Choose a supported project region.");
        var project = context.Organization.Projects.Values.FirstOrDefault(item => item.IsDefault)
            ?? throw new AccountAccessException("The personal project is unavailable.", 404);
        if (context.Project?.Id != project.Id)
            throw new OnboardingValidationException("Return to your Personal workspace and select its default project to finish setup.");
        if (project.Name != name) await platform.RenameProjectAsync(context.Session, context.Organization.Id, project.Id, name, cancellationToken);
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
