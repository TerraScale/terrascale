using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Common.Validation;

namespace TerraScale.Dashboard.Features.Onboarding
{
    public sealed class OnboardingService(AccountService accounts, StripeBilling payments, IOnboardingProjectProvisioner projects)
    {
        public async Task<IReadOnlyList<BillingPlan>> PlansAsync(CancellationToken cancellationToken)
        {
            return [.. (await payments.PlansAsync(cancellationToken)).Where(plan => plan.PriceCents > 0)
                .OrderBy(plan => plan.PriceCents)
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

        public async Task<string> SaveAsync(SessionRecord session, IFormCollection form, CancellationToken cancellationToken)
        {
            var step = form["step"].ToString();
            if (step is not ("account-information" or "payment-method" or "project"))
            {
                throw new OnboardingValidationException("Choose a valid setup step.");
            }

            var context = await accounts.GetContextAsync(session, cancellationToken);
            if (context.Account.OnboardingCompleted)
            {
                return "/";
            }

            switch (step)
            {
                case "account-information":
                    var displayName = DisplayName(form["displayName"].ToString());
                    await accounts.UpdateAccountAsync(session, account =>
                    {
                        account.Name = displayName;
                        if (account.OnboardingStep == "account-information")
                        {
                            account.OnboardingStep = "payment-method";
                        }
                    }, cancellationToken);
                    break;
                case "payment-method":
                    if (context.Account.OnboardingStep != "payment-method")
                    {
                        throw new OnboardingValidationException("Complete the current setup step first.");
                    }

                    if (form["bypass"] == "1")
                    {
                        await accounts.UpdateAccountAsync(session, account =>
                        {
                            if (account.OnboardingStep != "payment-method")
                            {
                                throw new AccountAccessException("Onboarding progress changed.", 409);
                            }

                            account.OnboardingStep = "project";
                        }, cancellationToken);
                        break;
                    }
                    if (context.Organization?.Kind != "personal")
                    {
                        throw new OnboardingValidationException("Return to your Personal workspace to finish account setup.");
                    }

                    var plan = form["plan"].ToString();
                    var interval = form["interval"].ToString();
                    if (interval.Length == 0)
                    {
                        interval = (await PlansAsync(cancellationToken)).FirstOrDefault(candidate => candidate.Key == plan)?.CheckoutInterval ?? "";
                    }

                    if (interval is not ("monthly" or "annual") || !(await PlansAsync(cancellationToken)).Any(candidate => candidate.Key == plan && candidate.SupportsInterval(interval)))
                    {
                        throw new OnboardingValidationException("Choose an available plan and billing interval.");
                    }

                    return await payments.StartCheckoutAsync(context, plan, interval, "/onboarding", cancellationToken);
                case "project":
                    if (context.Account.OnboardingStep != "project")
                    {
                        throw new OnboardingValidationException("Complete the current setup step first.");
                    }

                    if (context.Organization?.Id != context.Account.PersonalOrganizationId || context.Project?.IsDefault != true)
                    {
                        throw new OnboardingValidationException("Return to your default Personal project to finish account setup.");
                    }

                    var name = form["name"].ToString().Trim();
                    if (name.Any(char.IsControl))
                    {
                        throw new OnboardingValidationException("Enter a valid project name.");
                    }

                    if (name.EnumerateRunes().Count() > 100)
                    {
                        throw new OnboardingValidationException("Use a project name with 100 characters or fewer.");
                    }
                    // The provisioner accepts only a region TerraBase reports as serving.
                    var region = form["region"].ToString().Trim().ToUpperInvariant();
                    if (context.Organization is null || context.Project is null || !context.CanManage)
                    {
                        throw new AccountAccessException("Project membership is required.");
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
                default:
                    throw new OnboardingValidationException("Choose a valid setup step.");
            }
            return "/onboarding";
        }

        public static string DisplayName(string raw)
        {
            var name = raw.Trim();
            if (name.Length == 0)
            {
                throw new OnboardingValidationException("Enter your full name.", "displayName");
            }

            if (name.Any(char.IsControl))
            {
                throw new OnboardingValidationException("Enter a valid name.", "displayName");
            }

            if (name.EnumerateRunes().Count() > 128)
            {
                throw new OnboardingValidationException("Use a name with 128 characters or fewer.", "displayName");
            }

            return name;
        }
    }
}
