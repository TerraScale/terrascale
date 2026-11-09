using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.TerraBase.Contracts;
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

    public interface IAlertSource
    {
        Task<AlertAnalytics> ReadAnalyticsAsync(string projectId, CancellationToken cancellationToken);
        Task<IReadOnlyDictionary<string, long>> ReadOperationUnitsAsync(CancellationToken cancellationToken);
        Task<string> SendEmailAsync(AlertEmail email, CancellationToken cancellationToken);
    }

    public sealed class AlertRules(IDbContextFactory<DashboardDbContext> storage, IDashboardState state, IAlertSource source, ICustomerDelivery delivery, IDashboardIdentity identity,
        TimeProvider clock, ILogger<AlertRules> logger)
    {
        public const int RuleLimit = 20;
        public const int WebhookAttempts = 3;
        private const int EventLimit = 10;
        public static readonly string[] Metrics = ["p95_latency_ms", "error_rate_percent", "storage_gb", "operation_units_percent"];

        public async Task<IReadOnlyList<AlertRule>> ListAsync(string projectId, CancellationToken cancellationToken)
        {
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            var rows = await db.AlertRules.AsNoTracking().Where(row => row.ProjectId == projectId).OrderBy(row => row.Id).ToListAsync(cancellationToken);
            return [.. rows.Select(Rule)];
        }

        public async Task<(AlertRule Rule, string? Secret)> CreateAsync(AccountContext context, IFormCollection form, CancellationToken cancellationToken)
        {
            var (organization, project) = Owner(context);
            var name = form["name"].ToString().Trim();
            if (name.Length is 0 or > 64 || name.Any(char.IsControl))
            {
                throw new AccountAccessException("Name the rule in up to 64 characters.", 422);
            }

            var metric = form["metric"].ToString();
            if (!Metrics.Contains(metric))
            {
                throw new AccountAccessException("Choose a metric.", 422);
            }

            var threshold = Threshold(form["threshold"]);
            var databaseId = form["database"].ToString() is { Length: > 0 } selected ? selected : null;
            if (databaseId is not null && !project.Databases.ContainsKey(databaseId))
            {
                throw new AccountAccessException("Choose a database of this project.", 422);
            }

            var url = form["webhook"].ToString().Trim() is { Length: > 0 } text ? CustomerUrls.Validate(text).OriginalString : null;
            var id = AccountService.NewId();
            var secret = url is null ? null : WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var key = (await identity.ReadAsync(cancellationToken)).SessionEncryptionKey;
            var sealedSecret = secret is null ? null : SealedSecret.Seal(secret, key, Purpose(project.Id, id));
            var createdAt = clock.GetUtcNow().UtcDateTime;
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            for (var attempt = 0; attempt < RuleLimit; attempt++)
            {
                var taken = await db.AlertRules.Where(row => row.ProjectId == project.Id).Select(row => row.Slot).ToListAsync(cancellationToken);
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
                db.AlertRules.Add(row);
                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                    return (Rule(row), secret);
                }
                catch (DbUpdateException error) when (error.InnerException is TerraBaseSqlException { Code: TerraBaseSqlErrorCodes.UniqueViolation })
                {
                    db.Entry(row).State = EntityState.Detached;
                }
            }

            throw new AccountAccessException($"A project can have up to {RuleLimit} alert rules.", 409);
        }

        public async Task DeleteAsync(AccountContext context, string id, CancellationToken cancellationToken)
        {
            var (organization, project) = Owner(context);
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            if (await db.AlertRules.Where(row => row.Id == id && row.OrganizationId == organization.Id && row.ProjectId == project.Id).ExecuteDeleteAsync(cancellationToken) == 0)
            {
                throw new AccountAccessException("Alert rule is unavailable.", 404);
            }
        }

        public async Task UpdateThresholdAsync(AccountContext context, string id, string? value, CancellationToken cancellationToken)
        {
            var (organization, project) = Owner(context);
            var threshold = Threshold(value);
            var version = AccountService.NewId();
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            if (await db.AlertRules.Where(row => row.Id == id && row.OrganizationId == organization.Id && row.ProjectId == project.Id)
                .ExecuteUpdateAsync(row => row.SetProperty(rule => rule.Threshold, threshold).SetProperty(rule => rule.Version, version), cancellationToken) == 0)
            {
                throw new AccountAccessException("Alert rule is unavailable.", 404);
            }
        }

        private static double Threshold(string? value)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold) && double.IsFinite(threshold) && threshold is > 0 and <= 1e12
                ? threshold : throw new AccountAccessException("Enter a threshold greater than 0.", 422);
        }

        public async Task RunOnceAsync(CancellationToken cancellationToken)
        {
            List<(string ProjectId, string OrganizationId)> projects;
            await using (var db = await storage.CreateDbContextAsync(cancellationToken))
            {
                projects = [.. (await db.AlertRules.AsNoTracking().Select(row => new { row.ProjectId, row.OrganizationId }).Distinct().ToListAsync(cancellationToken))
                    .Select(owner => (owner.ProjectId, owner.OrganizationId))];
            }

            Task<IReadOnlyDictionary<string, long>>? units = null;
            foreach (var (projectId, organizationId) in projects)
            {
                try { await ObserveAsync(projectId, organizationId, () => units ??= source.ReadOperationUnitsAsync(cancellationToken), cancellationToken); }
                catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException or TerraBaseException or DbUpdateException ||
                    error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    logger.AlertRulesForAProjectCould(error.GetType().Name);
                }
            }
        }

        private async Task ObserveAsync(string projectId, string organizationId, Func<Task<IReadOnlyDictionary<string, long>>> units, CancellationToken cancellationToken)
        {
            if (await state.GetAsync(AccountService.OrganizationsTable, organizationId, cancellationToken) is not { } organizationRow)
            {
                return;
            }

            var organization = AccountService.Read<OrganizationRecord>(organizationRow);
            if (organization.Id != organizationId || !organization.Projects.TryGetValue(projectId, out var project))
            {
                return;
            }

            await EvaluateAsync(projectId, organization, project, units, cancellationToken);
            await DeliverAsync(projectId, organizationId, cancellationToken);
        }

        private async Task EvaluateAsync(string projectId, OrganizationRecord organization, ProjectRecord project, Func<Task<IReadOnlyDictionary<string, long>>> units,
            CancellationToken cancellationToken)
        {
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            var rules = await db.AlertRules.Include(row => row.Events).Where(row => row.ProjectId == projectId && row.OrganizationId == organization.Id)
                .OrderBy(row => row.Id).ToListAsync(cancellationToken);
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
                        row.Version = AccountService.NewId();
                        changed = true;
                    }

                    continue;
                }

                var alertId = firing ? AccountService.NewId() : row.AlertId ?? AccountService.NewId();
                row.State = firing ? "firing" : "ok";
                row.Since = now;
                row.AlertId = alertId;
                row.Value = value;
                row.Version = AccountService.NewId();
                changed = true;
                if (!row.Email && row.WebhookUrl is null)
                {
                    continue;
                }

                db.AlertEvents.RemoveRange(row.Events.OrderBy(item => item.Id, StringComparer.Ordinal).SkipLast(EventLimit - 1).ToArray());
                db.AlertEvents.Add(new AlertEventRow
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

            try { await db.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateConcurrencyException) { }
        }

        private async Task DeliverAsync(string projectId, string organizationId, CancellationToken cancellationToken)
        {
            await using var db = await storage.CreateDbContextAsync(cancellationToken);
            var rules = await db.AlertRules.AsNoTracking().Where(row => row.ProjectId == projectId && row.OrganizationId == organizationId).ToDictionaryAsync(row => row.Id, cancellationToken);
            var pending = await db.AlertEvents.Where(item => db.AlertRules.Any(rule => rule.Id == item.RuleId && rule.ProjectId == projectId && rule.OrganizationId == organizationId))
                .OrderBy(item => item.Id).ToListAsync(cancellationToken);
            if (pending.Count == 0)
            {
                return;
            }

            foreach (var item in pending)
            {
                if (!rules.TryGetValue(item.RuleId, out var rule))
                {
                    continue;
                }

                await DeliverAsync(projectId, organizationId, rule, item, cancellationToken);
                if (item.EmailStatus != "pending" && item.WebhookStatus != "pending")
                {
                    db.AlertEvents.Remove(item);
                }
            }

            try { await db.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateConcurrencyException) { }
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

        private async Task DeliverAsync(string projectId, string organizationId, AlertRuleRow rule, AlertEventRow item, CancellationToken cancellationToken)
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

            var body = JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "alert." + item.State,
                alertId = item.AlertId,
                projectId,
                rule = new { id = rule.Id, name = rule.Name, metric = rule.Metric, threshold = rule.Threshold },
                database = rule.DatabaseId is null ? null : new { id = rule.DatabaseId, name = item.DatabaseName },
                value = item.Value,
                at = Utc(item.At)
            });
            var key = WebEncoders.Base64UrlDecode(new SealedSecret(rule.WebhookNonce, rule.WebhookCiphertext, rule.WebhookTag)
                .Open((await identity.ReadAsync(cancellationToken)).SessionEncryptionKey, Purpose(projectId, rule.Id)));
            var timestamp = clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var signature = Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(timestamp + ".").Concat(body).ToArray()));
            int code;
            try
            {
                code = await delivery.PostAsync(new Uri(rule.WebhookUrl), body,
                    [new("X-TerraScale-Delivery", item.Id), new("X-TerraScale-Timestamp", timestamp), new("X-TerraScale-Signature", "v1=" + signature)], cancellationToken);
            }
            catch (Exception error) when (error is TerraBaseException or HttpRequestException or ArgumentException or IOException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested) { code = 0; }
            finally { CryptographicOperations.ZeroMemory(key); }
            item.WebhookAttempts++;
            item.WebhookStatus = code is >= 200 and <= 299 ? "delivered" : item.WebhookAttempts >= WebhookAttempts ? "failed" : "pending";
        }

        private static (OrganizationRecord Organization, ProjectRecord Project) Owner(AccountContext context)
        {
            if (context.Organization is not { } organization || context.Project is not { } project)
            {
                throw new AccountAccessException("Select a project.", 404);
            }

            if (context.Role != "owner")
            {
                throw new AccountAccessException("Only organization owners manage alert rules.");
            }

            return (organization, project);
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
