using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Errors;

namespace TerraScale.Dashboard.Features.Onboarding
{
    public readonly union OnboardingSaveResult(string, Validation, Forbidden);

    internal readonly union PersonalWorkspace(AccountContext, Validation);

    public sealed class OnboardingService(AccountService accounts, StripeBilling payments, IOnboardingProjectProvisioner projects)
    {
        // Free first, then paid plans by price; the payment step offers Free by default.
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
                return new Validation("Choose a valid setup step.");
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
                Validation invalid => invalid,
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
                return new Validation("Complete the current setup step first.");
            }

            var plan = form["plan"].ToString();
            var selected = (await PlansAsync(cancellationToken)).FirstOrDefault(candidate => candidate.Key == plan);
            if (selected is null)
            {
                return new Validation("Choose an available plan.");
            }

            if (selected.PriceCents == 0)
            {
                // Free needs no checkout; the account proceeds to the project step.
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
                return new Validation("Choose an available plan and billing interval.");
            }

            return await SwitchToPersonalAsync(session, context.Account.PersonalOrganizationId, cancellationToken) switch
            {
                Validation invalid => invalid,
                AccountContext personal => await payments.StartCheckoutAsync(personal, plan, interval, "/onboarding", cancellationToken)
            };
        }

        private async Task<OnboardingSaveResult> SaveProjectAsync(SessionRecord session, AccountContext context, IFormCollection form, CancellationToken cancellationToken)
        {
            if (context.Account.OnboardingStep != "project")
            {
                return new Validation("Complete the current setup step first.");
            }

            var name = form["name"].ToString().Trim();
            if (name.Any(char.IsControl))
            {
                return new Validation("Enter a valid project name.");
            }

            if (name.EnumerateRunes().Count() > 100)
            {
                return new Validation("Use a project name with 100 characters or fewer.");
            }
            // The provisioner accepts only a region TerraBase reports as serving.
            var region = form["region"].ToString().Trim().ToUpperInvariant();
            return await SwitchToPersonalAsync(session, context.Account.PersonalOrganizationId, cancellationToken) switch
            {
                Validation invalid => invalid,
                AccountContext personal => await CompleteProjectAsync(session, personal, name, region, cancellationToken)
            };
        }

        private async Task<OnboardingSaveResult> CompleteProjectAsync(SessionRecord session, AccountContext context, string name, string region, CancellationToken cancellationToken)
        {
            if (context.Organization is null || context.Project is null || !context.CanManage)
            {
                return new Forbidden("Project membership is required.");
            }

            var project = await projects.CompleteProjectAsync(context, name.Length == 0 ? context.Project.Name : name, region, cancellationToken);
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
            return personal.Project?.IsDefault == true ? personal : new Validation("Your Personal workspace has no default project.");
        }

        public static Valid<string> DisplayName(string raw)
        {
            var name = raw.Trim();
            if (name.Length == 0)
            {
                return new Validation("Enter your full name.", "displayName");
            }

            if (name.Any(char.IsControl))
            {
                return new Validation("Enter a valid name.", "displayName");
            }

            if (name.EnumerateRunes().Count() > 128)
            {
                return new Validation("Use a name with 128 characters or fewer.", "displayName");
            }

            return name;
        }
    }
}
