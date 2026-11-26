using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Errors;

namespace TerraScale.Dashboard.Features.Onboarding
{
    public readonly union OnboardingSaveResult(string, DashboardValidation, DashboardForbidden);

    public readonly union DisplayNameInput(string, DashboardValidation);

    internal readonly union PersonalWorkspace(AccountContext, DashboardValidation);

    public sealed class OnboardingService(AccountService accounts, StripeBilling payments, IOnboardingProjectProvisioner projects, DatabaseService databases)
    {
        public async Task<IReadOnlyList<BillingPlan>> PlansAsync(CancellationToken cancellationToken)
        {
            return [.. (await payments.PlansAsync(cancellationToken)).OrderBy(plan => plan.PriceCents == 0 ? 0 : 1)
                .ThenBy(plan => plan.PriceCents)
                .ThenBy(plan => plan.Key, StringComparer.Ordinal)];
        }

        public async Task ConfirmPaymentAsync(SessionRecord session, CancellationToken cancellationToken)
        {
            var context = await accounts.GetContextAsync(session, cancellationToken);
            if (context.Account.OnboardingStep != "payment-method")
            {
                return;
            }

            if (await payments.ConfirmAsync(context, cancellationToken))
            {
                await accounts.UpdateAccountAsync(session, account =>
                {
                    if (account.OnboardingStep == "payment-method")
                    {
                        account.OnboardingStep = "project";
                    }
                }, cancellationToken);
            }
        }

        public async Task<OnboardingSaveResult> SaveAsync(SessionRecord session, IFormCollection form, CancellationToken cancellationToken)
        {
            var step = form["step"].ToString();
            if (step is not ("account-information" or "payment-method" or "project"))
            {
                return new DashboardValidation("Choose a valid setup step.");
            }

            var context = await accounts.GetContextAsync(session, cancellationToken);
            if (context.Account.OnboardingCompleted)
            {
                return "/";
            }

            if (step == "account-information")
            {
                return await SaveAccountInformationAsync(session, form, cancellationToken);
            }

            if (step == "payment-method")
            {
                return await SavePaymentMethodAsync(session, context, form, cancellationToken);
            }

            return await SaveProjectAsync(session, context, form, cancellationToken);
        }

        private async Task<OnboardingSaveResult> SaveAccountInformationAsync(SessionRecord session, IFormCollection form, CancellationToken cancellationToken)
        {
            return DisplayName(form["displayName"].ToString()) switch
            {
                DashboardValidation invalid => invalid,
                string displayName => await ApplyDisplayNameAsync(session, displayName, form["plan"].ToString(), cancellationToken)
            };
        }

        private async Task<OnboardingSaveResult> ApplyDisplayNameAsync(SessionRecord session, string displayName, string requested, CancellationToken cancellationToken)
        {
            await accounts.UpdateAccountAsync(session, account =>
            {
                account.Name = displayName;
                if (account.OnboardingStep == "account-information")
                {
                    account.OnboardingStep = "payment-method";
                }
            }, cancellationToken);
            return (await PlansAsync(cancellationToken)).Any(candidate => candidate.Key == requested)
                ? "/onboarding?plan=" + Uri.EscapeDataString(requested) : "/onboarding";
        }

        private async Task<OnboardingSaveResult> SavePaymentMethodAsync(SessionRecord session, AccountContext context, IFormCollection form, CancellationToken cancellationToken)
        {
            if (context.Account.OnboardingStep != "payment-method")
            {
                return new DashboardValidation("Complete the current setup step first.");
            }

            var plan = form["plan"].ToString();
            var selected = (await PlansAsync(cancellationToken)).FirstOrDefault(candidate => candidate.Key == plan);
            if (selected is null)
            {
                return new DashboardValidation("Choose an available plan.");
            }

            if (selected.PriceCents == 0)
            {
                await accounts.UpdateAccountAsync(session, account =>
                {
                    if (account.OnboardingStep != "payment-method")
                    {
                        throw new AccountAccessException("Onboarding progress changed.", 409);
                    }

                    account.OnboardingStep = "project";
                }, cancellationToken);
                return "/onboarding";
            }

            var interval = form["interval"].ToString();
            if (interval.Length == 0)
            {
                interval = selected.CheckoutInterval;
            }

            if (interval is not ("monthly" or "annual") || !selected.SupportsInterval(interval))
            {
                return new DashboardValidation("Choose an available plan and billing interval.");
            }

            var outcome = await SwitchToPersonalAsync(session, context.Account.PersonalOrganizationId, cancellationToken);
            return outcome switch
            {
                DashboardValidation invalid => invalid,
                AccountContext personal => await payments.StartCheckoutAsync(personal, plan, interval, "/onboarding", cancellationToken)
            };
        }

        private async Task<OnboardingSaveResult> SaveProjectAsync(SessionRecord session, AccountContext context, IFormCollection form, CancellationToken cancellationToken)
        {
            if (context.Account.OnboardingStep != "project")
            {
                return new DashboardValidation("Complete the current setup step first.");
            }

            var name = form["name"].ToString().Trim();
            if (name.Any(char.IsControl))
            {
                return new DashboardValidation("Enter a valid project name.");
            }

            if (name.EnumerateRunes().Count() > 100)
            {
                return new DashboardValidation("Use a project name with 100 characters or fewer.");
            }
            var database = DatabaseService.ValidName(form["database"].ToString());
            var regions = DatabaseService.Capped(DatabasePlacementEndpoints.Selection(form, "regionOrder"));
            var outcome = await SwitchToPersonalAsync(session, context.Account.PersonalOrganizationId, cancellationToken);
            return outcome switch
            {
                DashboardValidation invalid => invalid,
                AccountContext personal => await CompleteProjectAsync(session, personal, name, regions, database, cancellationToken)
            };
        }

        private async Task<OnboardingSaveResult> CompleteProjectAsync(SessionRecord session, AccountContext context, string name, IReadOnlyList<string> regions, string database, CancellationToken cancellationToken)
        {
            if (context.Organization is null || context.Project is null || !context.CanManage)
            {
                return new DashboardForbidden("Project membership is required.");
            }

            var project = await projects.CompleteProjectAsync(context, name.Length == 0 ? context.Project.Name : name, regions, cancellationToken);
            if (project.Id != context.Project.Id || project.OrganizationId != context.Organization.Id)
            {
                throw new IOException("The provisioned project identity changed.");
            }

            await accounts.UpdateOrganizationAsync(context, organization =>
            {
                if (!organization.CanManage(session.UserId))
                {
                    throw new AccountAccessException("Project management is required.");
                }

                organization.Projects[project.Id] = project;
            }, cancellationToken);
            var created = await FirstDatabaseAsync(context, context.Project, database, regions, cancellationToken);
            return created switch
            {
                DatabaseRecord => await FinishAsync(session, cancellationToken),
                DatabaseRefusal refused => new DashboardValidation(refused.Message)
            };
        }

        private async Task<DatabaseMutation> FirstDatabaseAsync(AccountContext context, ProjectRecord project, string name, IReadOnlyList<string> regions, CancellationToken cancellationToken)
        {
            if (project.Databases.Values.FirstOrDefault(existing => existing.Name == name) is { } existing)
            {
                return existing;
            }

            return await databases.CreateAsync(context, name, regions[0], cancellationToken, replicaRegions: [.. regions.Skip(1)]);
        }

        private async Task<OnboardingSaveResult> FinishAsync(SessionRecord session, CancellationToken cancellationToken)
        {
            await accounts.UpdateAccountAsync(session, account =>
            {
                if (account.OnboardingStep != "project")
                {
                    throw new AccountAccessException("Onboarding progress changed.", 409);
                }

                account.OnboardingStep = "";
                account.OnboardingCompleted = true;
            }, cancellationToken);
            return "/";
        }

        private async Task<PersonalWorkspace> SwitchToPersonalAsync(SessionRecord session, string personalOrganizationId, CancellationToken cancellationToken)
        {
            var personal = await accounts.SwitchOrganizationAsync(session, personalOrganizationId, cancellationToken);
            return personal.Project?.IsDefault == true ? personal : new DashboardValidation("Your Personal workspace has no default project.");
        }

        public static DisplayNameInput DisplayName(string raw)
        {
            var name = raw.Trim();
            if (name.Length == 0)
            {
                return new DashboardValidation("Enter your full name.");
            }

            if (name.Any(char.IsControl))
            {
                return new DashboardValidation("Enter a valid name.");
            }

            if (name.EnumerateRunes().Count() > 128)
            {
                return new DashboardValidation("Use a name with 128 characters or fewer.");
            }

            return name;
        }
    }
}
