using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Common.Validation;
using TerraScale.Dashboard.Errors;

namespace TerraScale.Dashboard.Features.Onboarding
{
    public static class OnboardingEndpoints
    {
        public static void AddOnboarding(this IServiceCollection services)
        {
            services.AddSingleton<OnboardingService>();
        }

        internal static async Task<IResult> RenderAsync(HttpContext context, DashboardSessions sessions, AccountService accounts, OnboardingService service, IDatabaseBackend backend, IFormCollection? draft = null, string error = "", int status = 200)
        {
            var plans = await service.PlansAsync(context.RequestAborted);
            var requested = draft?["plan"].ToString() ?? context.Request.Query["plan"].ToString();
            var returnTo = context.Request.Query["returnTo"].ToString();
            if (returnTo.Length == 0)
            {
                returnTo = context.Request.Query["return"].ToString();
            }

            var resolvedSession = await sessions.ReadAsync(context);
            if (!AccountRequest.TryResolveSession(
                resolvedSession,
                () => HttpRedirects.SeeOther("/login?signup=1&returnTo=" + Uri.EscapeDataString(
                    ReturnPath(returnTo) is { Length: > 0 } path ? path : "/onboarding" + (OfferedPlan(plans, requested) is { Length: > 0 } plan ? "?plan=" + Uri.EscapeDataString(plan) : ""))),
                () => AccountRequest.ServiceUnavailable(context),
                out var session,
                out var rejection))
            {
                return rejection;
            }
            var page = Page(context) with { Plans = plans, Plan = OfferedPlan(plans, requested) };
            var account = await accounts.EnsureAsync(session, context.RequestAborted);
            if (account.OnboardingCompleted)
            {
                return HttpRedirects.SeeOther("/");
            }

            var payment = context.Request.Query["payment"].ToString();
            if (payment == "success" && account.OnboardingStep == "payment-method")
            {
                await service.ConfirmPaymentAsync(session, context.RequestAborted);
                account = await accounts.EnsureAsync(session, context.RequestAborted);
            }
            page = page with
            {
                DisplayName = draft?["displayName"].ToString() ?? ProfileName(account.Name, account.Email),
                Email = account.Email,
                Step = account.OnboardingStep,
                PaymentCanceled = payment == "canceled",
                Error = error,
                ProjectName = draft?["name"].ToString() ?? "Personal",
                SelectedRegions = draft is null ? null : Drafted(draft),
                DatabaseName = draft?["database"].ToString() ?? "main",
                Plan = OfferedPlan(plans, requested).Length == 0 ? (plans is [var firstPlan, ..] ? firstPlan?.Key ?? "" : "") : requested,
                RequestedPlan = OfferedPlan(plans, requested),
                Document = new("Activate your TerraScale account", DocumentModel.NonceFor(context))
            };
            if (payment == "success" && page.Step == "payment-method")
            {
                page = page with { Error = "Payment setup could not be confirmed. Finish checkout or continue without payment." };
            }
            if (page.Step == "project")
            {
                try { page = page with { Regions = [.. (await backend.RegionsAsync(context.RequestAborted)).Where(region => region.Healthy).Select(region => region.Code)] }; }
                catch (HttpRequestException) { page = page with { Regions = [] }; }
                page = await StartedAsync(page, accounts, session, account.PersonalOrganizationId, draft is null, context.RequestAborted);
            }
            var view = OnboardingView.Create(page);
            view.StatusCode = status;
            return view;
        }

        internal static string ProfileName(string name, string email)
        {
            return name.Equals(email, StringComparison.OrdinalIgnoreCase) || name.Equals(email.Split('@')[0], StringComparison.OrdinalIgnoreCase) ? "" : name;
        }

        internal static async Task<IResult> SaveAsync(HttpContext context, DashboardSessions sessions, AccountService accounts, OnboardingService service, IDatabaseBackend backend)
        {
            var form = await FormRequests.ReadAsync(context, context.RequestAborted);
            if (!Csrf.Validate(context, form))
            {
                return RequestError.FormExpired(context, "/onboarding", TypedResults.StatusCode(403));
            }

            var resolvedSession = await sessions.ReadAsync(context);
            if (!AccountRequest.TryResolveSession(
                resolvedSession,
                () => HttpRedirects.SeeOther("/login"),
                () => AccountRequest.ServiceUnavailable(context),
                out var session,
                out var rejection))
            {
                return rejection;
            }

            try
            {
                var outcome = await service.SaveAsync(session, form, context.RequestAborted);
                return outcome switch
                {
                    string destination => HttpRedirects.SeeOther(destination),
                    DashboardValidation invalid => await RenderAsync(context, sessions, accounts, service, backend, form, invalid.Message, 422),
                    DashboardForbidden refused => await RenderAsync(context, sessions, accounts, service, backend, form, refused.Message, 403),
                    DatabaseRefusal refusal => await RenderAsync(context, sessions, accounts, service, backend, form, refusal.Message, refusal.Status)
                };
            }
            catch (OnboardingValidationException error)
            {
                return await RenderAsync(context, sessions, accounts, service, backend, form, error.Message, 422);
            }
            catch (AccountAccessException error) { return await RenderAsync(context, sessions, accounts, service, backend, form, error.Message, error.StatusCode); }
            catch (Exception error) when (DatabaseResults.KnownFailure(error)) { return await RenderAsync(context, sessions, accounts, service, backend, form, DatabaseResults.Message(error), DatabaseResults.Status(error)); }
        }

        private static async Task<OnboardingPage> StartedAsync(OnboardingPage page, AccountService accounts, SessionRecord session, string personalOrganizationId, bool fresh, CancellationToken cancellationToken)
        {
            var available = page.Regions ?? [];
            var current = await accounts.GetContextAsync(session, cancellationToken);
            if (fresh && current.Organization?.Id == personalOrganizationId && current.Project?.Databases.Values.FirstOrDefault() is { } started)
            {
                page = page with { DatabaseName = started.Name, SelectedRegions = started.Regions };
            }

            if (fresh && current.Organization?.Id == personalOrganizationId && current.Project is { IsDefault: true } project)
            {
                page = page with { ProjectName = project.Name };
            }

            return page.SelectedRegions is { } chosen ? page with { SelectedRegions = [.. chosen.Where(code => available.Contains(code, StringComparer.OrdinalIgnoreCase))] } : page;
        }

        private static IReadOnlyList<string> Drafted(IFormCollection draft)
        {
            var chosen = draft["regions"].OfType<string>().ToArray();
            return [.. draft["regionOrder"].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(code => chosen.Contains(code, StringComparer.OrdinalIgnoreCase)).Concat(chosen).Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        private static OnboardingPage Page(HttpContext context)
        {
            return new(new("Create account", DocumentModel.NonceFor(context)), Csrf.Token(context));
        }

        private static string OfferedPlan(IReadOnlyList<BillingPlan> plans, string? raw)
        {
            return raw is not null && plans.Any(plan => plan.Key == raw) ? raw : "";
        }

        private static string ReturnPath(string? value)
        {
            return Csrf.LocalRedirect(value) is { } path && path != "/" ? path : "";
        }
    }
}
