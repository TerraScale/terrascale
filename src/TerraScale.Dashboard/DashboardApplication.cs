using TerraScale.Endpoints;
using RequestIdentity = TerraScale.Dashboard.Platform.Http.RequestIdentity;
using ExceptionFrames = TerraScale.Dashboard.Platform.Http.ExceptionFrames;
using TerraScale.Dashboard.Platform.Observability;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Routing;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Features.Billing.Webhooks;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Features.Data;
using TerraScale.Dashboard.Features.Backups;
using TerraScale.Dashboard.Features.Organizations.Invitations;
using TerraScale.Dashboard.Features.Organizations.Audit;
using TerraScale.Dashboard.Features.Organizations.Projects;
using TerraScale.Dashboard.Features.Onboarding;
using TerraScale.Dashboard.Features.Home;
using TerraScale.Dashboard.Features.Notifications;
using TerraScale.Dashboard.Features.Profile;
using TerraScale.Dashboard.Features.Support;
using TerraScale.Dashboard.Features.Organizations;
using TerraScale.Dashboard.Features.AuthenticationManagement;
using TerraScale.Dashboard.Features.ApiKeys;
using TerraScale.Dashboard.Features.Observability;
using TerraScale.Dashboard.Features.Analytics;
using TerraScale.Dashboard.Features.Usage;
using TerraScale.Dashboard.Features.Settings;
using TerraScale.Dashboard.Features.FileStorage;
using TerraScale.Dashboard.Features.Functions;
using TerraScale.Dashboard.Features.Logs;
using TerraScale.Dashboard.Features.Organizations.Sso;
using TerraScale.Dashboard.Features.Organizations.Wizard;
using TerraScale.Dashboard.Platform.Contracts;
using TerraScale.Dashboard.Platform.Assets;
using TerraScale.Dashboard.Platform.Development;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.Mcp;
using TerraScale.Dashboard.Platform.Rpc;
using TerraScale.Dashboard.Platform.TsAuth;
using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard
{
    public static class DashboardApplication
    {
        private const string ConflictRetry = "This changed in another tab or request at the same time. Try again.";

        // The service override is for isolated test hosts; production has one service graph.
        public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configureTestHost = null)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                WebRootPath = "web/static"
            });
            // The console formatter's fixed setting retains sanitized request/trace scopes in structured timings.
            // UTC timestamps place each line against ts-auth and TerraBase logs and ts-orch rollouts.
            builder.Logging.AddJsonConsole(options =>
            {
                options.IncludeScopes = true;
                options.UseUtcTimestamp = true;
                options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
            });
            // A stalled stdout drops log lines instead of blocking every request on the full console queue.
            builder.Services.Configure<Microsoft.Extensions.Logging.Console.ConsoleLoggerOptions>(options =>
                options.QueueFullMode = Microsoft.Extensions.Logging.Console.ConsoleLoggerQueueFullMode.DropWrite);
            builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
            builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
            builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
            builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);
            builder.Services.AddSafeHttpLogging();
            builder.Services.AddSingleton<AssetUrls>();
            builder.Services.AddResponseCompression(options =>
            {
                // ASP.NET's required third-party setting: the loopback TLS edge forwards HTTPS requests.
                options.EnableForHttps = true;
            });
            builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = problem =>
                problem.ProblemDetails.Extensions["requestId"] = problem.HttpContext.TraceIdentifier);
            builder.Services.AddSingleton<ServiceSloTelemetry>();
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<IDashboardIdentity, DashboardIdentity>();
            builder.Services.AddDashboardStorage();
            builder.Services.AddSingleton<TerraBasePlanSync>();
            builder.Services.AddSingleton<DashboardStateInitializer>();
            builder.Services.AddHostedService(services => services.GetRequiredService<DashboardStateInitializer>());
            builder.Services.AddSingleton<DependencyReadiness>();
            builder.Services.AddSingleton<DashboardSessions>();
            builder.Services.AddAccountFeatures();
            builder.Services.AddAllowanceNotifications();
            builder.Services.AddSupport();
            builder.Services.AddCustomerDelivery();
            builder.Services.AddAlerts();
            builder.Services.AddAuth();
            builder.Services.AddDatabaseFeatures();
            builder.Services.AddOnboarding();
            builder.Services.AddBillingFeatures();
            builder.Services.AddHomeFeatures();
            builder.Services.AddAnalyticsFeatures();
            builder.Services.AddUsageFeatures();
            builder.Services.AddSettingsFeatures();
            builder.Services.AddFileStorage();
            builder.Services.AddOrganizationFeatures();
            builder.Services.AddSingleton<AccessTokens>();
            builder.Services.AddSingleton<IProfileTokens>(services => services.GetRequiredService<AccessTokens>());
            builder.Services.AddDashboardMcp();
            builder.Services.AddDashboardContracts();
            builder.Services.AddTransient<DevelopmentEventsEndpoint>();
            builder.Services.AddTerraScaleEndpoints(
            [
                EndpointRegistration.Create<DatabaseTtlEndpoint>(),
                EndpointRegistration.Create<HealthzEndpoint>(),
                EndpointRegistration.Create<ReadyzEndpoint>(),
                EndpointRegistration.Create<HealthReadyEndpoint>(),
                EndpointRegistration.Create<HealthLiveEndpoint>(),
                EndpointRegistration.Create<ApiKeysPageEndpoint>(),
                EndpointRegistration.Create<ApiKeyDetailsPageEndpoint>(),
                EndpointRegistration.Create<ListDashboardCredentialsEndpoint>(),
                EndpointRegistration.Create<CreateDashboardCredentialEndpoint>(),
                EndpointRegistration.Create<RevokeDashboardCredentialEndpoint>(),
                EndpointRegistration.Create<RevokeDashboardCredentialByIdEndpoint>(),
                EndpointRegistration.Create<RotateDashboardCredentialEndpoint>(),
                EndpointRegistration.Create<SetCredentialAllowedCidrsEndpoint>(),
                EndpointRegistration.Create<OrganizationPageEndpoint>(),
                EndpointRegistration.Create<DashboardContextEndpoint>(),
                EndpointRegistration.Create<RenameOrganizationEndpoint>(),
                EndpointRegistration.Create<CreateProjectEndpoint>(),
                EndpointRegistration.Create<CreateOrganizationEndpoint>(),
                EndpointRegistration.Create<RenameProjectEndpoint>(),
                EndpointRegistration.Create<InviteMemberEndpoint>(),
                EndpointRegistration.Create<MemberRoleEndpoint>(),
                EndpointRegistration.Create<MemberRemoveEndpoint>(),
                EndpointRegistration.Create<StartOwnershipTransferEndpoint>(),
                EndpointRegistration.Create<ConfirmOwnershipTransferEndpoint>(),
                EndpointRegistration.Create<CancelOwnershipTransferEndpoint>(),
                EndpointRegistration.Create<RevokeInvitationEndpoint>(),
                EndpointRegistration.Create<AccessInviteEndpoint>(),
                EndpointRegistration.Create<AccessRevokeInvitationEndpoint>(),
                EndpointRegistration.Create<AccessResendInvitationEndpoint>(),
                EndpointRegistration.Create<MemberRoleByIdEndpoint>(),
                EndpointRegistration.Create<MemberRemoveByIdEndpoint>(),
                EndpointRegistration.Create<AccessMemberRoleEndpoint>(),
                EndpointRegistration.Create<AccessMemberRemoveEndpoint>(),
                EndpointRegistration.Create<SetMfaPolicyEndpoint>(),
                EndpointRegistration.Create<LeaveOrganizationEndpoint>(),
                EndpointRegistration.Create<DeleteOrganizationEndpoint>(),
                EndpointRegistration.Create<DeleteProjectEndpoint>(),
                EndpointRegistration.Create<AccountDeletedEndpoint>(),
                EndpointRegistration.Create<ProfilePageEndpoint>(),
                EndpointRegistration.Create<SaveProfileEndpoint>(),
                EndpointRegistration.Create<SaveProfileLanguageEndpoint>(),
                EndpointRegistration.Create<DeleteProfileEndpoint>(),
                EndpointRegistration.Create<AccountDeletionProgressEndpoint>(),
                EndpointRegistration.Create<ProfileDeletionStatusEndpoint>(),
                EndpointRegistration.Create<ProfileDeletionContinueEndpoint>(),
                EndpointRegistration.Create<UnlinkProviderEndpoint>(),
                EndpointRegistration.Create<SendProviderCodeEndpoint>(),
                EndpointRegistration.Create<EmailChangeEndpoint>(),
                EndpointRegistration.Create<ConfirmEmailChangeEndpoint>(),
                EndpointRegistration.Create<SignOutEverywhereEndpoint>(),
                EndpointRegistration.Create<RevokeSessionEndpoint>(),
                EndpointRegistration.Create<CreateProfileAccessTokenEndpoint>(),
                EndpointRegistration.Create<RevokeProfileAccessTokenEndpoint>(),
                EndpointRegistration.Create<DashboardIdentityEndpoint>(),
                EndpointRegistration.Create<SwitchOrganizationEndpoint>(),
                EndpointRegistration.Create<SwitchProjectEndpoint>(),
                EndpointRegistration.Create<BillingRedirectEndpoint>(),
                EndpointRegistration.Create<CheckoutStartEndpoint>(),
                EndpointRegistration.Create<BillingPortalEndpoint>(),
                EndpointRegistration.Create<BillingChangeEndpoint>(),
                EndpointRegistration.Create<BillingChangeUndoEndpoint>(),
                EndpointRegistration.Create<BillingCheckoutCancelEndpoint>(),
                EndpointRegistration.Create<CheckoutSessionCancelEndpoint>(),
                EndpointRegistration.Create<BillingCheckoutReturnEndpoint>(),
                EndpointRegistration.Create<StripeWebhookEndpoint>(),
                EndpointRegistration.Create<AuthStripeWebhookEndpoint>(),
                EndpointRegistration.Create<DatabasesPageEndpoint>(),
                EndpointRegistration.Create<ListDatabasesApiEndpoint>(),
                EndpointRegistration.Create<DatabaseOverviewEndpoint>(),
                EndpointRegistration.Create<DatabaseTablesEndpoint>(),
                EndpointRegistration.Create<DatabaseGrantsPageEndpoint>(),
                EndpointRegistration.Create<DatabaseReplicationEndpoint>(),
                EndpointRegistration.Create<DatabaseConflictsEndpoint>(),
                EndpointRegistration.Create<DatabaseBackupsPageEndpoint>(),
                EndpointRegistration.Create<DatabaseBranchesEndpoint>(),
                EndpointRegistration.Create<DatabaseDangerEndpoint>(),
                EndpointRegistration.Create<CreateDatabaseEndpoint>(),
                EndpointRegistration.Create<BranchDatabaseEndpoint>(),
                EndpointRegistration.Create<DeleteDatabaseEndpoint>(),
                EndpointRegistration.Create<SqlPageEndpoint>(),
                EndpointRegistration.Create<SavedQueriesListEndpoint>(),
                EndpointRegistration.Create<RunSqlEndpoint>(),
                EndpointRegistration.Create<CheckSqlEndpoint>(),
                EndpointRegistration.Create<SqlCatalogEndpoint>(),
                EndpointRegistration.Create<SaveQueryEndpoint>(),
                EndpointRegistration.Create<DeleteSavedQueryEndpoint>(),
                EndpointRegistration.Create<ListBackupsEndpoint>(),
                EndpointRegistration.Create<BackupRestoreStatusEndpoint>(),
                EndpointRegistration.Create<CreateBackupEndpoint>(),
                EndpointRegistration.Create<BackupPolicyEndpoint>(),
                EndpointRegistration.Create<RestoreBackupEndpoint>(),
                EndpointRegistration.Create<BackupDestinationEndpoint>(),
                EndpointRegistration.Create<BackupDestinationRemoveEndpoint>(),
                EndpointRegistration.Create<NotificationsPageEndpoint>(),
                EndpointRegistration.Create<NotificationDialogEndpoint>(),
                EndpointRegistration.Create<MarkAllNotificationsReadEndpoint>(),
                EndpointRegistration.Create<MarkNotificationReadEndpoint>(),
                EndpointRegistration.Create<ExportAuthUsersEndpoint>(),
                EndpointRegistration.Create<AuthManagementPostEndpoint>(),
                EndpointRegistration.Create<AuthPageEndpoint>(),
                EndpointRegistration.Create<AuthenticationPageEndpoint>(),
                EndpointRegistration.Create<AuthSectionEndpoint>(),
                EndpointRegistration.Create<AuthenticationSectionEndpoint>(),
                EndpointRegistration.Create<InvitationPageEndpoint>(),
                EndpointRegistration.Create<InvitationRespondEndpoint>(),
                EndpointRegistration.Create<InvitationSwitchAccountEndpoint>(),
                EndpointRegistration.Create<AuditRedirectEndpoint>(),
                EndpointRegistration.Create<AuditEventsEndpoint>(),
                EndpointRegistration.Create<AuditExportEndpoint>(),
                EndpointRegistration.Create<UsagePageEndpoint>(),
                EndpointRegistration.Create<SpendingCapEndpoint>(),
                EndpointRegistration.Create<SupportPageEndpoint>(),
                EndpointRegistration.Create<SupportRequestEndpoint>(),
                EndpointRegistration.Create<ProjectInvitationsPageEndpoint>(),
                EndpointRegistration.Create<RevokeProjectInvitationEndpoint>(),
                EndpointRegistration.Create<AnalyticsPageEndpoint>(),
                EndpointRegistration.Create<DatabaseGrantsEndpoint>(),
                EndpointRegistration.Create<DatabaseGrantsDeleteEndpoint>(),
                EndpointRegistration.Create<DatabasePlacementEndpoint>(),
                EndpointRegistration.Create<DatabaseSettingsPageEndpoint>(),
                EndpointRegistration.Create<RenameDatabaseEndpoint>(),
                EndpointRegistration.Create<FunctionsPageEndpoint>(),
                EndpointRegistration.Create<DeleteFunctionEndpoint>(),
                EndpointRegistration.Create<DeployFunctionEndpoint>(),
                EndpointRegistration.Create<InvokeFunctionEndpoint>(),
                EndpointRegistration.Create<HomePageEndpoint>(),
                EndpointRegistration.Create<HomeRegionsEndpoint>(),
                EndpointRegistration.Create<DatabaseLogsPageEndpoint>(),
                EndpointRegistration.Create<LogDrainEndpoint>(),
                EndpointRegistration.Create<LogDrainDeleteEndpoint>(),
                EndpointRegistration.Create<AlertsPageEndpoint>(),
                EndpointRegistration.Create<CreateAlertEndpoint>(),
                EndpointRegistration.Create<DeleteAlertEndpoint>(),
                EndpointRegistration.Create<AlertThresholdEndpoint>(),
                EndpointRegistration.Create<ProjectSettingsPageEndpoint>(),
                EndpointRegistration.Create<ProjectRenameEndpoint>(),
                EndpointRegistration.Create<ProjectMembersPageEndpoint>(),
                EndpointRegistration.Create<ProjectGrantPostEndpoint>(),
                EndpointRegistration.Create<OrganizationSsoSaveEndpoint>(),
                EndpointRegistration.Create<OrganizationSsoVerifyEndpoint>(),
                EndpointRegistration.Create<OrganizationSsoDeleteEndpoint>(),
                EndpointRegistration.Create<NewOrganizationEndpoint>(),
                EndpointRegistration.Create<StoragePageEndpoint>(),
                EndpointRegistration.Create<StorageBucketEndpoint>(),
                EndpointRegistration.Create<StorageRulesEndpoint>(),
                EndpointRegistration.Create<StorageDeleteBucketEndpoint>(),
                EndpointRegistration.Create<StorageUploadEndpoint>(),
                EndpointRegistration.Create<StorageCompleteEndpoint>(),
                EndpointRegistration.Create<StorageDeleteFileEndpoint>(),
                EndpointRegistration.Create<StorageDownloadEndpoint>(),
                EndpointRegistration.Create<ReferenceScriptEndpoint>(),
                EndpointRegistration.Create<LoginEndpoint>(),
                EndpointRegistration.Create<LoginCallbackEndpoint>(),
                EndpointRegistration.Create<AuthStepUpCallbackEndpoint>(),
                EndpointRegistration.Create<OrganizationChooseEndpoint>(),
                EndpointRegistration.Create<AuthLogoutEndpoint>(),
                EndpointRegistration.Create<RegisterEndpoint>(),
                EndpointRegistration.Create<OnboardingPageEndpoint>(),
                EndpointRegistration.Create<OnboardingStepEndpoint>(),
                EndpointRegistration.Create<McpProtectedResourceEndpoint>(),
                EndpointRegistration.Create<McpAuthorizationServerEndpoint>(),
                EndpointRegistration.Create<McpRegisterEndpoint>(),
                EndpointRegistration.Create<McpAuthorizeEndpoint>(),
                EndpointRegistration.Create<McpConsentEndpoint>(),
                EndpointRegistration.Create<McpTokenEndpoint>(),
                EndpointRegistration.Create<McpUnsupportedMethodEndpoint>(),
                EndpointRegistration.Create<McpRootUnsupportedMethodEndpoint>(),
            ]);
            builder.Services.AddTransient<ServerTiming.Handler>();
            builder.Services.AddTransient<RequestIdentity.Handler>();
            builder.Services.AddTransient<PlatformTransport.PreferHttp2>();
            // The pool itself recycles connections for DNS (PlatformTransport), so the factory keeps one handler and its warm pool.
            builder.Services.AddHttpClient("platform", client => client.Timeout = TimeSpan.FromSeconds(30))
                .ConfigurePrimaryHttpMessageHandler(PlatformTransport.CreateHandler)
                .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
                .AddHttpMessageHandler<ServerTiming.Handler>()
                .AddHttpMessageHandler<RequestIdentity.Handler>()
                .AddHttpMessageHandler<PlatformTransport.PreferHttp2>();
            // TerraBase is reached only over MagicOnion on its private listener (Platform/Rpc).
            builder.Services.AddSingleton<TerraBaseRpcClient>();
            builder.Services.AddSingleton(services => new TsAuthClient(services.GetRequiredService<IHttpClientFactory>().CreateClient("platform")));
            configureTestHost?.Invoke(builder);
            var app = builder.Build();
            RazorSliceWarmup.EnsureWarmed();

            var slo = app.Services.GetRequiredService<ServiceSloTelemetry>();
            var privateScrapeKey = new object();
            app.Use((context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/metrics"))
                {
                    context.Items[privateScrapeKey] = ServiceSloTelemetry.PrivateScrape(context);
                }

                return slo.ObserveAsync(context, next);
            });

            // ts-orch's TLS edge on the same host proxies over loopback. Only loopback peers (the default known
            // proxies) may set the scheme and client IP; a browser's own forwarding headers never reach past the edge.
            app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto });
            var timingLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ServerTiming");
            app.Use((context, next) => RequestIdentity.MiddlewareAsync(context, next, timingLogger));
            app.Use((context, next) => ServerTiming.MiddlewareAsync(context, next, timingLogger));
            app.Use(async (context, next) =>
            {
                var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
                context.Items["nonce"] = nonce;
                var path = context.Request.Path.Value ?? "";
                var editorStyles = path.StartsWith("/api/v1/dashboard/data/", StringComparison.Ordinal) ||
                    path.StartsWith("/databases/", StringComparison.Ordinal) && path.EndsWith("/sql", StringComparison.Ordinal) ||
                    path.StartsWith("/api/v1/dashboard/functions/", StringComparison.Ordinal) ||
                    path.StartsWith("/databases/", StringComparison.Ordinal) && path.EndsWith("/functions", StringComparison.Ordinal);
                var reference = context.Request.Path.StartsWithSegments("/reference");
                var fileStorage = path == "/storage" || path.StartsWith("/api/v1/dashboard/storage/", StringComparison.Ordinal);
                var fileOrigins = fileStorage ? " https://*.r2.cloudflarestorage.com" : "";
                var styles = reference ? $"style-src 'self' 'nonce-{nonce}'; style-src-attr 'unsafe-inline'" :
                    editorStyles ? $"style-src 'self' 'nonce-{nonce}'; style-src-attr 'unsafe-inline'; worker-src 'self'" : "style-src 'self'";
                // form-action covers the redirects that follow forms: sign-out to ts-auth, checkout and portal to Stripe,
                // and an MCP consent's return to the client's registered redirect URI.
                string Policy()
                {
                    return $"default-src 'none'; script-src 'self' 'nonce-{nonce}'; {(context.Items.ContainsKey("auth-branding") ? $"style-src 'self' 'nonce-{nonce}'" : styles)}; " +
                    $"img-src 'self' data:{(context.Items.ContainsKey("auth-branding") ? " https:" : "")}; font-src 'self'; connect-src 'self'" + fileOrigins + "; form-action 'self' https://auth.terrascale.tech https://checkout.stripe.com https://billing.stripe.com" + fileOrigins +
                    (context.Items[Platform.Mcp.McpOAuth.FormActionKey] is string client ? " " + client : "") + "; base-uri 'none'; frame-ancestors 'none'";
                }

                context.Response.OnStarting(() =>
                {
                    // Error handling can clear headers; preserve endpoint policies and restore the baseline before writing.
                    var headers = context.Response.Headers;
                    if (headers.Location is { Count: 1 } && headers.Location[0] is { } location)
                    {
                        headers.Location = WorkspaceUrl.PreserveRequest(context, location);
                    }

                    headers.XContentTypeOptions = "nosniff";
                    headers.XFrameOptions = "DENY";
                    headers.TryAdd("Referrer-Policy", "same-origin");
                    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
                    if (context.Request.IsHttps)
                    {
                        headers.StrictTransportSecurity = "max-age=31536000";
                    }

                    headers.TryAdd("Content-Security-Policy", Policy());
                    if (!context.Request.Path.StartsWithSegments("/js") &&
                        !context.Request.Path.StartsWithSegments("/css") &&
                        !context.Request.Path.StartsWithSegments("/fonts") &&
                        !context.Request.Path.StartsWithSegments("/brand") &&
                        !context.Request.Path.StartsWithSegments("/flags") &&
                        !context.Request.Path.StartsWithSegments("/img"))
                    {
                        headers.TryAdd("Cache-Control", "no-store");
                    }

                    return Task.CompletedTask;
                });
                try
                {
                    await next(context);
                }
                catch (Exception error) when (!context.Response.HasStarted &&
                                              (error is OrganizationMfaRequiredException || error is AccountAccessException { Code: TsAuthGateway.MfaRequiredCode }))
                {
                    // An organization that requires two-factor blocks a member without it with a page, never a failure.
                    var blocked = error as OrganizationMfaRequiredException;
                    var message = blocked?.Message ?? "This organization requires two-factor authentication.";
                    if (AcceptsJson(context))
                    {
                        await Results.Problem(message, statusCode: StatusCodes.Status403Forbidden, extensions: new Dictionary<string, object?>
                        { ["code"] = TsAuthGateway.MfaRequiredCode, ["manageUrl"] = TsAuthGateway.ManageMfaUrl }).ExecuteAsync(context);
                        return;
                    }
                    var view = MfaRequiredView.Create(new(new DocumentModel("Two-factor required", nonce),
                        blocked?.OrganizationName ?? "This organization", Csrf.Token(context), blocked?.Others ?? []));
                    view.StatusCode = StatusCodes.Status403Forbidden;
                    await ((IResult)view).ExecuteAsync(context);
                }
                catch (AccountAccessException error)
                {
                    if (context.Response.HasStarted)
                    {
                        throw;
                    }

                    await RequestError.Response(context, error.StatusCode, error.Message).ExecuteAsync(context);
                }
                catch (Exception error) when (error is HttpRequestException or IOException or JsonException or TimeoutException or Stripe.StripeException ||
                                              error is OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
                {
                    // Platform failures never select a demo store or leak response bodies/secrets.
                    app.Logger.DashboardDependencyFailedOn((context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "[unmatched]",
                        error.GetType().Name, (int?)(error as HttpRequestException)?.StatusCode);
                    if (context.Response.HasStarted)
                    {
                        throw;
                    }

                    if (AcceptsJson(context))
                    {
                        await Results.Problem("A TerraScale service is temporarily unavailable. Try again shortly.", statusCode: StatusCodes.Status503ServiceUnavailable).ExecuteAsync(context);
                        return;
                    }
                    var view = Common.UI.Slices.ServiceUnavailable.Create(new DocumentModel("Service unavailable", nonce));
                    view.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await ((IResult)view).ExecuteAsync(context);
                }
                catch (DbUpdateConcurrencyException) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
                {
                    app.Logger.DashboardRequestConflicted((context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "[unmatched]");
                    context.Response.Clear();
                    context.Response.Headers.RetryAfter = "1";
                    await RequestError.Response(context, StatusCodes.Status409Conflict, ConflictRetry).ExecuteAsync(context);
                }
                catch (Exception error) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
                {
                    app.Logger.DashboardRequestFailed((context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "[unmatched]", ExceptionFrames.Describe(error));
                    context.Response.Clear();
                    await RequestError.Response(context, StatusCodes.Status500InternalServerError).ExecuteAsync(context);
                }
            });
            app.Use((context, next) => context.Request.Path.StartsWithSegments("/metrics")
                ? slo.ScrapeAsync(context, context.Request.Path == "/metrics" && HttpMethods.IsGet(context.Request.Method) && context.Items[privateScrapeKey] is true)
                : next(context));
            // Browser forms and API clients both get an actionable body for otherwise empty failures.
            app.UseStatusCodePages(status => RequestError.Response(status.HttpContext,
                status.HttpContext.Response.StatusCode).ExecuteAsync(status.HttpContext));
            // Compress public assets only; signed-in HTML, secrets and streaming responses keep their own delivery.
            app.UseWhen(context => context.Request.Path.StartsWithSegments("/css") || context.Request.Path.StartsWithSegments("/js") ||
                context.Request.Path.StartsWithSegments("/brand") || context.Request.Path.StartsWithSegments("/flags"),
                assets => assets.UseResponseCompression());
            app.UseStaticFiles(new StaticFileOptions
            {
                OnPrepareResponse = response =>
                {
                    var assets = response.Context.RequestServices.GetRequiredService<AssetUrls>();
                    var version = assets.Version(response.Context.Request.Path.Value ?? "");
                    response.Context.Response.Headers.CacheControl =
                        version == response.Context.Request.Query["v"] && version.Length != 0
                            ? "public,max-age=31536000,immutable"
                            : "public,max-age=0,must-revalidate";
                }
            });
            // Until the state schema is initialized only health probes and assets answer; every other request is the
            // dependency 503 above, so nothing is admitted against a store that is not there yet.
            var stateInitializer = app.Services.GetService<DashboardStateInitializer>();
            app.Use((context, next) => stateInitializer is null or { Initialized: true } || context.Request.Path.StartsWithSegments("/healthz") ||
                context.Request.Path.StartsWithSegments("/readyz") || context.Request.Path.StartsWithSegments("/health")
                ? next(context) : throw new IOException("Dashboard state is initializing."));
            app.MapDatabaseFeatures();
            app.MapDashboardMcp();
            if (app.Environment.IsDevelopment())
            {
                app.Services.GetRequiredService<DevelopmentEventsEndpoint>().Map(app);
            }
            app.MapDashboardContracts();
            app.MapTerraScaleEndpoints();
            app.MapFallback(async (HttpContext context, DashboardSessions sessions, AccountService accounts) =>
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                if (context.Request.Method != "GET" || context.Request.Path.StartsWithSegments("/api") ||
                    context.Request.Headers.Accept.Any(value => value is not null && (value.Contains("application/json") || value.Contains("text/event-stream"))) ||
                    context.Request.Headers.ContainsKey("Datastar-Request"))
                {
                    return (IResult)Results.NotFound();
                }

                return await Features.NotFound.NotFoundEndpoints.PageAsync(context, sessions, accounts);
            });
            return app;
        }

        private static bool AcceptsJson(HttpContext context)
        {
            return RequestError.WantsJson(context);
        }
    }
}
