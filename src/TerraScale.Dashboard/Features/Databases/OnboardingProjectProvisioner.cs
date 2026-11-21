using TerraScale.Dashboard.Errors;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Onboarding;

namespace TerraScale.Dashboard.Features.Databases
{
    using NotFound = TerraScale.Dashboard.Errors.NotFound;

    // The Personal project is ts-auth's; onboarding renames it there and records the
    // chosen serving region as the dashboard's placement preference. TerraBase needs
    // no project registration before the project's first database.
    public sealed class OnboardingProjectProvisioner(ITsAuthPlatform platform, IDatabaseBackend backend) : IOnboardingProjectProvisioner
    {
        public async Task<CompletedProject> CompleteProjectAsync(AccountContext context, string name, string region, CancellationToken cancellationToken)
        {
            name = AccountService.ValidName(name);
            if (context.Organization is null || !context.CanManage || context.Organization.Kind != "personal")
            {
                return new Forbidden("Select your personal organization to complete onboarding.");
            }

            var placement = (await backend.RegionsAsync(cancellationToken)).FirstOrDefault(item => item.Code == region && item.Healthy);
            if (placement is null)
            {
                return new Validation("Choose a supported project region.");
            }

            var project = context.Organization.Projects.Values.FirstOrDefault(item => item.IsDefault);
            if (project is null)
            {
                return new NotFound("The personal project is unavailable.");
            }

            if (context.Project?.Id != project.Id)
            {
                return new Validation("Return to your Personal workspace and select its default project to finish setup.");
            }

            if (project.Name != name)
            {
                await platform.RenameProjectAsync(context.Session, context.Organization.Id, project.Id, name, cancellationToken);
            }

            return new ProjectRecord
            {
                Id = project.Id,
                OrganizationId = project.OrganizationId,
                Name = name,
                Slug = AccountService.Slug(name),
                Regions = [placement.Code],
                Status = project.Status,
                IsDefault = true,
                Databases = project.Databases,
                CreatedAt = project.CreatedAt
            };
        }
    }
}
