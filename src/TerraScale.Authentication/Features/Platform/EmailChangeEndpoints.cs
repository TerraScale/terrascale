using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.Ui;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.Platform;

/// <summary><c>POST /v1/terrascale/account/email-change</c>.</summary>
public sealed class RequestEmailChangeEndpoint(EmailChanges changes, StepUpProofs proofs, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/v1/terrascale/account/email-change");
        this.Document("request_email_change", "Start a sign-in email change",
            "Requires a fresh change_email/ts-auth proof in X-TerraScale-Step-Up-Proof. Emails a single-use code, valid ten minutes, to new_email and a cancel link to the current address; replaces any pending change. 409 email_taken when another account owns the address; 429 rate_limited after three requests per account or address per hour, or the platform-wide hourly budget.",
            "Members", typeof(EmailChangeRequest), StatusCodes.Status202Accepted, typeof(EmailChangeBody), roleChecked: false, scoped: false, conflict: true);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var body = await this.BodyAsync(EmailChangeJson.Default.EmailChangeRequest, cancellationToken);
        return Results.Json(await changes.RequestAsync(caller, this.HttpContext.Request, body.NewEmail, proofs, cancellationToken),
            EmailChangeJson.Default.EmailChangeBody, statusCode: StatusCodes.Status202Accepted);
    }
}

/// <summary><c>POST /v1/terrascale/account/email-change/confirm</c>.</summary>
public sealed class ConfirmEmailChangeEndpoint(EmailChanges changes, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/v1/terrascale/account/email-change/confirm");
        this.Document("confirm_email_change", "Confirm a sign-in email change",
            "Commits the caller's pending change with the code the new address received, at the authentication state it was requested in: the new address becomes the sign-in email, the old one is released, and the epoch advances, ending every session and token of the account, including this one. The code works once; five wrong codes, expiry, a replaced request or any sign-out void it (403 email_change_denied). 409 email_taken when another account claimed the address meanwhile.",
            "Members", typeof(ConfirmEmailChangeRequest), StatusCodes.Status204NoContent, null, roleChecked: false, scoped: false, conflict: true);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        await changes.ConfirmAsync(caller, (await this.BodyAsync(EmailChangeJson.Default.ConfirmEmailChangeRequest, cancellationToken)).Code, cancellationToken);
        return Results.NoContent();
    }
}

/// <summary>The cancel link of an email change notice. GET only renders; the POST cancels.</summary>
public sealed class EmailChangeCancelPageEndpoint : EndpointWithoutRequest<IResult>
{
    internal const string Path = "/ui/{slug}/email-change/cancel";

    /// <inheritdoc />
    public override void Configure()
    {
        Get(Path);
        Name("ui_email_change_cancel");
        Summary("Review a pending email change");
        Description("Renders a confirmation form for the cancel link of an email change notice. Changes nothing.");
        Tags("Security");
        Produces<string>(200, "text/html");
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(EmailChangeCancelHosted.Render(this.HttpContext, this.HttpContext.Request.Query["user"].ToString(), this.HttpContext.Request.Query["token"].ToString(), null));
}

/// <summary>Cancels a pending email change from the current address's link.</summary>
public sealed class CancelEmailChangeEndpoint(EmailChanges changes, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(EmailChangeCancelPageEndpoint.Path);
        Name("ui_cancel_email_change");
        Summary("Cancel a pending email change");
        Description("Same-origin form POST with user and token from the notice link. A live link cancels the change by signing the account out everywhere; the answer is the same page whether or not the link was live.");
        Tags("Security");
        Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(EmptyRequest)));
        Produces<string>(200, "text/html");
        Produces<ErrorBody>(403);
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (await SignInFlow.ReadAsync(this.HttpContext, options, cancellationToken) is null)
        {
            throw new ApiException(403, "forbidden", "Submit the form from the cancel page.");
        }

        var form = this.HttpContext.Request.Form;
        await changes.CancelAsync(form["user"].ToString(), form["token"].ToString(), cancellationToken);
        return EmailChangeCancelHosted.Render(this.HttpContext, string.Empty, string.Empty,
            "No email change is pending any more, and every session of the account is signed out. If you didn't ask for the change, reset your password now.");
    }
}

internal static class EmailChangeCancelHosted
{
    internal static IResult Render(HttpContext context, string user, string token, string? done)
    {
        if (SignInFlow.WrongSlug(context) || SignInFlow.Tenant(context) != "terrascale")
        {
            return SignInFlow.Refused(context);
        }

        var nonce = BrowserSecurity.NewNonce();
        BrowserSecurity.ApplyPageHeaders(context.Response, nonce);
        return EmailChangeCancelPage.Create(new EmailChangeCancelPageModel(user, token, done, nonce));
    }
}
