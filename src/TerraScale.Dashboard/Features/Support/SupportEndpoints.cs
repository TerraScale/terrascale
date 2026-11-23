using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Errors;
using TerraScale.Dashboard.Platform.Datastar;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.Support
{
    public sealed record SupportRequest(string RequestId, string UserId, string OrganizationId, string? ProjectId, string? DatabaseId,
        string Plan, string Severity, string Subject, string Message);

    public sealed record SupportPage(ShellModel Shell, string Error = "", string Sent = "")
    {
        public SupportDraft Draft { get; init; } = new("", "", "", "normal", "", "", "");
    }

    public sealed record SupportDraft(string OrganizationId, string ProjectId, string DatabaseId, string Severity, string Subject, string Message, string RequestId, string RelatedRequestId = "");

    public sealed record SupportMailAccepted(string Status);

    public readonly union SupportMailResult(SupportMailAccepted, DashboardQuotaExceeded, DashboardValidation);

    public interface ISupportMail
    {
        Task<SupportMailResult> SendAsync(SupportRequest request, CancellationToken cancellationToken);
    }

    public sealed class SupportMail(IHttpClientFactory http, IDashboardIdentity identity) : ISupportMail
    {
        public async Task<SupportMailResult> SendAsync(SupportRequest request, CancellationToken cancellationToken)
        {
            var credentials = await identity.ReadAsync(cancellationToken);
            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(TsAuthOrigin.Value, "v1/terrascale/email/support")) { Content = JsonContent.Create(request) };
            message.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(
                Uri.EscapeDataString(credentials.AuthClientId) + ":" + Uri.EscapeDataString(credentials.AuthClientSecret))));
            using var response = await http.CreateClient("platform").SendAsync(message, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return new DashboardQuotaExceeded("You sent too many support requests this hour. Email support@terrascale.tech instead.");
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity)
            {
                return new DashboardValidation("Check the organization, project and message, then send again.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new IOException("Support email service is unavailable.");
            }

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            return body.TryGetProperty("status", out var value) && value.GetString() is "accepted" or "retryable" or "unknown" or "rejected"
                ? new SupportMailAccepted(value.GetString()!) : throw new IOException("Support email response is invalid.");
        }
    }

    public static class SupportEndpoints
    {
        public static IServiceCollection AddSupport(this IServiceCollection services)
        {
            return services.AddSingleton<ISupportMail, SupportMail>();
        }

        internal static async Task<IResult> SendAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, ISupportMail mail, ILogger<SupportMail> logger)
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return TypedResults.Unauthorized();
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, "/support", TypedResults.BadRequest("Invalid form token."));
            }

            var draft = new SupportDraft(form["organizationId"].ToString(), form["projectId"].ToString(), form["databaseId"].ToString(),
                form["severity"].ToString(), form["subject"].ToString().Trim(), form["message"].ToString().Trim(), form["requestId"].ToString(), form["relatedRequestId"].ToString());
            var organization = context.Organizations.FirstOrDefault(item => item.Id == draft.OrganizationId && item.RoleFor(context.UserId) != "");
            if (organization is null)
            {
                return await RefusedAsync(http, context, draft, "Choose one of your organizations.", 422);
            }

            var project = draft.ProjectId.Length == 0 ? null : organization.Projects.GetValueOrDefault(draft.ProjectId);
            if (draft.ProjectId.Length > 0 && project is null)
            {
                return await RefusedAsync(http, context, draft, "Choose a project in the selected organization.", 422);
            }

            if (draft.DatabaseId.Length > 0 && project?.Databases.ContainsKey(draft.DatabaseId) != true)
            {
                return await RefusedAsync(http, context, draft, "Choose a database in the selected project.", 422);
            }

            if (draft.Severity is not ("low" or "normal" or "high" or "urgent"))
            {
                return await RefusedAsync(http, context, draft, "Choose a severity.", 422);
            }

            if (draft.Subject.Length is 0 or > 150 || draft.Subject.Any(char.IsControl))
            {
                return await RefusedAsync(http, context, draft, "Enter a one-line subject of at most 150 characters.", 422);
            }

            if (draft.Message.Length == 0 || draft.Message.Length + (draft.RelatedRequestId.Length == 0 ? 0 : 39) > 8000)
            {
                return await RefusedAsync(http, context, draft, "Describe the problem in at most 8000 characters.", 422);
            }

            if (!TerraScale.Dashboard.Common.Accounts.TerraScaleIds.IsValid(draft.RequestId))
            {
                return await RefusedAsync(http, context, draft, "Reload the page and send again.", 422);
            }

            if (draft.RelatedRequestId.Length > 0 && !TerraScale.Dashboard.Common.Accounts.TerraScaleIds.IsValid(draft.RelatedRequestId))
            {
                return await RefusedAsync(http, context, draft, "Enter a 26-character request ULID.", 422);
            }

            try
            {
                var delivery = await mail.SendAsync(new(draft.RequestId, context.UserId, organization.Id, project?.Id, draft.DatabaseId.Length == 0 ? null : draft.DatabaseId,
                    PlanCatalog.For(organization)?.Key ?? "free", draft.Severity, draft.Subject, draft.Message + (draft.RelatedRequestId.Length == 0 ? "" : "\nRequest ID: " + draft.RelatedRequestId)), http.RequestAborted);
                return delivery switch
                {
                    SupportMailAccepted accepted => await DeliveredAsync(accepted),
                    DashboardQuotaExceeded error => await RefusedAsync(http, context, draft, error.Message, 429),
                    DashboardValidation error => await RefusedAsync(http, context, draft, error.Message, 422),
                };
            }
            catch (Exception error) when (error is AccountAccessException or HttpRequestException or IOException or JsonException)
            {
                if (error is not AccountAccessException)
                {
                    logger.ASupportRequestCouldNotBe(error.GetType().Name);
                }

                return await RefusedAsync(http, context, draft, error is AccountAccessException access ? access.Message : "Support email is unavailable. Send again, or email support@terrascale.tech.",
                    error is AccountAccessException { StatusCode: var code } ? code : 503);
            }

            async Task<IResult> DeliveredAsync(SupportMailAccepted accepted)
            {
                if (accepted.Status != "accepted")
                {
                    throw new AccountAccessException("Your request could not be delivered. Send again, or email support@terrascale.tech.", 503);
                }

                if (http.Request.Headers["Datastar-Request"] == "true")
                {
                    await DatastarEvents.PatchElementsAsync(http, SupportContent.Create(new(ShellModel.For(http, context, "Support"), Sent: draft.RequestId[^8..])
                    {
                        Draft = new(draft.OrganizationId, draft.ProjectId, draft.DatabaseId, "normal", "", "", AccountService.NewId())
                    }));
                    return TypedResults.Empty;
                }

                return HttpRedirects.SeeOther("/support?sent=" + draft.RequestId[^8..]);
            }
        }

        private static async Task<IResult> RefusedAsync(HttpContext http, AccountContext context, SupportDraft draft, string message, int status)
        {
            var page = new SupportPage(ShellModel.For(http, context, "Support"), message) { Draft = draft };
            if (http.Request.Headers["Datastar-Request"] == "true")
            {
                await DatastarEvents.PatchElementsAsync(http, SupportContent.Create(page));
                return TypedResults.Empty;
            }

            var slice = SupportView.Create(page);
            slice.StatusCode = status;
            return slice;
        }
    }
}
