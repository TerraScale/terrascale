using System.Data;
using System.Security.Cryptography;
using TerraScale.Dashboard.Errors;
using TerraScale.Dashboard.Features.Profile;
using TerraScale.Dashboard.Platform.Mcp;
using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Features.Auth
{
    public static class AuthEndpoints
    {
        public static void AddAuth(this IServiceCollection services)
        {
            services.AddSingleton<AuthFlows>();
            services.AddSingleton<IAuthEmailChangeGateway>(provider => provider.GetRequiredService<TsAuthGateway>());
            services.AddSingleton<IAuthStepUpGateway>(provider => provider.GetRequiredService<TsAuthGateway>());
            services.AddSingleton<TsAuthGateway>();
            services.AddSingleton<IAuthGateway>(provider => provider.GetRequiredService<TsAuthGateway>());
            services.AddSingleton<IAuthOidcGateway>(provider => provider.GetRequiredService<TsAuthGateway>());
            services.AddSingleton<IAuthRefreshGateway>(provider => provider.GetRequiredService<TsAuthGateway>());
            services.AddSingleton<ITsAuthMfa>(provider => provider.GetRequiredService<TsAuthGateway>());
            services.AddSingleton<ITsAuthProviders>(provider => provider.GetRequiredService<TsAuthGateway>());
            services.AddSingleton<IAuthLogoutGateway>(provider => provider.GetRequiredService<TsAuthGateway>());
            services.AddSingleton<IPlatformRoleGateway>(provider => provider.GetRequiredService<TsAuthGateway>());
            services.AddSingleton<IProjectAccessTokens>(provider => provider.GetRequiredService<TsAuthGateway>());
            services.AddSingleton<ITsAuthPlatform>(provider => provider.GetRequiredService<TsAuthGateway>());
            services.AddSingleton<ITsAuthProjects>(provider => provider.GetRequiredService<TsAuthGateway>());
            services.AddSingleton<ITsAuthDeletions>(provider => provider.GetRequiredService<TsAuthGateway>());
            services.AddHostedService<DeletionReconciler>();
        }

        internal static async Task<IResult> StartAsync(HttpContext context, DashboardSessions sessions)
        {
            var returnTo = Csrf.LocalRedirect(context.Request.Query["returnTo"].ToString());
            if (await sessions.ReadAsync(context) is not null)
            {
                return HttpRedirects.SeeOther(returnTo);
            }

            if (context.Request.Query["emailChanged"] == "1")
            {
                context.Response.Headers["Refresh"] = "0;url=/login?returnTo=%2Fprofile";
                return EmailChangedView.Create(new("Sign-in email changed", DocumentModel.NonceFor(context)));
            }

            return await AuthorizeAsync(context, returnTo, signUp: context.Request.Query["signup"] == "1");
        }

        public static async Task<IResult> AuthorizeAsync(HttpContext context, string returnTo, string tokenName = "", int tokenDays = 0, bool signUp = false, TerraScale.Dashboard.Platform.Mcp.McpAuthorization? mcp = null, StepUpIntent? stepUp = null)
        {
            try
            {
                var flow = await context.RequestServices.GetRequiredService<AuthFlows>().StartOidcAsync(context, returnTo, tokenName, tokenDays, mcp, stepUp);
                var authorize = await context.RequestServices.GetRequiredService<IAuthOidcGateway>().AuthorizationUrlAsync(flow, context.RequestAborted);
                return TypedResults.Redirect(signUp ? HostedSignUp(authorize) : authorize);
            }
            catch (Exception error) when (DependencyFailure(error, context))
            { return Failure(context, returnTo, "Sign-in is unavailable right now. Try again in a moment.", 503, error.GetType().Name); }
        }

        private static string HostedSignUp(string authorize)
        {
            var uri = new Uri(authorize);
            return uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) is [var slug, "oauth", "authorize"]
                ? uri.GetLeftPart(UriPartial.Authority) + "/ui/" + slug + "/sign-up?next=" + Uri.EscapeDataString(uri.PathAndQuery)
                : authorize;
        }

        internal static async Task<IResult> CompleteAsync(HttpContext context, AuthFlows flows, IAuthOidcGateway auth, DashboardSessions sessions)
        {
            var taken = await flows.TakeOidcAsync(context, context.Request.Query["state"].ToString());
            if (taken is not OidcTake.Taken { Flow: var flow })
            {
                var returnTo = taken is OidcTake.Expired expired ? expired.ReturnTo : "/";
                if (await sessions.ReadAsync(context) is not null)
                {
                    return HttpRedirects.SeeOther(returnTo);
                }

                return taken is OidcTake.Expired
                    ? Failure(context, returnTo, "This sign-in took longer than an hour to finish. Select Try again; you usually won't need a new code.", 400, "FlowExpired")
                    : Failure(context, returnTo, "This sign-in was already used, or was started in another browser. Select Try again; you usually won't need a new code.", 400, "FlowUnknown");
            }

            if (context.Request.Query["error"].Count != 0)
            {
                return flow.Mcp is not null ? McpOAuth.Denied(flow.Mcp) : Failure(context, flow.ReturnTo, "ts-auth did not sign you in. Sign in again.", 401, "AuthorizationDenied");
            }

            try
            {
                var code = context.Request.Query["code"].ToString();
                var time = context.RequestServices.GetRequiredService<TimeProvider>();
                if (flow.TokenName.Length != 0)
                {
                    var tokenExpiresAt = AccessTokens.ExpiresAt(time, flow.TokenDays);
                    var exchanged = await auth.ExchangeAsync(code, flow, tokenExpiresAt, context.RequestAborted);
                    return exchanged switch
                    {
                        SessionRecord fresh => await AccessTokenEndpoints.IssueAsync(context, flow, fresh, tokenExpiresAt),
                        DashboardValidation error => ExchangeRefused(context, flow, error)
                    };
                }

                if (flow.Mcp is not null)
                {
                    var tokenExpiresAt = McpOAuth.TokenExpiresAt(time);
                    var exchanged = await auth.ExchangeAsync(code, flow, tokenExpiresAt, context.RequestAborted);
                    return exchanged switch
                    {
                        SessionRecord fresh => await McpOAuth.GrantAsync(context, flow.Mcp, fresh, flow.StepUp, tokenExpiresAt),
                        DashboardValidation error => ExchangeRefused(context, flow, error)
                    };
                }

                var signedIn = await auth.ExchangeAsync(code, flow, null, context.RequestAborted);
                return signedIn switch
                {
                    SessionRecord fresh => await CompleteSignInAsync(context, sessions, flow, fresh),
                    DashboardValidation error => ExchangeRefused(context, flow, error)
                };
            }
            catch (AccountAccessException error) when (flow.StepUp is not null || flow.Mcp is not null)
            { return Failure(context, flow.ReturnTo, error.Message, error.StatusCode, nameof(AccountAccessException)); }
            catch (SignInRejectedException)
            { return Failure(context, flow.ReturnTo, "ts-auth could not confirm this sign-in. Sign in again.", 401, nameof(SignInRejectedException)); }
            catch (DBConcurrencyException)
            { return Failure(context, flow.ReturnTo, "Another sign-in to this account finished at the same moment. Select Try again.", 409, nameof(DBConcurrencyException)); }
            catch (TerraScale.Dashboard.Platform.TsAuth.TsAuthException error) when (error.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                var seconds = Math.Clamp((int)Math.Ceiling(error.RetryAfter?.TotalSeconds ?? 60), 1, 3600);
                context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var wait = seconds > 120 ? $"{(long)Math.Ceiling(seconds / 60.0)} minutes" : $"{seconds} seconds";
                return Failure(context, flow.ReturnTo, $"Too many sign-in attempts. Try again in {wait}.", 429, "RateLimited");
            }
            catch (Exception error) when (DependencyFailure(error, context))
            { return Failure(context, flow.ReturnTo, "Sign-in could not finish right now. Try again in a moment.", 503, error.GetType().Name); }
        }

        internal static async Task<IResult> LogoutAsync(HttpContext context)
        {
            var form = await FormRequests.ReadAsync(context, context.RequestAborted);
            if (!Csrf.Validate(context, form))
            {
                return RequestError.FormExpired(context, "/", TypedResults.StatusCode(403));
            }

            return await SignOutAsync(context);
        }

        public static async Task<IResult> SignOutAsync(HttpContext context)
        {
            var sessions = context.RequestServices.GetRequiredService<DashboardSessions>();
            var auth = context.RequestServices.GetRequiredService<IAuthGateway>();
            var logout = context.RequestServices.GetRequiredService<IAuthLogoutGateway>();
            var session = await sessions.ReadAsync(context);
            if (session is not null)
            {
                await auth.RevokeAsync(session, context.RequestAborted);
            }

            await sessions.EndAsync(context);
            return HttpRedirects.SeeOther(await logout.EndSessionUrlAsync($"{context.Request.Scheme}://{context.Request.Host}", session?.IdToken, context.RequestAborted));
        }

        private static IResult ExchangeRefused(HttpContext context, OidcFlow flow, DashboardValidation error)
        {
            return flow.StepUp is not null || flow.Mcp is not null
                ? Failure(context, flow.ReturnTo, error.Message, StatusCodes.Status422UnprocessableEntity, nameof(AccountAccessException))
                : RequestError.Response(context, StatusCodes.Status422UnprocessableEntity, error.Message);
        }

        private static async Task<IResult> CompleteSignInAsync(HttpContext context, DashboardSessions sessions, OidcFlow flow, SessionRecord session)
        {
            var account = await EnsureAccountAsync(context, session);
            await sessions.StartAsync(context, session);
            var target = flow.ReturnTo == "/" && !account.OnboardingCompleted ? "/onboarding" : flow.ReturnTo;
            var offersChoice = !target.StartsWith("/api/", StringComparison.Ordinal) && !target.StartsWith("/onboarding", StringComparison.Ordinal) &&
                !target.StartsWith("/organizations/choose", StringComparison.Ordinal);
            var recorded = RecordSignInAsync(context, session, account);
            var workspaces = offersChoice && session.OrganizationPicker ? context.RequestServices.GetRequiredService<AccountService>().GetContextAsync(session, context.RequestAborted) : null;
            await recorded;
            if (workspaces is not null && (await workspaces).Organizations.Count > 1)
            {
                return HttpRedirects.SeeOther("/organizations/choose?returnTo=" + Uri.EscapeDataString(target));
            }

            return HttpRedirects.SeeOther(target);
        }

        private static Task RecordSignInAsync(HttpContext context, SessionRecord session, AccountRecord account)
        {
            if (account.ActiveOrganizationId.Length == 0)
            {
                return Task.CompletedTask;
            }

            var time = context.RequestServices.GetRequiredService<TimeProvider>();
            return context.RequestServices.GetRequiredService<AccountService>().RecordAsync(new(session, account, [], new() { Id = account.ActiveOrganizationId }, null),
                org => org.AuditEvents.Add(new(AccountService.NewId(), account.Email, "session.created", account.Email, "session", time.GetUtcNow())), context.RequestAborted);
        }

        private static async Task<AccountRecord> EnsureAccountAsync(HttpContext context, SessionRecord session)
        {
            try { return await context.RequestServices.GetRequiredService<AccountService>().EnsureAsync(session, context.RequestAborted); }
            catch
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await context.RequestServices.GetRequiredService<IAuthGateway>().RevokeAsync(session, cleanup.Token); }
                catch (Exception error)
                {
                    context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("DashboardAuth")
                        .RejectedAccountGrantCleanupFailed(error.GetType().Name);
                }
                throw;
            }
        }

        private static RazorSlices.RazorSlice<AuthFailurePage> Failure(HttpContext context, string returnTo, string error, int status, string cause)
        {
            var reference = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(5));
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("DashboardAuth").TsAuthSignInFailed(reference, cause);
            var view = AuthFailureView.Create(new(new("Sign in", DocumentModel.NonceFor(context)), error, reference, Csrf.LocalRedirect(returnTo)));
            view.StatusCode = status;
            return view;
        }

        private static bool DependencyFailure(Exception error, HttpContext context)
        {
            return error is HttpRequestException or IOException or System.Text.Json.JsonException or TimeoutException ||
            error is OperationCanceledException && !context.RequestAborted.IsCancellationRequested;
        }
    }
}
