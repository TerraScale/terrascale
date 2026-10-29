using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Features.Support
{
    public sealed record SupportRequest(string RequestId, string UserId, string OrganizationId, string? ProjectId, string? DatabaseId,
        string Plan, string Severity, string Subject, string Message);

    public sealed record SupportPage(ShellModel Shell, string Error = "", string Sent = "")
    {
        public SupportDraft Draft { get; init; } = new("", "", "", "normal", "", "", "");
    }

    public sealed record SupportDraft(string OrganizationId, string ProjectId, string DatabaseId, string Severity, string Subject, string Message, string RequestId, string RelatedRequestId = "");

    public interface ISupportMail
    {
        // accepted, retryable, unknown or rejected; throws AccountAccessException for a refusal the user can act on.
        Task<string> SendAsync(SupportRequest request, CancellationToken cancellationToken);
    }

    // ts-auth owns the support mailbox address, the requester's reply address and the per-user hourly limit.
    public sealed class SupportMail(IHttpClientFactory http, IDashboardIdentity identity) : ISupportMail
    {
        public async Task<string> SendAsync(SupportRequest request, CancellationToken cancellationToken)
        {
            var credentials = await identity.ReadAsync(cancellationToken);
            using var message = new HttpRequestMessage(HttpMethod.Post, "https://auth.terrascale.tech/v1/terrascale/email/support") { Content = JsonContent.Create(request) };
            message.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(
                Uri.EscapeDataString(credentials.AuthClientId) + ":" + Uri.EscapeDataString(credentials.AuthClientSecret))));
            using var response = await http.CreateClient("platform").SendAsync(message, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new AccountAccessException("You sent too many support requests this hour. Email support@terrascale.tech instead.", 429);
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity)
            {
                throw new AccountAccessException("Check the organization, project and message, then send again.", 422);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new IOException("Support email service is unavailable.");
            }

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            return body.TryGetProperty("status", out var value) && value.GetString() is "accepted" or "retryable" or "unknown" or "rejected"
                ? value.GetString()! : throw new IOException("Support email response is invalid.");
        }
    }

    public static class SupportEndpoints
    {
        public static IServiceCollection AddSupport(this IServiceCollection services)
        {
            return services.AddSingleton<ISupportMail, SupportMail>();
        }

        public static IEndpointRouteBuilder MapSupport(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/support", async Task<IResult> (HttpContext http, DashboardSessions sessions, AccountService accounts) =>
            {
                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return AccountRequest.LoginRedirect(http);
                }

                var sent = http.Request.Query["sent"].ToString();
                return SupportView.Create(new(ShellModel.For(http, context, "Support"), Sent: sent.Length == 8 && sent.All(char.IsAsciiLetterOrDigit) ? sent : "")
                {
                    Draft = new(context.Organization?.Id ?? "", context.Project?.Id ?? "", "", "normal", "", "", AccountService.NewId(), TerraScale.DotNet.TerraScaleIds.IsValid(http.Request.Query["relatedRequestId"].ToString()) ? http.Request.Query["relatedRequestId"].ToString() : "")
                });
            });
            endpoints.MapPost("/api/v1/dashboard/support", SendAsync);
            return endpoints;
        }

        private static async Task<IResult> SendAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, ISupportMail mail, ILogger<SupportMail> logger)
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return Results.BadRequest("Invalid form token.");
            }

            var draft = new SupportDraft(form["organizationId"].ToString(), form["projectId"].ToString(), form["databaseId"].ToString(),
                form["severity"].ToString(), form["subject"].ToString().Trim(), form["message"].ToString().Trim(), form["requestId"].ToString(), form["relatedRequestId"].ToString());
            try
            {
                var organization = context.Organizations.FirstOrDefault(item => item.Id == draft.OrganizationId && item.RoleFor(context.UserId) != "")
                    ?? throw new AccountAccessException("Choose one of your organizations.", 422);
                var project = draft.ProjectId.Length == 0 ? null : organization.Projects.GetValueOrDefault(draft.ProjectId)
                    ?? throw new AccountAccessException("Choose a project in the selected organization.", 422);
                if (draft.DatabaseId.Length > 0 && project?.Databases.ContainsKey(draft.DatabaseId) != true)
                {
                    throw new AccountAccessException("Choose a database in the selected project.", 422);
                }

                if (draft.Severity is not ("low" or "normal" or "high" or "urgent"))
                {
                    throw new AccountAccessException("Choose a severity.", 422);
                }

                if (draft.Subject.Length is 0 or > 150 || draft.Subject.Any(char.IsControl))
                {
                    throw new AccountAccessException("Enter a one-line subject of at most 150 characters.", 422);
                }

                if (draft.Message.Length == 0 || draft.Message.Length + (draft.RelatedRequestId.Length == 0 ? 0 : 39) > 8000)
                {
                    throw new AccountAccessException("Describe the problem in at most 8000 characters.", 422);
                }

                if (!TerraScale.DotNet.TerraScaleIds.IsValid(draft.RequestId))
                {
                    throw new AccountAccessException("Reload the page and send again.", 422);
                }

                if (draft.RelatedRequestId.Length > 0 && !TerraScale.DotNet.TerraScaleIds.IsValid(draft.RelatedRequestId))
                {
                    throw new AccountAccessException("Enter a 26-character request ULID.", 422);
                }

                var status = await mail.SendAsync(new(draft.RequestId, context.UserId, organization.Id, project?.Id, draft.DatabaseId.Length == 0 ? null : draft.DatabaseId,
                    PlanCatalog.For(organization)?.Key ?? "free", draft.Severity, draft.Subject, draft.Message + (draft.RelatedRequestId.Length == 0 ? "" : "\nRequest ID: " + draft.RelatedRequestId)), http.RequestAborted);
                if (status != "accepted")
                {
                    throw new AccountAccessException("Your request could not be delivered. Send again, or email support@terrascale.tech.", 503);
                }

                return HttpRedirects.SeeOther("/support?sent=" + draft.RequestId[^8..]);
            }
            catch (Exception error) when (error is AccountAccessException or HttpRequestException or IOException or JsonException)
            {
                if (error is not AccountAccessException)
                {
                    logger.ASupportRequestCouldNotBe(error.GetType().Name);
                }

                var slice = SupportView.Create(new(ShellModel.For(http, context, "Support"),
                    error is AccountAccessException access ? access.Message : "Support email is unavailable. Send again, or email support@terrascale.tech.")
                { Draft = draft });
                slice.StatusCode = error is AccountAccessException { StatusCode: var code } ? code : 503;
                return slice;
            }
        }
    }
}
