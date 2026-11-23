using TerraBaseDbConnection = TerraScale.DotNet.TerraBase.TerraBaseDbConnection;
using System.Data;
using Dapper;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Errors;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.Clients;
using TerraBaseSqlErrorCodes = TerraScale.DotNet.TerraBase.TerraBaseSqlErrorCodes;
using TerraBaseSqlException = TerraScale.DotNet.TerraBase.TerraBaseSqlException;

namespace TerraScale.Dashboard.Features.Notifications
{

    public sealed record AlertRule(string Id, string Name, string Metric, double Threshold, string? DatabaseId, bool Email, string? WebhookUrl,
        DateTimeOffset CreatedAt, string State = "ok", DateTimeOffset? Since = null, string? AlertId = null, double? Value = null);

    public sealed record AlertBucket(DateTimeOffset Start, long Requests, long Errors, double? P95Ms, long? StorageBytes);
    public sealed record AlertSeries(string DatabaseId, string DatabaseName, string Region, IReadOnlyList<AlertBucket> Buckets);
    public sealed record AlertAnalytics(DateTimeOffset To, IReadOnlyList<AlertSeries> Items);
    public sealed record AlertEmail(string OrganizationId, string ProjectId, string AlertId, string State, string RuleName, string Metric, string? DatabaseName, double Threshold, double Value);

    public readonly union AlertRuleCreated(AlertRuleWithSecret, DashboardValidation, DashboardConflict, DashboardForbidden, DashboardNotFound);

    public sealed record AlertRuleWithSecret(AlertRule Rule, string? Secret);

    public readonly union AlertRuleChanged(Done, DashboardValidation, DashboardNotFound, DashboardForbidden);

    public interface IAlertSource
    {
        Task<AlertAnalytics> ReadAnalyticsAsync(string projectId, CancellationToken cancellationToken);
        Task<IReadOnlyDictionary<string, long>> ReadOperationUnitsAsync(CancellationToken cancellationToken);
        Task<string> SendEmailAsync(AlertEmail email, CancellationToken cancellationToken);
    }

    public sealed class AlertRules(DashboardDb storage, OrganizationStore organizations, IAlertSource source, ICustomerDelivery delivery, IDashboardIdentity identity,
        TimeProvider clock, ILogger<AlertRules> logger, BackgroundJobs jobs, BillingSchemaGate gate)
    {
        private const string RulesOfProject = DashboardSql.AlertRules.Select + " WHERE project_id = @ProjectId ORDER BY id";
        private const string RulesOf = DashboardSql.AlertRules.Select + " WHERE project_id = @ProjectId AND organization_id = @OrganizationId ORDER BY id";
        private const string RuleCount = "SELECT count(*) FROM alert_rules WHERE project_id = @ProjectId AND organization_id = @OrganizationId";
        private const string Slots = "SELECT slot FROM alert_rules WHERE project_id = @ProjectId";
        private const string RemoveRule = "DELETE FROM alert_rules WHERE id = @Id AND organization_id = @OrganizationId AND project_id = @ProjectId";
        private const string SetThreshold = """
            UPDATE alert_rules SET threshold = @Threshold, version = @Version WHERE id = @Id AND organization_id = @OrganizationId AND project_id = @ProjectId
            """;
        private const string UpdateRule = """
            UPDATE alert_rules SET state = @State, since = @Since, alert_id = @AlertId, value = @Value, version = @Version WHERE id = @Id AND version = @OriginalVersion
            """;
        private const string EventsOf = DashboardSql.AlertEvents.Select
            + " WHERE rule_id IN (SELECT id FROM alert_rules WHERE project_id = @ProjectId AND organization_id = @OrganizationId) ORDER BY id";
        private const string JobById = BackgroundJobs.Snapshot + " WHERE id = @Id";

        public const int RuleLimit = 20;
        public const int WebhookAttempts = 3;
        private const int EventLimit = 10;
        public static readonly string[] Metrics = ["p95_latency_ms", "error_rate_percent", "storage_gb", "operation_units_percent"];

        public async Task<IReadOnlyList<AlertRule>> ListAsync(string projectId, CancellationToken cancellationToken)
        {
            await using var connection = await storage.OpenAsync(cancellationToken);
            var rows = await connection.QueryAsync<AlertRuleRow>(RulesOfProject, new { ProjectId = projectId });
            return [.. rows.Select(Rule)];
        }

        public async Task<AlertRuleCreated> CreateAsync(AccountContext context, IFormCollection form, CancellationToken cancellationToken)
        {
            if (Owner(context) is not { } owner)
            {
                return context.Organization is null || context.Project is null
                    ? new AlertRuleCreated(new DashboardNotFound("Select a project."))
                    : new AlertRuleCreated(new DashboardForbidden("Only organization owners manage alert rules."));
            }

            var (organization, project) = owner;
            var name = form["name"].ToString().Trim();
            if (name.Length is 0 or > 64 || name.Any(char.IsControl))
            {
                return new DashboardValidation("Name the rule in up to 64 characters.");
            }

            var metric = form["metric"].ToString();
            if (!Metrics.Contains(metric))
            {
                return new DashboardValidation("Choose a metric.");
            }

            if (Threshold(form["threshold"]) is not { } threshold)
            {
                return new DashboardValidation("Enter a threshold greater than 0.");
            }

            var databaseId = form["database"].ToString() is { Length: > 0 } selected ? selected : null;
            if (databaseId is not null && !project.Databases.ContainsKey(databaseId))
            {
                return new DashboardValidation("Choose a database of this project.");
            }

            var url = form["webhook"].ToString().Trim() is { Length: > 0 } text ? CustomerUrls.Validate(text).OriginalString : null;
            var id = AccountService.NewId();
            var secret = url is null ? null : WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var key = (await identity.ReadAsync(cancellationToken)).SessionEncryptionKey;
            var sealedSecret = secret is null ? null : SealedSecret.Seal(secret, key, Purpose(project.Id, id));
            var createdAt = clock.GetUtcNow().UtcDateTime;
            await using var connection = await storage.OpenAsync(cancellationToken);
            for (var attempt = 0; attempt < RuleLimit; attempt++)
            {
                var taken = (await connection.QueryAsync<int>(Slots, new { ProjectId = project.Id })).AsList();
                if (Enumerable.Range(0, RuleLimit).Except(taken).Select(slot => (int?)slot).FirstOrDefault() is not { } slot)
                {
                    break;
                }

                var row = new AlertRuleRow
                {
                    Id = id,
                    OrganizationId = organization.Id,
                    ProjectId = project.Id,
                    Slot = slot,
                    Name = name,
                    Metric = metric,
                    Threshold = threshold,
                    DatabaseId = databaseId,
                    Email = form["email"] == "on",
                    WebhookUrl = url,
                    WebhookNonce = sealedSecret?.Nonce,
                    WebhookCiphertext = sealedSecret?.Ciphertext,
                    WebhookTag = sealedSecret?.Tag,
                    CreatedAt = createdAt,
                    State = "ok",
                    Version = AccountService.NewId(),
                };
                try
                {
                    await connection.ExecuteAsync(DashboardSql.AlertRules.Insert, row);
                    return new AlertRuleWithSecret(Rule(row), secret);
                }
                catch (Exception error) when (DashboardDb.IsDuplicate(error))
                {
                }
            }

            return new DashboardConflict($"A project can have up to {RuleLimit} alert rules.");
        }

        public async Task<AlertRuleChanged> DeleteAsync(AccountContext context, string id, CancellationToken cancellationToken)
        {
            if (Owner(context) is not { } owner)
            {
                return context.Organization is null || context.Project is null
                    ? new AlertRuleChanged(new DashboardNotFound("Select a project."))
                    : new AlertRuleChanged(new DashboardForbidden("Only organization owners manage alert rules."));
            }

            var (organization, project) = owner;
            await using var connection = await storage.OpenAsync(cancellationToken);
            if (await connection.ExecuteAsync(RemoveRule, new { Id = id, OrganizationId = organization.Id, ProjectId = project.Id }) == 0)
            {
                return new DashboardNotFound("Alert rule is unavailable.");
            }

            return new Done();
        }

        public async Task<AlertRuleChanged> UpdateThresholdAsync(AccountContext context, string id, string? value, CancellationToken cancellationToken)
        {
            if (Owner(context) is not { } owner)
            {
                return context.Organization is null || context.Project is null
                    ? new AlertRuleChanged(new DashboardNotFound("Select a project."))
                    : new AlertRuleChanged(new DashboardForbidden("Only organization owners manage alert rules."));
            }

            var (organization, project) = owner;
            if (Threshold(value) is not { } threshold)
            {
                return new DashboardValidation("Enter a threshold greater than 0.");
            }

            var version = AccountService.NewId();
            await using var connection = await storage.OpenAsync(cancellationToken);
            if (await connection.ExecuteAsync(SetThreshold, new { Id = id, OrganizationId = organization.Id, ProjectId = project.Id, Threshold = threshold, Version = version }) == 0)
            {
                return new DashboardNotFound("Alert rule is unavailable.");
            }

            return new Done();
        }

        private static double? Threshold(string? value)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold) && double.IsFinite(threshold) && threshold is > 0 and <= 1e12
                ? threshold : null;
        }

        public async Task RunOnceAsync(CancellationToken cancellationToken)
        {
            if (!await gate.AllowsAsync(cancellationToken)) { return; }
            List<(string ProjectId, string OrganizationId)> projects;
            await using (var connection = await storage.OpenAsync(cancellationToken))
            {
                projects = [.. (await connection.QueryAsync<AlertRuleRow>(DashboardSql.AlertRules.Select)).Select(row => (row.ProjectId, row.OrganizationId)).Distinct()];
            }

            Task<IReadOnlyDictionary<string, long>>? units = null;
            foreach (var (projectId, organizationId) in projects)
            {
                try { await ObserveAsync(projectId, organizationId, () => units ??= source.ReadOperationUnitsAsync(cancellationToken), cancellationToken); }
                catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException or TerraBaseException or DBConcurrencyException ||
                    error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    logger.AlertRulesForAProjectCould(error.GetType().Name);
                }
            }
        }

        private async Task ObserveAsync(string projectId, string organizationId, Func<Task<IReadOnlyDictionary<string, long>>> units, CancellationToken cancellationToken)
        {
            if (await organizations.FindAsync(organizationId, cancellationToken) is not { } organizationRow)
            {
                return;
            }

            var organization = organizationRow.Erased ? throw new IOException("Dashboard record was erased.") : organizationRow.Organization;
            if (organization.Id != organizationId || !organization.Projects.TryGetValue(projectId, out var project))
            {
                return;
            }

            var id = "alerts/" + projectId;
            BackgroundJobRow? state;
            await using (var snapshot = await storage.OpenAsync(cancellationToken))
            {
                state = await snapshot.QuerySingleOrDefaultAsync<BackgroundJobRow>(JobById, new { Id = id });
                if (state is { Leased: true } ||
                    !await ChangesDueAsync(snapshot, projectId, organization, project, units, cancellationToken))
                {
                    return;
                }
            }

            await jobs.RunAsync(id, TimeSpan.Zero, async (claim, token) =>
            {
                if (await claim.Connection.ExecuteScalarAsync<long>(RuleCount, new { ProjectId = projectId, OrganizationId = organizationId }) == 0)
                {
                    await claim.ForgetAsync(null, token);
                    return;
                }

                await EvaluateAsync(projectId, organization, project, units, claim, token);
                await DeliverAsync(projectId, organizationId, claim, token);
            }, cancellationToken, fence: state?.Checkpoint ?? "");
        }

        private async Task<bool> ChangesDueAsync(TerraBaseDbConnection connection, string projectId, OrganizationRecord organization, ProjectRecord project,
            Func<Task<IReadOnlyDictionary<string, long>>> units, CancellationToken cancellationToken)
        {
            var rules = await RulesWithEventsAsync(connection, projectId, organization.Id);
            if (rules.Any(rule => rule.Events.Any(item => item.EmailStatus == "pending" || item.WebhookStatus == "pending"))) { return true; }
            if (rules.Count == 0) { return false; }
            var analytics = await source.ReadAnalyticsAsync(projectId, cancellationToken);
            double? percent = null;
            if (rules.Any(rule => rule.Metric == "operation_units_percent") && PlanCatalog.Limits(organization).MonthlyOperations is > 0 and var allowance &&
                (await units()).TryGetValue(organization.Id, out var used)) { percent = used * 100d / allowance; }
            return rules.Any(row => Evaluate(Rule(row), analytics, project, percent) is var (value, _) &&
                (row.Value != value || (row.State == "firing") != (value > row.Threshold)));
        }

        private async Task EvaluateAsync(string projectId, OrganizationRecord organization, ProjectRecord project, Func<Task<IReadOnlyDictionary<string, long>>> units,
            BackgroundJobs.Claim claim, CancellationToken cancellationToken)
        {
            var rules = await RulesWithEventsAsync(claim.Connection, projectId, organization.Id);
            if (rules.Count == 0)
            {
                return;
            }

            var analytics = await source.ReadAnalyticsAsync(projectId, cancellationToken);
            double? percent = null;
            if (rules.Any(rule => rule.Metric == "operation_units_percent") && PlanCatalog.Limits(organization).MonthlyOperations is > 0 and var allowance &&
                (await units()).TryGetValue(organization.Id, out var used))
            {
                percent = used * 100d / allowance;
            }

            var now = clock.GetUtcNow().UtcDateTime;
            var changed = false;
            List<AlertRuleRow> updated = [];
            List<AlertEventRow> removed = [];
            List<AlertEventRow> added = [];
            foreach (var row in rules)
            {
                if (Evaluate(Rule(row), analytics, project, percent) is not var (value, database))
                {
                    continue;
                }

                var firing = value > row.Threshold;
                if (firing == (row.State == "firing"))
                {
                    if (row.Value != value)
                    {
                        row.Value = value;
                        row.OriginalVersion = row.Version;
                        row.Version = AccountService.NewId();
                        updated.Add(row);
                        changed = true;
                    }

                    continue;
                }

                var alertId = firing ? AccountService.NewId() : row.AlertId ?? AccountService.NewId();
                row.State = firing ? "firing" : "ok";
                row.Since = now;
                row.AlertId = alertId;
                row.Value = value;
                row.OriginalVersion = row.Version;
                row.Version = AccountService.NewId();
                updated.Add(row);
                changed = true;
                if (!row.Email && row.WebhookUrl is null)
                {
                    continue;
                }

                removed.AddRange(row.Events.OrderBy(item => item.Id == claim.State.DeliveryId ? 1 : 0).ThenBy(item => item.Id, StringComparer.Ordinal).SkipLast(EventLimit - 1));
                added.Add(new AlertEventRow
                {
                    Id = AccountService.NewId(),
                    RuleId = row.Id,
                    AlertId = alertId,
                    State = firing ? "firing" : "resolved",
                    Value = value,
                    DatabaseName = database,
                    At = now,
                    EmailStatus = row.Email ? "pending" : "none",
                    WebhookStatus = row.WebhookUrl is null ? "none" : "pending",
                    WebhookAttempts = 0,
                });
            }

            if (!changed)
            {
                return;
            }

            await claim.SaveAsync(async (connection, transaction) =>
            {
                foreach (var row in updated)
                {
                    DashboardDb.Expect(await connection.ExecuteAsync(UpdateRule, row, transaction));
                }

                foreach (var item in removed)
                {
                    await connection.ExecuteAsync(DashboardSql.AlertEvents.Delete, item, transaction);
                }

                foreach (var item in added)
                {
                    await connection.ExecuteAsync(DashboardSql.AlertEvents.Insert, item, transaction);
                }
            }, cancellationToken);
        }

        private async Task DeliverAsync(string projectId, string organizationId, BackgroundJobs.Claim claim, CancellationToken cancellationToken)
        {
            var key = new { ProjectId = projectId, OrganizationId = organizationId };
            var rules = (await claim.Connection.QueryAsync<AlertRuleRow>(RulesOf, key)).ToDictionary(row => row.Id, StringComparer.Ordinal);
            var pending = (await claim.Connection.QueryAsync<AlertEventRow>(EventsOf, key)).AsList();
            if (claim.State.DeliveryId is { } deliveryId && pending.All(item => item.Id != deliveryId))
            {
                claim.State.Payload = null;
                claim.State.DeliveryId = null;
                await claim.SaveAsync(cancellationToken);
            }
            foreach (var item in pending.OrderBy(item => item.Id == claim.State.DeliveryId ? 0 : 1).ThenBy(item => item.Id, StringComparer.Ordinal))
            {
                if (!rules.TryGetValue(item.RuleId, out var rule))
                {
                    continue;
                }

                await DeliverAsync(projectId, organizationId, rule, item, claim, cancellationToken);
                var finished = item.EmailStatus != "pending" && item.WebhookStatus != "pending";
                if (finished)
                {
                    claim.State.Payload = null;
                    claim.State.DeliveryId = null;
                }

                await claim.SaveAsync((connection, transaction) => finished
                    ? connection.ExecuteAsync(DashboardSql.AlertEvents.Delete, item, transaction)
                    : connection.ExecuteAsync(DashboardSql.AlertEvents.Update, item, transaction), cancellationToken);
                if (claim.State.Payload is not null) { return; }
            }
        }

        public static (double Value, string? Database)? Evaluate(AlertRule rule, AlertAnalytics analytics, ProjectRecord project, double? operationUnitsPercent)
        {
            if (rule.Metric == "operation_units_percent")
            {
                return operationUnitsPercent is { } percent ? (Math.Round(percent, 3), null) : null;
            }

            var (from, to) = analytics.Items.Count == 0 ? (default, default) : (analytics.To.AddMinutes(-6), analytics.To.AddMinutes(-1));
            var value = 0d;
            foreach (var database in analytics.Items.Where(item => rule.DatabaseId is null ? project.Databases.ContainsKey(item.DatabaseId) : item.DatabaseId == rule.DatabaseId).GroupBy(item => item.DatabaseId))
            {
                var window = database.SelectMany(series => series.Buckets).Where(bucket => bucket.Start >= from && bucket.Start < to).ToArray();
                var requests = window.Sum(bucket => bucket.Requests);
                value = Math.Max(value, rule.Metric switch
                {
                    "p95_latency_ms" => window.Max(bucket => bucket.P95Ms) ?? 0,
                    "error_rate_percent" => requests > 0 ? window.Sum(bucket => bucket.Errors) * 100d / requests : 0,
                    _ => database.Sum(series => series.Buckets.LastOrDefault(bucket => bucket.StorageBytes is not null)?.StorageBytes ?? 0) / 1e9
                });
            }
            return (Math.Round(value, 3), rule.DatabaseId is null ? null : project.Databases.GetValueOrDefault(rule.DatabaseId)?.Name);
        }

        private async Task DeliverAsync(string projectId, string organizationId, AlertRuleRow rule, AlertEventRow item, BackgroundJobs.Claim claim, CancellationToken cancellationToken)
        {
            if (item.EmailStatus == "pending")
            {
                var status = await source.SendEmailAsync(new(organizationId, projectId, item.AlertId, item.State, rule.Name, rule.Metric, item.DatabaseName, rule.Threshold, item.Value), cancellationToken);
                item.EmailStatus = status == "retryable" ? "pending" : status;
            }

            if (item.WebhookStatus != "pending" || rule.WebhookUrl is null || rule.WebhookNonce is null || rule.WebhookCiphertext is null || rule.WebhookTag is null)
            {
                return;
            }

            if (claim.State.Payload is null)
            {
                claim.State.DeliveryId = item.Id;
                claim.State.Payload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    type = "alert." + item.State,
                    alertId = item.AlertId,
                    projectId,
                    rule = new { id = rule.Id, name = rule.Name, metric = rule.Metric, threshold = rule.Threshold },
                    database = rule.DatabaseId is null ? null : new { id = rule.DatabaseId, name = item.DatabaseName },
                    value = item.Value,
                    at = Utc(item.At)
                });
                await claim.SaveAsync(cancellationToken);
            }
            if (claim.State.DeliveryId != item.Id) { throw new IOException("Alert claim contains another delivery."); }
            var body = claim.State.Payload;
            var key = WebEncoders.Base64UrlDecode(new SealedSecret(rule.WebhookNonce, rule.WebhookCiphertext, rule.WebhookTag)
                .Open((await identity.ReadAsync(cancellationToken)).SessionEncryptionKey, Purpose(projectId, rule.Id)));
            var timestamp = clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var signature = Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(timestamp + ".").Concat(body).ToArray()));
            int code;
            try
            {
                code = await delivery.PostAsync(new Uri(rule.WebhookUrl), body,
                    [new("Idempotency-Key", item.Id), new("X-TerraScale-Delivery", item.Id), new("X-TerraScale-Timestamp", timestamp), new("X-TerraScale-Signature", "v1=" + signature)], cancellationToken);
            }
            catch (Exception error) when (error is TerraBaseException or HttpRequestException or ArgumentException or IOException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested) { code = 0; }
            finally { CryptographicOperations.ZeroMemory(key); }
            item.WebhookAttempts++;
            item.WebhookStatus = code is >= 200 and <= 299 ? "delivered" : item.WebhookAttempts >= WebhookAttempts ? "failed" : "pending";
        }

        private static async Task<List<AlertRuleRow>> RulesWithEventsAsync(TerraBaseDbConnection connection, string projectId, string organizationId)
        {
            var key = new { ProjectId = projectId, OrganizationId = organizationId };
            var rules = (await connection.QueryAsync<AlertRuleRow>(RulesOf, key)).AsList();
            var events = (await connection.QueryAsync<AlertEventRow>(EventsOf, key)).ToLookup(item => item.RuleId, StringComparer.Ordinal);
            foreach (var rule in rules)
            {
                rule.Events.AddRange(events[rule.Id]);
            }

            return rules;
        }

        private static (OrganizationRecord Organization, ProjectRecord Project)? Owner(AccountContext context)
        {
            return context.Organization is not { } organization || context.Project is not { } project || context.Role != "owner" ? null : (organization, project);
        }

        private static string Purpose(string projectId, string ruleId)
        {
            return "alert-webhook/" + projectId + "/" + ruleId;
        }

        private static AlertRule Rule(AlertRuleRow row)
        {
            return new(row.Id, row.Name, row.Metric, row.Threshold, row.DatabaseId, row.Email, row.WebhookUrl, Utc(row.CreatedAt), row.State,
                row.Since is { } since ? Utc(since) : null, row.AlertId, row.Value);
        }

        private static DateTimeOffset Utc(DateTime value)
        {
            return new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
        }
    }
}
