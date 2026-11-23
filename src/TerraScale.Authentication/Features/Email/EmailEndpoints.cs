using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Errors;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.RateLimiting;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Hosting;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.Email;

public sealed record EmailBrandingRequest(
    [property: JsonPropertyName("expected_version")] long? ExpectedVersion,
    [property: JsonPropertyName("actor")] EmailActor? Actor,
    [property: JsonPropertyName("branding")] EmailBranding? Branding);

public sealed record EmailBrandingResponse(
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt,
    [property: JsonPropertyName("updated_by")] EmailActor? UpdatedBy,
    [property: JsonPropertyName("branding")] EmailBranding Branding,
    [property: JsonPropertyName("defaults")] EmailBranding Defaults);

public sealed record EmailTemplateRequest(
    [property: JsonPropertyName("expected_version")] long? ExpectedVersion,
    [property: JsonPropertyName("actor")] EmailActor? Actor,
    [property: JsonPropertyName("fields")] EmailTemplateFields? Fields);

public sealed record EmailTemplateSummary(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("locale")] string Locale,
    [property: JsonPropertyName("overridden")] bool Overridden,
    [property: JsonPropertyName("variables")] IReadOnlyList<string> Variables);

public sealed record EmailTemplateListResponse(
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("templates")] IReadOnlyList<EmailTemplateSummary> Templates);

public sealed record EmailTemplateResponse(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("locale")] string Locale,
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("overridden")] bool Overridden,
    [property: JsonPropertyName("variables")] IReadOnlyList<string> Variables,
    [property: JsonPropertyName("fields")] EmailTemplateFields Fields,
    [property: JsonPropertyName("defaults")] EmailTemplateFields Defaults);

public sealed record EmailPreviewRequest(
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("locale")] string? Locale,
    [property: JsonPropertyName("color_scheme")] string? ColorScheme = null,
    [property: JsonPropertyName("branding")] EmailBranding? Branding = null,
    [property: JsonPropertyName("fields")] EmailTemplateFields? Fields = null);

public sealed record EmailPreviewResponse(
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("html")] string Html,
    [property: JsonPropertyName("text")] string Text);

public sealed record EmailTestRequest(
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("locale")] string? Locale,
    [property: JsonPropertyName("to")] string? To,
    [property: JsonPropertyName("actor")] EmailActor? Actor,
    [property: JsonPropertyName("branding")] EmailBranding? Branding = null,
    [property: JsonPropertyName("fields")] EmailTemplateFields? Fields = null);

public sealed record EmailTestResponse(
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("subject")] string Subject);

[JsonSerializable(typeof(EmailTestResponse))]
[JsonSerializable(typeof(ErrorBody))]
internal sealed partial class EmailEndpointJson : JsonSerializerContext;

public sealed record EmailVersionSummary(
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt,
    [property: JsonPropertyName("actor")] EmailActor? Actor,
    [property: JsonPropertyName("change")] string? Change);

public sealed record EmailVersionListResponse([property: JsonPropertyName("versions")] IReadOnlyList<EmailVersionSummary> Versions);

public sealed record EmailRestoreRequest(
    [property: JsonPropertyName("expected_version")] long? ExpectedVersion,
    [property: JsonPropertyName("actor")] EmailActor? Actor);

internal readonly union EmailBody<T>(T, Validation, PayloadTooLarge, UnsupportedMediaType);

internal readonly union SavedEmailConfig(EmailConfig, Conflict);

internal static class EmailApi
{
    public const int MaxBodyBytes = 64 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static IResult? Refuse(HttpContext context, AuthOptions options) => DashboardClientAuthentication.Refuse(context, options);

    public static async Task<EmailBody<T>> ReadAsync<T>(HttpContext context, CancellationToken cancellationToken)
    {
        if (!context.Request.HasJsonContentType())
        {
            return new UnsupportedMediaType("Send the body as application/json.");
        }

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxBodyBytes;
        }

        try
        {
            var value = await JsonSerializer.DeserializeAsync<T>(context.Request.Body, Json, cancellationToken);
            if (value is null)
            {
                return new Validation("The body must be a JSON object.");
            }

            return value;
        }
        catch (JsonException error)
        {
            return new Validation($"The body is not valid JSON for this operation ({error.Path}).");
        }
        catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return new PayloadTooLarge($"The body is larger than {MaxBodyBytes} bytes.");
        }
    }

    public static AuthEmailKind Kind(string? kind, bool route = false) =>
        kind is not null && AuthEmails.Kinds.TryGetValue(kind, out var parsed) ? parsed
        : throw new ApiException(route ? StatusCodes.Status404NotFound : StatusCodes.Status422UnprocessableEntity, route ? "not_found" : "invalid_request",
            $"kind must be one of {string.Join(", ", AuthEmails.Kinds.Keys)}.");

    public static string Locale(string? locale, bool route = false) =>
        locale is not null && AuthEmails.Locales.Contains(locale) ? locale
        : throw new ApiException(route ? StatusCodes.Status404NotFound : StatusCodes.Status422UnprocessableEntity, route ? "not_found" : "invalid_request",
            $"locale must be one of {string.Join(", ", AuthEmails.Locales)}.");

    public static long ExpectedVersion(long? version) =>
        version is >= 0 ? version.Value : throw new ApiException(StatusCodes.Status422UnprocessableEntity, "invalid_request", "expected_version is required: the version last read, 0 before the first save.");

    public static SavedEmailConfig Saved(EmailConfig? saved, long expected) =>
        saved is { } config ? new SavedEmailConfig(config) : new SavedEmailConfig(new Conflict(
            $"The email configuration is no longer at version {expected}; read it again and retry.", "version_conflict"));

    public static IResult Answer(SavedEmailConfig saved, Func<EmailConfig, IResult> answer) => saved switch
    {
        EmailConfig config => answer(config),
        Conflict error => ErrorResults.ToResult(error),
    };

    public static EmailConfig Draft(EmailConfig config, AuthEmailKind kind, string locale, EmailBranding? branding, EmailTemplateFields? fields) => config with
    {
        Branding = branding is null ? config.Branding : EmailValidation.Branding(branding),
        Templates = fields is null ? config.Templates : WithTemplate(config.Templates, kind, locale, EmailValidation.Fields(kind, fields)),
    };

    public static IReadOnlyList<EmailTemplateOverride> WithTemplate(IReadOnlyList<EmailTemplateOverride> templates, AuthEmailKind kind, string locale, EmailTemplateFields fields)
    {
        var name = AuthEmails.Name(kind);
        var others = templates.Where(entry => entry.Kind != name || entry.Locale != locale);
        return fields == new EmailTemplateFields() ? [.. others] : [.. others, new EmailTemplateOverride(name, locale, fields)];
    }

    public static AuthEmailValues Sample(AuthEmailKind kind, string email, AuthOptions options, DateTimeOffset now) => new(email)
    {
        Code = kind is AuthEmailKind.MagicLink or AuthEmailKind.Invitation or AuthEmailKind.NewDevice or AuthEmailKind.Lockout or AuthEmailKind.EmailChangeNotice ? null : "123456",
        Link = kind switch
        {
            AuthEmailKind.MagicLink or AuthEmailKind.Invitation => new Uri(options.PublicOrigin, $"/ui/{OidcIssuer.Slug}/sign-in"),
            AuthEmailKind.EmailChangeNotice => new Uri(options.PublicOrigin, $"/ui/{OidcIssuer.Slug}/email-change/cancel"),
            _ => null,
        },
        ExpiresIn = kind switch
        {
            AuthEmailKind.Invitation => TimeSpan.FromDays(7),
            AuthEmailKind.SocialLinkCode => PendingSocialLinks.Lifetime,
            AuthEmailKind.NewDevice or AuthEmailKind.Lockout => null,
            AuthEmailKind.EmailChangeCode or AuthEmailKind.EmailChangeNotice => EmailChanges.Lifetime,
            _ => Passcodes.Lifetime,
        },
        Provider = kind == AuthEmailKind.SocialLinkCode ? "Google" : null,
        NewEmail = kind is AuthEmailKind.EmailChangeCode or AuthEmailKind.EmailChangeNotice ? "new." + email : null,
        Organization = "Acme",
        Inviter = "Alex Silva",
        Device = "Chrome on macOS",
        Location = "São Paulo, Brazil",
        Time = now,
        LockoutUntil = kind == AuthEmailKind.Lockout ? now.AddMinutes(15) : null,
    };

    public static EmailBrandingResponse Branding(EmailConfig config, AuthEmails emails) =>
        new(config.Version, config.CreatedAt, config.Actor, config.Branding, emails.Brand.ToBranding());

    public static EmailTemplateResponse Template(EmailConfig config, AuthEmailKind kind, string locale)
    {
        var fields = config.Override(AuthEmails.Name(kind), locale);
        return new(AuthEmails.Name(kind), locale, config.Version, fields is not null, AuthEmails.Variables(kind), fields ?? new EmailTemplateFields(), AuthEmails.Defaults(kind, locale));
    }

    public static IResult Error(int status, string code, string message) =>
        TypedResults.Json(new ErrorBody(new ErrorDetail(code, message)), EmailEndpointJson.Default.ErrorBody, statusCode: status);
}

public sealed class GetEmailBrandingEndpoint(EmailSettings settings, AuthEmails emails, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/v1/{slug}/email/branding");
        Name("email_branding_get");
        Summary("Read email branding");
        Description("The saved branding (null fields use the default) and the default of every field.");
        Tags("Email");
        Produces<EmailBrandingResponse>();
        Produces<ErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Metadata(new RequiresSecurity(SecuritySchemes.ClientBasic));
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        EmailApi.Refuse(this.HttpContext, options)
        ?? TypedResults.Json(EmailApi.Branding(await settings.GetAsync(OidcIssuer.InstanceId, cancellationToken), emails));
}

public sealed class PutEmailBrandingEndpoint(EmailSettings settings, AuthEmails emails, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Put("/v1/{slug}/email/branding");
        Name("email_branding_put");
        Summary("Save email branding");
        Description("Replaces the whole branding (null or omitted fields use the default) if the configuration is still at expected_version, and returns the new version. Colours are #rrggbb; URLs are https; logo_url needs logo_width and logo_height.");
        Tags("Email");
        Metadata(new AcceptsMetadata(["application/json"], typeof(EmailBrandingRequest)));
        Produces<EmailBrandingResponse>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status422UnprocessableEntity);
        Metadata(new RequiresSecurity(SecuritySchemes.ClientBasic));
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (EmailApi.Refuse(this.HttpContext, options) is { } refused)
        {
            return refused;
        }

        return (await EmailApi.ReadAsync<EmailBrandingRequest>(this.HttpContext, cancellationToken) switch
        {
            EmailBrandingRequest request => await SaveAsync(request, cancellationToken),
            Validation error => ErrorResults.ToResult(error),
            PayloadTooLarge error => ErrorResults.ToResult(error),
            UnsupportedMediaType error => ErrorResults.ToResult(error),
        });
    }

    private async Task<IResult> SaveAsync(EmailBrandingRequest request, CancellationToken cancellationToken)
    {
        var expected = EmailApi.ExpectedVersion(request.ExpectedVersion);
        var actor = EmailValidation.Actor(request.Actor);
        var branding = EmailValidation.Branding(request.Branding);
        var saved = await settings.SaveAsync(OidcIssuer.InstanceId, expected, actor, "branding", current => current with { Branding = branding }, cancellationToken);
        return EmailApi.Answer(EmailApi.Saved(saved, expected), config => TypedResults.Json(EmailApi.Branding(config, emails)));
    }
}

public sealed class ListEmailTemplatesEndpoint(EmailSettings settings, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/v1/{slug}/email/templates");
        Name("email_templates_list");
        Summary("List email templates");
        Description("Every email kind in every locale, whether its copy is overridden, and the placeholders it may use.");
        Tags("Email");
        Produces<EmailTemplateListResponse>();
        Produces<ErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Metadata(new RequiresSecurity(SecuritySchemes.ClientBasic));
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (EmailApi.Refuse(this.HttpContext, options) is { } refused)
        {
            return refused;
        }

        var config = await settings.GetAsync(OidcIssuer.InstanceId, cancellationToken);
        return TypedResults.Json(new EmailTemplateListResponse(config.Version,
        [
            .. from kind in AuthEmails.Kinds
               from locale in AuthEmails.Locales
               select new EmailTemplateSummary(kind.Key, locale, config.Override(kind.Key, locale) is not null, AuthEmails.Variables(kind.Value)),
        ]));
    }
}

public sealed class GetEmailTemplateEndpoint(EmailSettings settings, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/v1/{slug}/email/templates/{kind}/{locale}");
        Name("email_template_get");
        Summary("Read an email template");
        Description("kind is sign_in_code, magic_link, verify_email, password_reset, invitation, mfa_code, new_device, lockout, email_change_code or email_change_notice; locale is en or pt-BR. Lockout copy uses locked_until, an absolute UTC timestamp.");
        Tags("Email");
        Produces<EmailTemplateResponse>();
        Produces<ErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Metadata(new RequiresSecurity(SecuritySchemes.ClientBasic));
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (EmailApi.Refuse(this.HttpContext, options) is { } refused)
        {
            return refused;
        }

        var kind = EmailApi.Kind(this.HttpContext.GetRouteValue("kind") as string, route: true);
        var locale = EmailApi.Locale(this.HttpContext.GetRouteValue("locale") as string, route: true);
        return TypedResults.Json(EmailApi.Template(await settings.GetAsync(OidcIssuer.InstanceId, cancellationToken), kind, locale));
    }
}

public sealed class PutEmailTemplateEndpoint(EmailSettings settings, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Put("/v1/{slug}/email/templates/{kind}/{locale}");
        Name("email_template_put");
        Summary("Save an email template");
        Description("Replaces the override (null fields use the default; all null resets the template) if the configuration is still at expected_version. Copy is plain text with the kind's {{ name }} placeholders only; a blank line in body starts a paragraph. The code, button, expiry line and footer always come from the layout.");
        Tags("Email");
        Metadata(new AcceptsMetadata(["application/json"], typeof(EmailTemplateRequest)));
        Produces<EmailTemplateResponse>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status422UnprocessableEntity);
        Metadata(new RequiresSecurity(SecuritySchemes.ClientBasic));
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (EmailApi.Refuse(this.HttpContext, options) is { } refused)
        {
            return refused;
        }

        var kind = EmailApi.Kind(this.HttpContext.GetRouteValue("kind") as string, route: true);
        var locale = EmailApi.Locale(this.HttpContext.GetRouteValue("locale") as string, route: true);
        return (await EmailApi.ReadAsync<EmailTemplateRequest>(this.HttpContext, cancellationToken) switch
        {
            EmailTemplateRequest request => await SaveAsync(kind, locale, request, cancellationToken),
            Validation error => ErrorResults.ToResult(error),
            PayloadTooLarge error => ErrorResults.ToResult(error),
            UnsupportedMediaType error => ErrorResults.ToResult(error),
        });
    }

    private async Task<IResult> SaveAsync(AuthEmailKind kind, string locale, EmailTemplateRequest request, CancellationToken cancellationToken)
    {
        var expected = EmailApi.ExpectedVersion(request.ExpectedVersion);
        var actor = EmailValidation.Actor(request.Actor);
        var fields = EmailValidation.Fields(kind, request.Fields);
        var saved = await settings.SaveAsync(OidcIssuer.InstanceId, expected, actor, $"template:{AuthEmails.Name(kind)}/{locale}",
            current => current with { Templates = EmailApi.WithTemplate(current.Templates, kind, locale, fields) }, cancellationToken);
        return EmailApi.Answer(EmailApi.Saved(saved, expected), config => TypedResults.Json(EmailApi.Template(config, kind, locale)));
    }
}

public sealed class PreviewEmailEndpoint(EmailSettings settings, AuthEmails emails, AuthOptions options, TimeProvider clock) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/v1/{slug}/email/preview");
        Name("email_preview");
        Summary("Preview an email");
        Description("Renders kind in locale with synthetic values (code 123456; lockout until 15 minutes from now in UTC, without a code, link or expiry line) and the saved configuration, or the draft branding and fields given. Nothing is saved or sent. color_scheme light or dark pins an auto theme for the preview.");
        Tags("Email");
        Metadata(new AcceptsMetadata(["application/json"], typeof(EmailPreviewRequest)));
        Produces<EmailPreviewResponse>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status422UnprocessableEntity);
        Metadata(new RequiresSecurity(SecuritySchemes.ClientBasic));
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (EmailApi.Refuse(this.HttpContext, options) is { } refused)
        {
            return refused;
        }

        return (await EmailApi.ReadAsync<EmailPreviewRequest>(this.HttpContext, cancellationToken) switch
        {
            EmailPreviewRequest request => await RenderAsync(request, cancellationToken),
            Validation error => ErrorResults.ToResult(error),
            PayloadTooLarge error => ErrorResults.ToResult(error),
            UnsupportedMediaType error => ErrorResults.ToResult(error),
        });
    }

    private async Task<IResult> RenderAsync(EmailPreviewRequest request, CancellationToken cancellationToken)
    {
        var kind = EmailApi.Kind(request.Kind);
        var locale = EmailApi.Locale(request.Locale);
        var scheme = request.ColorScheme ?? "auto";
        if (scheme is not ("auto" or "light" or "dark"))
        {
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, "invalid_request", "color_scheme must be auto, light or dark.");
        }

        var config = EmailApi.Draft(await settings.GetAsync(OidcIssuer.InstanceId, cancellationToken), kind, locale, request.Branding, request.Fields);
        var message = await emails.RenderAsync(kind, locale, EmailApi.Sample(kind, "jane@example.com", options, clock.GetUtcNow()), config, scheme, cancellationToken);
        return TypedResults.Json(new EmailPreviewResponse(message.Subject, message.Html!, message.Text));
    }
}

public sealed class TestEmailEndpoint(EmailSettings settings, AuthEmails emails, IEmailSender email, AttemptLimiter limiter, AuthOptions options, TimeProvider clock) : EndpointWithoutRequest<IResult>
{
    public const int PerMinute = 10;

    public override void Configure()
    {
        Post("/v1/{slug}/email/test");
        Name("email_test_send");
        Summary("Send a test email");
        Description("Renders like preview (auto colour scheme) and sends it to `to` with the subject prefixed [Test]. 202 confirms provider acceptance, not inbox delivery. Rejected or unknown acceptance returns 502; do not automatically retry unknown acceptance. At most 10 per instance per minute; capacity refusals also return 429 with Retry-After.");
        Tags("Email");
        Metadata(new AcceptsMetadata(["application/json"], typeof(EmailTestRequest)));
        Produces<EmailTestResponse>(StatusCodes.Status202Accepted);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status422UnprocessableEntity);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
        Produces<ErrorBody>(StatusCodes.Status502BadGateway);
        Metadata(new RequiresSecurity(SecuritySchemes.ClientBasic));
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (EmailApi.Refuse(this.HttpContext, options) is { } refused)
        {
            return refused;
        }

        return (await EmailApi.ReadAsync<EmailTestRequest>(this.HttpContext, cancellationToken) switch
        {
            EmailTestRequest request => await SendAsync(request, cancellationToken),
            Validation error => ErrorResults.ToResult(error),
            PayloadTooLarge error => ErrorResults.ToResult(error),
            UnsupportedMediaType error => ErrorResults.ToResult(error),
        });
    }

    private async Task<IResult> SendAsync(EmailTestRequest request, CancellationToken cancellationToken)
    {
        var kind = EmailApi.Kind(request.Kind);
        var locale = EmailApi.Locale(request.Locale);
        var to = EmailValidation.Address("to", request.To) ?? throw new ApiException(StatusCodes.Status422UnprocessableEntity, "invalid_request", "to is required.");
        EmailValidation.Actor(request.Actor);
        var config = EmailApi.Draft(await settings.GetAsync(OidcIssuer.InstanceId, cancellationToken), kind, locale, request.Branding, request.Fields);
        if (!await limiter.TryAcquireAsync(OidcIssuer.InstanceId, "email-test", OidcIssuer.InstanceId, PerMinute, cancellationToken))
        {
            this.HttpContext.Response.Headers.RetryAfter = "60";
            return EmailApi.Error(StatusCodes.Status429TooManyRequests, "rate_limited", $"At most {PerMinute} test emails per minute.");
        }

        var message = await emails.RenderAsync(kind, locale, EmailApi.Sample(kind, to, options, clock.GetUtcNow()), config, cancellationToken: cancellationToken);
        message = message with { Subject = "[Test] " + message.Subject, Purpose = EmailPurpose.Test };
        var outcome = await email.DeliverAsync(to, message, cancellationToken);
        if (outcome == EmailDeliveryOutcome.Retryable)
        {
            this.HttpContext.Response.Headers.RetryAfter = "60";
            return EmailApi.Error(StatusCodes.Status429TooManyRequests, "rate_limited", "Email capacity is reserved for authentication; try later.");
        }
        if (outcome != EmailDeliveryOutcome.Accepted)
        {
            return outcome == EmailDeliveryOutcome.Rejected
                ? EmailApi.Error(StatusCodes.Status502BadGateway, "email_rejected", "The email provider rejected the test message.")
                : EmailApi.Error(StatusCodes.Status502BadGateway, "email_acceptance_unknown", "The email provider did not confirm acceptance; do not automatically retry.");
        }
        return TypedResults.Json(new EmailTestResponse(to, message.Subject), EmailEndpointJson.Default.EmailTestResponse, statusCode: StatusCodes.Status202Accepted);
    }
}

public sealed class ListEmailVersionsEndpoint(EmailSettings settings, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/v1/{slug}/email/versions");
        Name("email_versions_list");
        Summary("List email configuration versions");
        Description("The newest saved versions, newest first: limit (1 to 100, default 20). Version 0, the defaults, is not listed but can be read and restored.");
        Tags("Email");
        Produces<EmailVersionListResponse>();
        Produces<ErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status422UnprocessableEntity);
        Metadata(new RequiresSecurity(SecuritySchemes.ClientBasic));
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (EmailApi.Refuse(this.HttpContext, options) is { } refused)
        {
            return refused;
        }

        var limit = 20;
        var values = this.HttpContext.Request.Query["limit"];
        if (values.Count > 1 || values is [{ } value]
            && (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 100))
        {
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, "invalid_request", "limit must be given once, as 1 to 100.");
        }

        var versions = await settings.ListVersionsAsync(OidcIssuer.InstanceId, limit, cancellationToken);
        return TypedResults.Json(new EmailVersionListResponse([.. versions.Select(version => new EmailVersionSummary(version.Version, version.CreatedAt, version.Actor, version.Change))]));
    }
}

public sealed class GetEmailVersionEndpoint(EmailSettings settings, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/v1/{slug}/email/versions/{version}");
        Name("email_version_get");
        Summary("Read an email configuration version");
        Description("The branding and overridden templates of one version; version 0 is the defaults.");
        Tags("Email");
        Produces<EmailConfig>();
        Produces<ErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Metadata(new RequiresSecurity(SecuritySchemes.ClientBasic));
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        EmailApi.Refuse(this.HttpContext, options)
        ?? await Find(settings, this.HttpContext, cancellationToken) switch
        {
            EmailConfig config => TypedResults.Json(config),
            NotFound error => ErrorResults.ToResult(error),
        };

    internal static async Task<Found<EmailConfig>> Find(EmailSettings settings, HttpContext context, CancellationToken cancellationToken) =>
        long.TryParse(context.GetRouteValue("version") as string, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var version)
        && await settings.GetVersionAsync(OidcIssuer.InstanceId, version, cancellationToken) is { } config
            ? config
            : new NotFound("No such version.");
}

public sealed class RestoreEmailVersionEndpoint(EmailSettings settings, AuthOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/v1/{slug}/email/versions/{version}/restore");
        Name("email_version_restore");
        Summary("Roll back the email configuration");
        Description("Saves version's branding and templates as a new version (change rollback:{version}) if the configuration is still at expected_version. History is never rewritten.");
        Tags("Email");
        Metadata(new AcceptsMetadata(["application/json"], typeof(EmailRestoreRequest)));
        Produces<EmailConfig>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status422UnprocessableEntity);
        Metadata(new RequiresSecurity(SecuritySchemes.ClientBasic));
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (EmailApi.Refuse(this.HttpContext, options) is { } refused)
        {
            return refused;
        }

        return (await GetEmailVersionEndpoint.Find(settings, this.HttpContext, cancellationToken) switch
        {
            EmailConfig snapshot => await EmailApi.ReadAsync<EmailRestoreRequest>(this.HttpContext, cancellationToken) switch
            {
                EmailRestoreRequest request => await SaveAsync(snapshot, request, cancellationToken),
                Validation error => ErrorResults.ToResult(error),
                PayloadTooLarge error => ErrorResults.ToResult(error),
                UnsupportedMediaType error => ErrorResults.ToResult(error),
            },
            NotFound error => ErrorResults.ToResult(error),
        });
    }

    private async Task<IResult> SaveAsync(EmailConfig snapshot, EmailRestoreRequest request, CancellationToken cancellationToken)
    {
        var expected = EmailApi.ExpectedVersion(request.ExpectedVersion);
        var actor = EmailValidation.Actor(request.Actor);
        var saved = await settings.SaveAsync(OidcIssuer.InstanceId, expected, actor, $"rollback:{snapshot.Version}",
            current => current with { Branding = snapshot.Branding, Templates = snapshot.Templates }, cancellationToken);
        return EmailApi.Answer(EmailApi.Saved(saved, expected), static config => TypedResults.Json(config));
    }
}
