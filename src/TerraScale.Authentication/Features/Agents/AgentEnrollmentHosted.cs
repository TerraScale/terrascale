using System.Diagnostics;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Ui;

namespace TerraScale.Authentication.Features.Agents;

internal static class AgentEnrollmentHosted
{
    internal const string Route = "/ui/{slug}/agent-enrollments";

    private const string StepUpCookie = "tsauth_agent_step_up";

    internal static async Task<IResult> HandleAsync(HttpContext context, bool post, CancellationToken cancellationToken)
    {
        var services = context.RequestServices;
        if (SignInFlow.WrongSlug(context) || !Tenancy.IsProject(SignInFlow.Tenant(context)))
        {
            return Results.NotFound();
        }

        var tenant = SignInFlow.Tenant(context);
        if (post && (!context.Request.HasFormContentType || context.Request.Headers.Origin is not [{ } origin]
            || !SignInFlow.IsPublicOrigin(origin, services.GetRequiredService<AuthOptions>().PublicOrigin)))
        {
            return SignInFlow.Refused(context);
        }

        if (await services.GetRequiredService<Sessions>().ResolveVersionedAsync(tenant, context.Request.Cookies[Tenancy.SessionCookie(tenant)], cancellationToken) is not { } live)
        {
            return post ? SignInFlow.Refused(context)
                : Results.Redirect($"/ui/{tenant}/sign-in?next={Uri.EscapeDataString(context.Request.Path + context.Request.QueryString)}");
        }

        var form = post ? await context.Request.ReadFormAsync(cancellationToken) : null;
        var entered = (form is null ? context.Request.Query["user_code"] : form["user_code"]).ToString().Trim();
        if (entered.Length == 0)
        {
            return AgentEnrollmentPageModel.Render(context, new(tenant, AgentEnrollmentStep.Enter));
        }

        if ((await services.GetRequiredService<HostedAuth>().LimitIpDurablyAsync(SignInFlow.Ip(context), cancellationToken)
            ?? await services.GetRequiredService<AttemptLimiter>().AcquireDurableAsync(tenant, "agent-enrollment-code-user", live.User.Id, AgentEnrollments.LookupsPerUser,
                AgentEnrollments.LookupWindow, cancellationToken)) is { } wait)
        {
            return HostedAuth.TooMany(wait);
        }

        var enrollments = services.GetRequiredService<AgentEnrollments>();
        if (AgentEnrollments.Normalize(entered) is not { } code || await enrollments.FindAsync(tenant, code, cancellationToken) is not { } view)
        {
            return AgentEnrollmentPageModel.Render(context, new(tenant, AgentEnrollmentStep.Enter, entered.Length <= 32 ? entered : null, "That code is wrong or expired. Start again from the agent."),
                StatusCodes.Status400BadRequest);
        }

        var confirm = new AgentEnrollmentPageModel(tenant, AgentEnrollmentStep.Confirm, view.UserCode)
        {
            Account = live.User.Email,
            AgentName = view.Enrollment.Value.Name,
            Scopes = view.Scopes,
            SelfSubject = view.Enrollment.Value.AllowSelfSubject,
            Fingerprint = view.Enrollment.Value.Thumbprint[..16],
            Confirmation = enrollments.Confirmation(view, live.User.Id),
        };
        var approver = new LiveApprover(live.Session, live.Version, live.User);
        AgentDecision? outcome = form?["decision"].ToString() switch
        {
            "approve" => await enrollments.ApproveAsync(approver, view, form["confirmation"].ToString(), context.Request.Cookies[StepUpCookie], cancellationToken),
            "deny" => await enrollments.DenyAsync(approver, view, cancellationToken),
            _ => null,
        };
        return outcome switch
        {
            null => AgentEnrollmentPageModel.Render(context, confirm, post ? StatusCodes.Status400BadRequest : StatusCodes.Status200OK),
            AgentStepUpRequired required => StepUp(context, tenant, required),
            AgentApproved => Finish(context, tenant, AgentEnrollmentStep.Approved),
            AgentDenied => Finish(context, tenant, AgentEnrollmentStep.Denied),
            AgentDecisionRefused refused => AgentEnrollmentPageModel.Render(context, confirm with { Error = refused.Message }, StatusCodes.Status409Conflict),
            _ => throw new UnreachableException(),
        };
    }

    private static IResult StepUp(HttpContext context, string tenant, AgentStepUpRequired required)
    {
        var now = context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        context.Response.Cookies.Append(StepUpCookie, required.StepUp.Proof, Options(tenant, required.StepUp.ExpiresAt - now));
        return Results.Redirect(required.StepUp.StepUpUrl);
    }

    private static IResult Finish(HttpContext context, string tenant, AgentEnrollmentStep step)
    {
        context.Response.Cookies.Delete(StepUpCookie, Options(tenant, null));
        return AgentEnrollmentPageModel.Render(context, new(tenant, step));
    }

    private static CookieOptions Options(string tenant, TimeSpan? lifetime) => new()
    {
        Path = AgentEnrollments.PagePath(tenant),
        Secure = true,
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        IsEssential = true,
        MaxAge = lifetime,
    };
}
