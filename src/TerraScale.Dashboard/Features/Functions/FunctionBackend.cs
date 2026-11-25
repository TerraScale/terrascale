using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Clients;

namespace TerraScale.Dashboard.Features.Functions
{
    public sealed record FunctionRecord(string Name, string? Source, string? Cron, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    public sealed record FunctionInvocation(string Id, DateTimeOffset At, string Trigger, string Status, double DurationMs, IReadOnlyList<string> Lines, string? Error, int SqlCalls, long SqlMs, long RowsRead, long RowsWritten, long ResponseBytes, int? HttpStatus);

    public sealed partial class FunctionBackend(DatabaseService databases, IProjectAccessTokens tokens, PlatformHttpClient platform, ILogger<FunctionBackend> logger)
    {
        public const string NameInputPattern = "[a-z][a-z0-9_-]{0,62}";

        public const string SecretInputPattern = "[A-Z_][A-Z0-9_]{0,63}";

        public const string StarterSource = "async function handler(event) {\n  const rows = await sql.query(\"SELECT 1 AS ok\");\n  return { rows };\n}\n";

        public static bool ValidName(string name)
        {
            return NamePattern().IsMatch(name);
        }

        public static bool DeployName(string name)
        {
            return DeployNamePattern().IsMatch(name) && !ReservedName().IsMatch(name);
        }

        public static string InvokeUrl(AccountContext context, DatabaseRecord database, string name)
        {
            return "https://api.terrascale.tech/v1/projects/" + Uri.EscapeDataString(DatabaseService.Scope(context).ProjectId) +
                "/databases/" + Uri.EscapeDataString(database.Name) + "/functions/" + Uri.EscapeDataString(name) + "/invoke";
        }

        public async Task<IReadOnlyList<FunctionRecord>> ListAsync(AccountContext context, DatabaseRecord database, CancellationToken ct)
        {
            var target = await ClientAsync(context, database, false, WriteScopes, ct);
            var result = await target.Client.ListAsync(target.ProjectId, target.Database);
            IReadOnlyList<FunctionRecord> functions = [.. result.Select(Record)];
            Validate(functions);
            return functions;
        }

        public async Task<FunctionRecord?> ReadAsync(AccountContext context, DatabaseRecord database, string name, CancellationToken ct)
        {
            CheckName(name);
            var target = await ClientAsync(context, database, false, WriteScopes, ct);
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
            var target = await ClientAsync(context, database, false, WriteScopes, ct);
            var result = await target.Client.LogsAsync(target.ProjectId, target.Database, name, 25);
            IReadOnlyList<FunctionInvocation> logs = [.. result.Select(Invocation)];
            Validate(logs);
            return logs;
        }

        public async Task<TerraBaseFunctionStats> StatsAsync(AccountContext context, DatabaseRecord database, string name, string window, CancellationToken cancellationToken)
        {
            CheckName(name);
            if (window is not ("1h" or "24h" or "7d"))
            {
                throw new AccountAccessException("window must be 1h, 24h or 7d", 400);
            }

            var target = await ClientAsync(context, database, false, WriteScopes, cancellationToken);
            var stats = await target.Client.StatsAsync(target.ProjectId, target.Database, name, window);
            Validate(stats);
            if (stats.Window != window)
            {
                throw new JsonException("Functions returned the wrong stats window.");
            }

            return stats;
        }

        public async Task<bool> DeleteAsync(AccountContext context, DatabaseRecord database, string name, CancellationToken ct)
        {
            CheckName(name);
            var target = await ClientAsync(context, database, true, WriteScopes, ct);
            try
            {
                return await target.Client.DeleteAsync(target.ProjectId, target.Database, name);
            }
            catch (TerraBaseNotFoundException)
            {
                return false;
            }
        }

        public async Task<FunctionRecord> DeployAsync(AccountContext context, DatabaseRecord database, string name, string source, string? cron, CancellationToken ct)
        {
            ValidateDeploy(name, source);

            var target = await ClientAsync(context, database, true, WriteScopes, ct);
            var result = Record(await target.Client.DeployAsync(target.ProjectId, target.Database, name, new TerraBaseFunctionDeploy(source, cron)));
            Validate(result);
            return result;
        }

        public async Task<IReadOnlyList<TerraBaseFunctionSecret>> SecretsAsync(AccountContext context, DatabaseRecord database, string name, CancellationToken ct)
        {
            CheckName(name);
            var target = await ClientAsync(context, database, false, WriteScopes, ct);
            var secrets = await target.Client.ListSecretsAsync(target.ProjectId, target.Database, name);
            ValidateSecrets(secrets);
            return secrets;
        }

        public async Task<TerraBaseFunctionSecret> SetSecretAsync(AccountContext context, DatabaseRecord database, string name, string secret, string value, CancellationToken ct)
        {
            CheckName(name);
            ValidateSecret(secret, value);
            var target = await ClientAsync(context, database, true, WriteScopes, ct);
            var saved = await target.Client.SetSecretAsync(target.ProjectId, target.Database, name, secret, new TerraBaseFunctionSecretValue(value));
            ValidateSecrets([saved]);
            return saved;
        }

        public async Task<bool> DeleteSecretAsync(AccountContext context, DatabaseRecord database, string name, string secret, CancellationToken ct)
        {
            CheckName(name);
            ValidateSecretName(secret);
            var target = await ClientAsync(context, database, true, WriteScopes, ct);
            try
            {
                return await target.Client.DeleteSecretAsync(target.ProjectId, target.Database, name, secret);
            }
            catch (TerraBaseNotFoundException)
            {
                return false;
            }
        }

        internal static void ValidateSecretName(string secret)
        {
            if (!SecretNamePattern().IsMatch(secret))
            {
                throw new AccountAccessException("Secret names start with an uppercase letter or underscore and use at most 64 uppercase letters, digits or underscores.", 400);
            }
        }

        internal static void ValidateSecret(string secret, string value)
        {
            ValidateSecretName(secret);
            if (Encoding.UTF8.GetByteCount(value) is 0 or > 4 * 1024)
            {
                throw new AccountAccessException("value is required and at most 4 KiB", 400);
            }
        }

        internal static void ValidateSecrets(IReadOnlyList<TerraBaseFunctionSecret> secrets)
        {
            if (secrets is not { Count: <= 50 } || !secrets.All(secret => secret is not null && SecretNamePattern().IsMatch(secret.Name)))
            {
                throw new JsonException("Functions returned an invalid response.");
            }
        }

        public async Task<TerraBaseFunctionCall> InvokeAsync(AccountContext context, DatabaseRecord database, string name, string body, IReadOnlyDictionary<string, string>? query, CancellationToken ct)
        {
            CheckName(name);
            var target = await ClientAsync(context, database, false, ReadScopes, ct);
            return await target.Client.InvokeAsync(target.ProjectId, target.Database, name, body, query);
        }

        internal static void ValidateDeploy(string name, string source)
        {
            if (!DeployName(name))
            {
                throw new AccountAccessException("Function names start with a lowercase letter and use at most 63 lowercase letters, digits, underscores or hyphens.", 400);
            }

            if (Encoding.UTF8.GetByteCount(source) is 0 or > 256 * 1024)
            {
                throw new AccountAccessException("source is required and at most 256 KiB", 400);
            }
        }

        internal async Task<McpFunctionRun> InvokeForMcpAsync(AccountContext context, DatabaseRecord database, string name, string body, IReadOnlyDictionary<string, string>? query, CancellationToken ct)
        {
            CheckName(name);
            var target = await ClientAsync(context, database, false, ReadScopes, ct);
            var call = await target.Client.InvokeAsync(target.ProjectId, target.Database, name, body, query);
            return await FunctionMcpOutput.CompleteAsync(call, target.Context.CanWriteProjectData, ReadNewestAsync, logger, ct);

            async Task<IReadOnlyList<FunctionInvocation>> ReadNewestAsync(CancellationToken cancellationToken)
            {
                var logs = await ClientAsync(target.Context, database, false, WriteScopes, cancellationToken);
                return [.. (await logs.Client.LogsAsync(logs.ProjectId, logs.Database, name, 1)).Select(Invocation)];
            }
        }

        private async Task<(FunctionsClient Client, string ProjectId, string Database, AccountContext Context)> ClientAsync(AccountContext context, DatabaseRecord database, bool write, IReadOnlyList<string> scopes, CancellationToken ct)
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

            await databases.HomeAsync(context, database, ct);
            var access = await tokens.GetAsync(context.Session, scope.OrganizationId, scope.ProjectId, scopes, ct);
            return (new FunctionsClient(platform, ServiceCaller.User(access), ct), scope.ProjectId, database.Name, context);
        }

        internal static FunctionInvocation Invocation(TerraBaseFunctionInvocation item) =>
            new(item.Id, item.At, item.Trigger, item.Status, item.DurationMs, item.Lines, item.Error, item.SqlCalls, item.SqlMs,
                item.RowsRead, item.RowsWritten, item.ResponseBytes, item.HttpStatus);

        internal static FunctionRecord Record(TerraBaseFunctionInfo function)
        {
            return new(function.Name, function.Source, function.Cron, function.CreatedAt, function.UpdatedAt);
        }

        internal static void CheckName(string name)
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
                    item.SqlCalls >= 0 && item.SqlMs >= 0 && item.SqlMs <= item.DurationMs && item.RowsRead >= 0 &&
                    item.RowsWritten >= 0 && item.ResponseBytes >= 0 && (item.HttpStatus is null or >= 100 and <= 599) &&
                    item.Lines is { Count: <= 50 } && item.Lines.All(line => line is not null)) &&
                    logs.SequenceEqual(logs.OrderByDescending(item => item.At).ThenByDescending(item => item.Id, StringComparer.Ordinal)),
                TerraBaseFunctionStats stats => stats.Window is "1h" or "24h" or "7d" && stats.Invocations >= 0 &&
                    stats.Errors >= 0 && stats.Errors <= stats.Invocations && stats.P50Ms >= 0 && stats.P95Ms >= stats.P50Ms &&
                    stats.P99Ms >= stats.P95Ms && stats.MaxMs >= 0 && stats.SqlCalls >= 0 && stats.RowsRead >= 0 && stats.RowsWritten >= 0 &&
                    stats.Buckets is not null && stats.Buckets.Count == (stats.Window == "1h" ? 60 : stats.Window == "24h" ? 24 : 168) &&
                    stats.Buckets.All(bucket => bucket is not null && bucket.Invocations >= 0 && bucket.Errors >= 0 &&
                        bucket.Errors <= bucket.Invocations && bucket.P50Ms >= 0 && bucket.P95Ms >= bucket.P50Ms) &&
                    stats.Buckets.Zip(stats.Buckets.Skip(1)).All(pair => pair.Second.At - pair.First.At == TimeSpan.FromMinutes(stats.Window == "1h" ? 1 : 60)) &&
                    stats.Buckets.Sum(bucket => (decimal)bucket.Invocations) == stats.Invocations &&
                    stats.Buckets.Sum(bucket => (decimal)bucket.Errors) == stats.Errors,
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

        private static readonly string[] ReadScopes = ["sql:read"];

        private static readonly string[] WriteScopes = ["sql:read", "sql:write"];

        [GeneratedRegex(@"\A[a-z0-9][a-z0-9_-]{0,62}\z")]
        private static partial Regex NamePattern();

        [GeneratedRegex(@"\A[a-z][a-z0-9_-]{0,62}\z")]
        private static partial Regex DeployNamePattern();

        [GeneratedRegex(@"\A[A-Z_][A-Z0-9_]{0,63}\z")]
        private static partial Regex SecretNamePattern();

        [GeneratedRegex(@"\A(con|prn|aux|nul|com[0-9]|lpt[0-9])\z")]
        private static partial Regex ReservedName();
    }
}
