using System.Text.Json;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Errors;
using TerraScale.Dashboard.Platform.Datastar;
using TerraScale.TerraBase.Contracts;
using TerraScale.TerraBase.Contracts.Management;
using System.Diagnostics;

namespace TerraScale.Dashboard.Features.Rules
{
    public sealed record RulesComparison(TerraBaseRulesVersion Version, IReadOnlyList<RulesDiffLine> Lines);

    public sealed record RulesPage(ShellModel Shell, DatabaseRecord Database, TerraBaseRules? Current, string Source, string LoadedVersion,
        IReadOnlyList<TerraBaseRulesVersion> Versions, RulesRequest Playground)
    {
        public IReadOnlyList<TerraBaseRulesProblem> Problems { get; init; } = [];
        public IReadOnlyList<TerraBaseRulesProblem> Warnings { get; init; } = [];
        public TerraBaseRulesTestResult? Result { get; init; }
        public RulesComparison? Comparison { get; init; }
        public string Error { get; init; } = "";
        public string Notice { get; init; } = "";
        public string PlaygroundError { get; init; } = "";
        public bool Stale { get; init; }
        public bool CanWrite => Shell.Context.CanWriteProjectData;
        public string Path => DatabaseService.Path(Database.Id, "rules");

        public IReadOnlyList<string> Tables => Current?.Tables.ValueKind == JsonValueKind.Object
            ? [.. Current.Tables.EnumerateObject().Select(table => table.Name)]
            : [];

        public string Author(string userId) =>
            Shell.Context.Organization?.Members.GetValueOrDefault(userId) is { Email.Length: > 0 } member ? member.Email : userId;
    }

    public static class RulesEndpoints
    {
        public static readonly RulesRequest DefaultPlayground = new("read", "", "{\"uid\":\"user_1\",\"claims\":{},\"is_anonymous\":false}", "{}", "{}");

        public static IServiceCollection AddDatabaseRules(this IServiceCollection services)
        {
            return services.AddSingleton<RulesBackend>();
        }

        internal static async Task<IResult> PageAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, RulesBackend rules)
        {
            http.Response.Headers.CacheControl = "no-store";
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return AccountRequest.LoginRedirect(http);
            }

            DatabaseRecord database;
            try { database = DatabaseService.Database(context, http.Request.RouteValues["databaseId"]?.ToString() ?? ""); }
            catch (AccountAccessException error) { return DatabaseResults.PageFailure(http, context, error); }
            var compare = http.Request.Query["compare"].ToString();
            var notice = http.Request.Query["restored"].ToString() == "1" ? "Version restored." : "";
            return await RenderAsync(http, rules, context, database, null, DefaultPlayground, compare: compare, state: page => page with { Notice = notice });
        }

        internal static async Task<IResult> DeployAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, RulesBackend rules)
        {
            var (context, database, form, refusal) = await ReadAsync(http, sessions, accounts);
            if (refusal is not null)
            {
                return refusal;
            }

            var source = form["source"].ToString();
            var loaded = form["version"].ToString();
            var playground = Playground(form);
            try
            {
                return await rules.DeployAsync(context!, database!, loaded.Length == 0 ? null : loaded, source, http.RequestAborted) switch
                {
                    TerraBaseRulesDeploy { Rules: { } saved } => await RenderAsync(http, rules, context!, database!, (source, saved.Version), playground,
                        state: page => page with { Notice = "Rules deployed.", Warnings = saved.Warnings ?? [] }),
                    TerraBaseRulesDeploy notDeployed => await RenderAsync(http, rules, context!, database!, (source, loaded), playground,
                        state: page => page with { Error = "The rules were not deployed.", Problems = notDeployed.Problems }),
                    Validation invalid => await RenderAsync(http, rules, context!, database!, (source, loaded), playground, state: page => Refused(page, invalid.Message)),
                };
            }
            catch (Exception error) when (KnownFailure(error, http))
            {
                return await RenderAsync(http, rules, context!, database!, (source, loaded), playground, state: page => Refused(page, error));
            }
        }

        internal static async Task<IResult> TestAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, RulesBackend rules)
        {
            var (context, database, form, refusal) = await ReadAsync(http, sessions, accounts);
            if (refusal is not null)
            {
                return refusal;
            }

            var source = form["source"].ToString();
            var loaded = form["version"].ToString();
            var playground = Playground(form);
            try
            {
                return await rules.TestAsync(context!, database!, source, playground, http.RequestAborted) switch
                {
                    TerraBaseRulesTestResult result => await RenderAsync(http, rules, context!, database!, (source, loaded), playground, state: page => page with { Result = result }),
                    Validation invalid => await RenderAsync(http, rules, context!, database!, (source, loaded), playground,
                        state: page => page with { PlaygroundError = invalid.Message }),
                };
            }
            catch (Exception error) when (KnownFailure(error, http))
            {
                return await RenderAsync(http, rules, context!, database!, (source, loaded), playground,
                    state: page => page with { PlaygroundError = FailureText(error) });
            }
        }

        internal static async Task<IResult> RestoreAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, RulesBackend rules)
        {
            var (context, database, form, refusal) = await ReadAsync(http, sessions, accounts);
            if (refusal is not null)
            {
                return refusal;
            }

            var version = form["restore"].ToString();
            var current = form["version"].ToString();
            try
            {
                var restored = await rules.RestoreAsync(context!, database!, version, current, http.RequestAborted);
                return restored.Rules is not null
                    ? HttpRedirects.SeeOther(DatabaseService.Path(database!.Id, "rules") + "?restored=1")
                    : await RenderAsync(http, rules, context!, database!, null, DefaultPlayground,
                        state: page => page with { Error = "The version was not restored.", Problems = restored.Problems });
            }
            catch (Exception error) when (KnownFailure(error, http))
            {
                return await RenderAsync(http, rules, context!, database!, null, DefaultPlayground, state: page => Refused(page, error));
            }
        }

        private static RulesPage Refused(RulesPage page, Exception error) =>
            error is TerraBasePreconditionFailedException { ErrorCode: "version_mismatch" }
                ? page with { Stale = true, Error = "These rules changed since you loaded them." }
                : page with { Error = FailureText(error) };

        private static RulesPage Refused(RulesPage page, string message) =>
            page with { Error = message };

        private static async Task<(AccountContext? Context, DatabaseRecord? Database, IFormCollection Form, IResult? Refusal)> ReadAsync(HttpContext http, DashboardSessions sessions, AccountService accounts)
        {
            http.Response.Headers.CacheControl = "no-store";
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return (null, null, FormCollection.Empty, Results.Unauthorized());
            }

            IFormCollection form;
            switch (await FormRequests.ReadAsync(http, http.RequestAborted))
            {
                case Validation invalid:
                    return (context, null, FormCollection.Empty, FormRequests.Refused(http, invalid));
                case UnsupportedMediaType unsupported:
                    return (context, null, FormCollection.Empty, FormRequests.Refused(http, unsupported));
                case IFormCollection parsed:
                    form = parsed;
                    break;
                case null:
                    throw new UnreachableException();
            }
            if (!Csrf.Validate(http, form))
            {
                return (context, null, form, RequestError.FormExpired(http, DatabaseService.Path(form["databaseId"].ToString(), "rules"), Results.BadRequest("Invalid form token.")));
            }

            try { return (context, DatabaseService.Database(context, form["databaseId"].ToString()), form, null); }
            catch (AccountAccessException error) { return (context, null, form, DatabaseResults.PageFailure(http, context, error)); }
        }

        private static RulesRequest Playground(IFormCollection form) =>
            new(form["operation"].ToString(), form["table"].ToString(), form["auth"].ToString(), form["row"].ToString(), form["new"].ToString());

        private static async Task<IResult> RenderAsync(HttpContext http, RulesBackend rules, AccountContext context, DatabaseRecord database,
            (string Source, string Version)? draft, RulesRequest playground, Func<RulesPage, RulesPage> state, string compare = "")
        {
            TerraBaseRules? current = null;
            IReadOnlyList<TerraBaseRulesVersion> versions = [];
            RulesComparison? comparison = null;
            var error = "";
            var status = 200;
            try
            {
                current = await rules.CurrentAsync(context, database, http.RequestAborted);
                if (current is not null)
                {
                    versions = await rules.VersionsAsync(context, database, http.RequestAborted);
                    if (compare.Length > 0)
                    {
                        switch (await rules.VersionAsync(context, database, compare, http.RequestAborted))
                        {
                            case TerraBaseRules older:
                                comparison = new(versions.FirstOrDefault(item => item.Version == older.Version) ?? new(older.Version, older.UpdatedAt, older.UpdatedBy, ""),
                                    RulesDiff.Lines(RulesBackend.Source(older.Tables), RulesBackend.Source(current.Tables)));
                                break;
                            case Errors.NotFound missing:
                                error = missing.Message ?? "This rules version does not exist.";
                                status = 404;
                                break;
                        }
                    }
                }
            }
            catch (Exception failure) when (KnownFailure(failure, http))
            {
                error = FailureText(failure);
                status = DatabaseResults.Status(failure) is 403 or 404 ? DatabaseResults.Status(failure) : 503;
            }

            var source = draft?.Source ?? (current is null ? RulesBackend.EmptySource : RulesBackend.Source(current.Tables));
            var page = state(new RulesPage(ShellModel.For(http, context, database.Name + " · Rules"), database, current, source, draft?.Version ?? current?.Version ?? "",
                versions, playground)
            { Comparison = comparison });
            if (error.Length > 0)
            {
                page = page with { Error = page.Error.Length > 0 ? page.Error : error };
            }

            if (http.Request.IsDatastarRequest)
            {
                await DatastarEvents.PatchElementsAsync(http, RulesPatchView.Create(page));
                return Results.Empty;
            }

            var view = RulesView.Create(page);
            view.StatusCode = status;
            return view;
        }

        private static string FailureText(Exception error) =>
            error is AccountAccessException or TerraBaseValidationException ? error.Message
            : DatabaseResults.Status(error) is 401 or 403 ? "Rules need a role that can read this project's databases; deploying needs write access."
            : DatabaseResults.KnownFailure(error) ? DatabaseResults.Message(error)
            : "Rules are unavailable. Retry to load this database's rules.";

        private static bool KnownFailure(Exception error, HttpContext http) =>
            error is HttpRequestException or JsonException or IOException || DatabaseResults.KnownFailure(error) ||
            error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested;
    }
}
