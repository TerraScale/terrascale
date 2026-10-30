using System.Text.Json;
using System.Text.RegularExpressions;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Rpc;
using TerraScale.TerraBase.Contracts.Functions;
using TerraScale.TerraBase.Contracts;

namespace TerraScale.Dashboard.Features.Functions
{
    public sealed record FunctionRecord(string Name, string? Source, string? Cron, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    public sealed record FunctionInvocation(string Id, DateTimeOffset At, string Trigger, string Status, double DurationMs, IReadOnlyList<string> Lines, string? Error);

    public sealed partial class FunctionBackend(DatabaseService databases, IProjectAccessTokens tokens, TerraBaseRpcClient rpc)
    {
        public static bool ValidName(string name)
        {
            return NamePattern().IsMatch(name);
        }

        public static string InvokeUrl(AccountContext context, DatabaseRecord database, string name)
        {
            return "https://api.terrascale.tech/v1/projects/" + Uri.EscapeDataString(DatabaseService.Scope(context).ProjectId) +
                "/databases/" + Uri.EscapeDataString(database.Name) + "/functions/" + Uri.EscapeDataString(name) + "/invoke";
        }

        public async Task<IReadOnlyList<FunctionRecord>> ListAsync(AccountContext context, DatabaseRecord database, CancellationToken ct)
        {
            var target = await ClientAsync(context, database, false, ct);
            var result = await target.Client.ListAsync(target.ProjectId, target.Database);
            IReadOnlyList<FunctionRecord> functions = [.. result.Select(Record)];
            Validate(functions);
            return functions;
        }

        public async Task<FunctionRecord?> ReadAsync(AccountContext context, DatabaseRecord database, string name, CancellationToken ct)
        {
            CheckName(name);
            var target = await ClientAsync(context, database, false, ct);
            try
            {
                var result = Record(await target.Client.GetAsync(target.ProjectId, target.Database, name));
                Validate(result);
                return result;
            }
            catch (TerraBaseNotFoundException)
            {
                return null;
            }
        }

        public async Task<IReadOnlyList<FunctionInvocation>> LogsAsync(AccountContext context, DatabaseRecord database, string name, CancellationToken ct)
        {
            CheckName(name);
            var target = await ClientAsync(context, database, false, ct);
            var result = await target.Client.LogsAsync(target.ProjectId, target.Database, name, 25);
            IReadOnlyList<FunctionInvocation> logs = [.. result.Select(item => new FunctionInvocation(item.Id, item.At, item.Trigger,
                item.Status, item.DurationMs, item.Lines, item.Error))];
            Validate(logs);
            return logs;
        }

        public async Task<bool> DeleteAsync(AccountContext context, DatabaseRecord database, string name, CancellationToken ct)
        {
            CheckName(name);
            var target = await ClientAsync(context, database, true, ct);
            try
            {
                return await target.Client.DeleteAsync(target.ProjectId, target.Database, name);
            }
            catch (TerraBaseNotFoundException)
            {
                return false;
            }
        }

        private async Task<(IFunctionsService Client, string ProjectId, string Database)> ClientAsync(AccountContext context, DatabaseRecord database, bool write, CancellationToken ct)
        {
            context = await databases.CurrentAsync(context, ct);
            database = DatabaseService.Database(context, database.Id);
            if (write && !context.CanWriteProjectData)
            {
                throw new AccountAccessException("Write access is required.");
            }

            var scope = DatabaseService.Scope(context);
            if (PlatformDatabases.IsPlatform(scope.OrganizationId))
            {
                throw PlatformDatabases.ReadOnly();
            }

            var home = await databases.HomeAsync(context, database, ct);
            var access = await tokens.GetAsync(context.Session, scope.OrganizationId, scope.ProjectId, ["documents:read", "documents:write"], ct);
            return (rpc.User<IFunctionsService>(access, home.Id).WithCancellationToken(ct).WithDeadline(DateTime.UtcNow.AddSeconds(30)), scope.ProjectId, database.Name);
        }

        private static FunctionRecord Record(TerraBaseFunctionInfo function)
        {
            return new(function.Name, function.Source, function.Cron, function.CreatedAt, function.UpdatedAt);
        }

        private static void CheckName(string name)
        {
            if (!ValidName(name))
            {
                throw new AccountAccessException("This function does not exist.", 404);
            }
        }

        internal static void Validate(object? value)
        {
            var valid = value switch
            {
                FunctionRecord function => Valid(function),
                IReadOnlyList<FunctionRecord> functions => functions.Count <= 32 && functions.All(item => item is not null && Valid(item)),
                IReadOnlyList<FunctionInvocation> logs => logs.Count <= 100 && logs.All(item => item is not null && TerraScaleIds.IsValid(item.Id) &&
                    item.Trigger is "http" or "cron" && item.Status is "ok" or "error" && double.IsFinite(item.DurationMs) && item.DurationMs >= 0 &&
                    item.Lines is { Count: <= 50 } && item.Lines.All(line => line is not null)) &&
                    logs.SequenceEqual(logs.OrderByDescending(item => item.At).ThenByDescending(item => item.Id, StringComparer.Ordinal)),
                _ => false
            };
            if (!valid)
            {
                throw new JsonException("Functions returned an invalid response.");
            }
        }

        private static bool Valid(FunctionRecord function)
        {
            return ValidName(function.Name) && (function.Source?.Length ?? 0) <= 256 * 1024 &&
            (function.Cron is null || function.Cron.Length > 0);
        }

        [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,62}$")]
        private static partial Regex NamePattern();
    }
}
