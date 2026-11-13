using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.ProjectTenants;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.SignIn;

public sealed class SetUsernameEndpoint(Users users, Sessions sessions, TotpCredentials mfa, HostedAuth hosted, AuthOptions options)
    : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/ui/{slug}/security/username");
        Name("ui_security_username");
        Summary("Set the account username");
        Description("Same-origin form post from a project session opened in the last ten minutes. Requires username sign-in for the project. Usernames are case-insensitive, unique per project and cannot be reserved names. Shares the hosted IP budget. A name taken before or during the update returns the same form error.");
        Tags("Security");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<string>(StatusCodes.Status200OK, "text/html");
        Produces<string>(StatusCodes.Status400BadRequest, "text/html");
        Produces<string>(StatusCodes.Status401Unauthorized, "text/html");
        Produces<string>(StatusCodes.Status429TooManyRequests, "text/html");
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var context = this.HttpContext;
        if (await SignInFlow.ReadAsync(context, options, cancellationToken) is not { } form
            || !Tenancy.IsProject(SignInFlow.Tenant(context)) || !HostedTheme.Of(context).Username)
        {
            return SignInFlow.Refused(context);
        }

        if (await hosted.LimitIpAsync(SignInFlow.Ip(context), mails: false, cancellationToken) is { } wait)
        {
            return HostedAuth.TooMany(wait);
        }

        if (await SecurityFlow.LiveAsync(context, sessions, cancellationToken) is not { } live)
        {
            return SecurityFlow.SignIn(context);
        }

        if (!SecurityFlow.Recent(context, live.Session, SecurityFlow.FactorEnrollmentWindow))
        {
            return SignInPageModel.Render(context, new SignInPageModel(SignInStep.Reauthenticate, SignInFlow.Tenant(context),
                Next: SignInFlow.SecurityPath(SignInFlow.Tenant(context)))
            { Account = live.User.Email }, StatusCodes.Status401Unauthorized);
        }

        if (Users.NormalizeUsername(form.Username) is not { } username)
        {
            return await SecurityFlow.PageAsync(context, mfa, live.User,
                ("Choose a username", "Use 3 to 32 letters, digits, underscores or dots, and choose a name that is not reserved."),
                cancellationToken, StatusCodes.Status400BadRequest);
        }

        var owner = await users.FindByUsernameAsync(live.User.InstanceId, username, cancellationToken);
        if (owner is not null && owner.Id != live.User.Id
            || !await users.SetUsernameAsync(live.User, username,
                Sessions.Live(live.Session, live.Version, context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow()), cancellationToken))
        {
            return await SecurityFlow.RefusedAsync(context, sessions, mfa, live.User,
                ("Username taken", "Username taken. Choose another username."), StatusCodes.Status400BadRequest, cancellationToken);
        }

        return await SecurityFlow.PageAsync(context, mfa, live.User with { Username = username }, null, cancellationToken, notice: "Username saved.");
    }
}
