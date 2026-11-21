using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Common.Validation;
using TerraScale.Dashboard.Errors;
using System.Diagnostics;

namespace TerraScale.Dashboard.Features.Onboarding
{
    using NotFound = TerraScale.Dashboard.Errors.NotFound;
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

            var session = await sessions.ReadAsync(context);
            // Accounts are created on ts-auth's hosted sign-up; a signed-out visitor here is registering, so open it directly.
            if (session is null)
            {
                var target = ReturnPath(returnTo) is { Length: > 0 } path ? path : "/onboarding" + (OfferedPlan(plans, requested) is { Length: > 0 } plan ? "?plan=" + Uri.EscapeDataString(plan) : "");
                return HttpRedirects.SeeOther("/login?signup=1&returnTo=" + Uri.EscapeDataString(target));
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
                Error = error.Length > 0 ? error : context.Request.Query["error"].ToString(),
                ProjectName = draft?["name"].ToString() ?? "Personal",
                Region = draft?["region"].ToString() ?? "",
                Plan = OfferedPlan(plans, requested).Length == 0 ? (plans is [var firstPlan, ..] ? firstPlan?.Key ?? "" : "") : requested,
                RequestedPlan = OfferedPlan(plans, requested),
                Document = new("Activate your TerraScale account", DocumentModel.NonceFor(context))
            };
            if (payment == "success" && page.Step == "payment-method")
            {
                page = page with { Error = "Payment setup could not be confirmed. Finish checkout or continue without payment." };
            }
            // Offer exactly the regions the project step accepts: those TerraBase reports as serving.
            if (page.Step == "project")
            {
                try { page = page with { Regions = [.. (await backend.RegionsAsync(context.RequestAborted)).Where(region => region.Healthy).Select(region => region.Code)] }; }
                catch (HttpRequestException) { page = page with { Regions = [] }; }
            }
            var view = OnboardingView.Create(page);
            view.StatusCode = status;
            return view;
        }

        // ts-auth names a new account after its email (or the email's handle) until the user gives a name;
        // only a real profile name is offered as the Full name default.
        internal static string ProfileName(string name, string email)
        {
            return name.Equals(email, StringComparison.OrdinalIgnoreCase) || name.Equals(email.Split('@')[0], StringComparison.OrdinalIgnoreCase) ? "" : name;
        }

        internal static async Task<IResult> SaveAsync(HttpContext context, DashboardSessions sessions, AccountService accounts, OnboardingService service, IDatabaseBackend backend)
        {
            IFormCollection form;
            switch (await FormRequests.ReadAsync(context, context.RequestAborted))
            {
                case Validation invalid:
                    return FormRequests.Refused(context, invalid);
                case UnsupportedMediaType unsupported:
                    return FormRequests.Refused(context, unsupported);
                case IFormCollection parsed:
                    form = parsed;
                    break;
                case null:
                    throw new UnreachableException();
            }
            if (!Csrf.Validate(context, form))
            {
                return RequestError.FormExpired(context, "/onboarding", Results.StatusCode(403));
            }

            var session = await sessions.ReadAsync(context);
            if (session is null)
            {
                return HttpRedirects.SeeOther("/login");
            }

            try
            {
                return await service.SaveAsync(session, form, context.RequestAborted) switch
                {
                    string destination => HttpRedirects.SeeOther(destination),
                    Validation invalid => await RenderAsync(context, sessions, accounts, service, backend, form, invalid.Message, 422),
                    Forbidden refused => await RenderAsync(context, sessions, accounts, service, backend, form, refused.Message, 403),
                    NotFound missing => await RenderAsync(context, sessions, accounts, service, backend, form, missing.Message ?? "The personal project is unavailable.", 404)
                };
            }
            catch (OnboardingValidationException error)
            {
                return await RenderAsync(context, sessions, accounts, service, backend, form, error.Message, 422);
            }
            catch (AccountAccessException error) { return await RenderAsync(context, sessions, accounts, service, backend, form, error.Message, error.StatusCode); }
        }

        private static OnboardingPage Page(HttpContext context)
        {
            return new(new("Create account", DocumentModel.NonceFor(context)), Csrf.Token(context));
        }

        private static string OfferedPlan(IReadOnlyList<BillingPlan> plans, string? raw)
        {
            return plans.Any(plan => plan.Key == raw) ? raw! : "";
        }

        private static string ReturnPath(string? value)
        {
            return Csrf.LocalRedirect(value) is { } path && path != "/" ? path : "";
        }
    }
}
