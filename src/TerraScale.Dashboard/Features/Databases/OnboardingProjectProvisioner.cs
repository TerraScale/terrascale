using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Onboarding;
using TerraScale.Dashboard.Common.Validation;

namespace TerraScale.Dashboard.Features.Databases
{
    public sealed class OnboardingProjectProvisioner(ITsAuthPlatform platform, IDatabaseBackend backend) : IOnboardingProjectProvisioner
    {
        public async Task<ProjectRecord> CompleteProjectAsync(AccountContext context, string name, IReadOnlyList<string> regions, CancellationToken cancellationToken)
        {
            name = AccountService.ValidName(name);
            if (context.Organization is null || !context.CanManage || context.Organization.Kind != "personal")
            {
                throw new AccountAccessException("Select your personal organization to complete onboarding.");
            }

            var healthy = (await backend.RegionsAsync(cancellationToken)).Where(item => item.Healthy).ToArray();
            string[] placement = [.. regions.Select(region => Array.Find(healthy, item => item.Code.Equals(region, StringComparison.OrdinalIgnoreCase))?.Code
                ?? throw new OnboardingValidationException("Choose supported project regions."))];
            var project = context.Organization.Projects.Values.FirstOrDefault(item => item.IsDefault)
                ?? throw new AccountAccessException("The personal project is unavailable.", 404);
            if (context.Project?.Id != project.Id)
            {
                throw new OnboardingValidationException("Return to your Personal workspace and select its default project to finish setup.");
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
                Regions = [.. placement],
                Status = project.Status,
                IsDefault = true,
                Databases = project.Databases,
                CreatedAt = project.CreatedAt
            };
        }
    }
}
