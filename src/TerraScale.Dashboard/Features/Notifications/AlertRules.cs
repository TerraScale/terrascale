using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Notifications;

// One transition's pending work. The delivery id stays the same across webhook retries.
public sealed record AlertEvent(string AlertId, string DeliveryId, string State, double Value, string? DatabaseName, DateTimeOffset At,
    string EmailStatus, string WebhookStatus, int WebhookAttempts = 0)
{
    public bool Pending => EmailStatus == "pending" || WebhookStatus == "pending";
}
public sealed record AlertRule(string Id, string Name, string Metric, double Threshold, string? DatabaseId, bool Email, string? WebhookUrl,
    SealedSecret? WebhookSecret, DateTimeOffset CreatedAt, string State = "ok", DateTimeOffset? Since = null, string? AlertId = null, double? Value = null)
{
    public List<AlertEvent> Events { get; init; } = [];
}
public sealed record AlertRuleSet(string ProjectId, string OrganizationId, List<AlertRule> Rules);
public sealed record AlertProjectIndex(Dictionary<string, string> Projects);

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

public sealed class AlertRules(IDashboardState state, IAlertSource source, ICustomerDelivery delivery, IDashboardIdentity identity, TimeProvider clock, ILogger<AlertRules> logger)
{
    public const string Collection = "dashboard_alert_rules";
    // A single document lists every project that has (or had) rules, so the worker needs no collection scan.
    public const string IndexCollection = "dashboard_alert_projects";
    public static readonly string[] Metrics = ["p95_latency_ms", "error_rate_percent", "storage_gb", "operation_units_percent"];

    public async Task<IReadOnlyList<AlertRule>> ListAsync(string projectId, CancellationToken cancellationToken) =>
        await state.GetAsync(Collection, projectId, cancellationToken) is { } document ? Read(document, projectId).Rules : [];

    public async Task<(AlertRule Rule, string? Secret)> CreateAsync(AccountContext context, IFormCollection form, CancellationToken cancellationToken)
    {
        var (organization, project) = Owner(context);
        var name = form["name"].ToString().Trim();
        if (name.Length is 0 or > 64 || name.Any(char.IsControl)) throw new AccountAccessException("Name the rule in up to 64 characters.", 422);
        var metric = form["metric"].ToString();
        if (!Metrics.Contains(metric)) throw new AccountAccessException("Choose a metric.", 422);
        var threshold = Threshold(form["threshold"]);
        var databaseId = form["database"].ToString() is { Length: > 0 } selected ? selected : null;
        if (databaseId is not null && !project.Databases.ContainsKey(databaseId)) throw new AccountAccessException("Choose a database of this project.", 422);
        var url = form["webhook"].ToString().Trim() is { Length: > 0 } text ? CustomerUrls.Validate(text).OriginalString : null;
        var id = AccountService.NewId();
        var secret = url is null ? null : WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var key = (await identity.ReadAsync(cancellationToken)).SessionEncryptionKey;
        var rule = new AlertRule(id, name, metric, threshold, databaseId, form["email"] == "on", url,
            secret is null ? null : SealedSecret.Seal(secret, key, Purpose(project.Id, id)), clock.GetUtcNow());
        await state.UpdateAsync(Collection, project.Id, () => new AlertRuleSet(project.Id, organization.Id, []), document => Read(document, project.Id), set =>
        {
            if (set.Rules.Count >= 20) throw new AccountAccessException("A project can have up to 20 alert rules.", 409);
            set.Rules.Add(rule);
            return set;
        }, cancellationToken);
        await state.UpdateAsync(IndexCollection, "all", () => new AlertProjectIndex([]), AccountService.Read<AlertProjectIndex>,
            index => index.Projects.TryAdd(project.Id, organization.Id) ? index : null, cancellationToken);
        return (rule, secret);
    }

    public async Task DeleteAsync(AccountContext context, string id, CancellationToken cancellationToken)
    {
        var (organization, project) = Owner(context);
        await state.UpdateAsync(Collection, project.Id, () => new AlertRuleSet(project.Id, organization.Id, []), document => Read(document, project.Id),
            set => set.Rules.RemoveAll(rule => rule.Id == id) > 0 ? set : throw new AccountAccessException("Alert rule is unavailable.", 404), cancellationToken);
    }

    // A changed threshold takes effect on the next run: raising it above the current value resolves a firing rule.
    public async Task UpdateThresholdAsync(AccountContext context, string id, string? value, CancellationToken cancellationToken)
    {
        var (organization, project) = Owner(context);
        var threshold = Threshold(value);
        await state.UpdateAsync(Collection, project.Id, () => new AlertRuleSet(project.Id, organization.Id, []), document => Read(document, project.Id), set =>
        {
            var index = set.Rules.FindIndex(rule => rule.Id == id);
            if (index < 0) throw new AccountAccessException("Alert rule is unavailable.", 404);
            set.Rules[index] = set.Rules[index] with { Threshold = threshold };
            return set;
        }, cancellationToken);
    }

    private static double Threshold(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold) && double.IsFinite(threshold) && threshold is > 0 and <= 1e12
            ? threshold : throw new AccountAccessException("Enter a threshold greater than 0.", 422);

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        if (await state.GetAsync(IndexCollection, "all", cancellationToken) is not { } index) return;
        Task<IReadOnlyDictionary<string, long>>? units = null;
        foreach (var (projectId, organizationId) in AccountService.Read<AlertProjectIndex>(index).Projects)
        {
            try { await ObserveAsync(projectId, organizationId, () => units ??= source.ReadOperationUnitsAsync(cancellationToken), cancellationToken); }
            catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException or TerraBaseException ||
                error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Alert rules for a project could not be evaluated ({ErrorType}); other projects continue.", error.GetType().Name);
            }
        }
    }

    private async Task ObserveAsync(string projectId, string organizationId, Func<Task<IReadOnlyDictionary<string, long>>> units, CancellationToken cancellationToken)
    {
        if (await state.GetAsync(Collection, projectId, cancellationToken) is not { } document || Read(document, projectId).Rules.Count == 0) return;
        if (await state.GetAsync(AccountService.OrganizationsCollection, organizationId, cancellationToken) is not { } organizationDocument) return;
        var organization = AccountService.Read<OrganizationRecord>(organizationDocument);
        if (organization.Id != organizationId || !organization.Projects.TryGetValue(projectId, out var project)) return;
        var rules = Read(document, projectId).Rules;
        var analytics = await source.ReadAnalyticsAsync(projectId, cancellationToken);
        double? percent = null;
        if (rules.Any(rule => rule.Metric == "operation_units_percent") && PlanCatalog.Limits(organization).MonthlyOperations is > 0 and var allowance &&
            (await units()).TryGetValue(organizationId, out var used)) percent = used * 100d / allowance;
        var now = clock.GetUtcNow();
        var saved = await state.UpdateAsync(Collection, projectId, () => throw new IOException("Alert rules disappeared."), current => Read(current, projectId), set =>
        {
            var changed = false;
            for (var index = 0; index < set.Rules.Count; index++)
            {
                var rule = set.Rules[index];
                if (Evaluate(rule, analytics, project, percent) is not var (value, database)) continue;
                var firing = value > rule.Threshold;
                if (firing == (rule.State == "firing"))
                {
                    changed |= rule.Value != value;
                    set.Rules[index] = rule with { Value = value };
                    continue;
                }
                changed = true;
                var alertId = firing ? AccountService.NewId() : rule.AlertId ?? AccountService.NewId();
                var events = rule.Events.TakeLast(9).Append(new(alertId, AccountService.NewId(), firing ? "firing" : "resolved", value, database, now,
                    rule.Email ? "pending" : "none", rule.WebhookUrl is null ? "none" : "pending")).ToList();
                set.Rules[index] = rule with { State = firing ? "firing" : "ok", Since = now, AlertId = alertId, Value = value, Events = events };
            }
            return changed ? set : null;
        }, cancellationToken);
        var results = new Dictionary<string, AlertEvent>(StringComparer.Ordinal);
        foreach (var rule in saved.Rules)
            foreach (var pending in rule.Events.Where(item => item.Pending))
                results[pending.DeliveryId] = await DeliverAsync(saved, rule, pending, cancellationToken);
        if (results.Count == 0 && !saved.Rules.Any(rule => rule.Events.Any(item => !item.Pending))) return;
        await state.UpdateAsync(Collection, projectId, () => throw new IOException("Alert rules disappeared."), current => Read(current, projectId), set =>
        {
            for (var index = 0; index < set.Rules.Count; index++)
                set.Rules[index] = set.Rules[index] with { Events = set.Rules[index].Events.Select(item => results.GetValueOrDefault(item.DeliveryId) ?? item).Where(item => item.Pending).ToList() };
            return set;
        }, cancellationToken);
    }

    // The last five complete one-minute buckets; the newest bucket of the response is still filling.
    public static (double Value, string? Database)? Evaluate(AlertRule rule, AlertAnalytics analytics, ProjectRecord project, double? operationUnitsPercent)
    {
        if (rule.Metric == "operation_units_percent") return operationUnitsPercent is { } percent ? (Math.Round(percent, 3), null) : null;
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

    private async Task<AlertEvent> DeliverAsync(AlertRuleSet set, AlertRule rule, AlertEvent item, CancellationToken cancellationToken)
    {
        if (item.EmailStatus == "pending")
        {
            // ts-auth owns the send claim per alert and state, so repeating this call after a crash sends at most once.
            var status = await source.SendEmailAsync(new(set.OrganizationId, set.ProjectId, item.AlertId, item.State, rule.Name, rule.Metric, item.DatabaseName, rule.Threshold, item.Value), cancellationToken);
            item = item with { EmailStatus = status == "retryable" ? "pending" : status };
        }
        if (item.WebhookStatus != "pending" || rule.WebhookUrl is null || rule.WebhookSecret is null) return item;
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "alert." + item.State,
            alertId = item.AlertId,
            projectId = set.ProjectId,
            rule = new { id = rule.Id, name = rule.Name, metric = rule.Metric, threshold = rule.Threshold },
            database = rule.DatabaseId is null ? null : new { id = rule.DatabaseId, name = item.DatabaseName },
            value = item.Value,
            at = item.At
        });
        var key = WebEncoders.Base64UrlDecode(rule.WebhookSecret.Open((await identity.ReadAsync(cancellationToken)).SessionEncryptionKey, Purpose(set.ProjectId, rule.Id)));
        var timestamp = clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(timestamp + ".").Concat(body).ToArray()));
        int code;
        try
        {
            code = await delivery.PostAsync(new Uri(rule.WebhookUrl), body,
                [new("X-TerraScale-Delivery", item.DeliveryId), new("X-TerraScale-Timestamp", timestamp), new("X-TerraScale-Signature", "v1=" + signature)], cancellationToken);
        }
        catch (Exception error) when (error is HttpRequestException or ArgumentException or IOException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested) { code = 0; }
        finally { CryptographicOperations.ZeroMemory(key); }
        if (code is >= 200 and <= 299) return item with { WebhookStatus = "delivered", WebhookAttempts = item.WebhookAttempts + 1 };
        return item with { WebhookStatus = item.WebhookAttempts + 1 >= 3 ? "failed" : "pending", WebhookAttempts = item.WebhookAttempts + 1 };
    }

    private static (OrganizationRecord Organization, ProjectRecord Project) Owner(AccountContext context)
    {
        if (context.Organization is not { } organization || context.Project is not { } project) throw new AccountAccessException("Select a project.", 404);
        if (context.Role != "owner") throw new AccountAccessException("Only organization owners manage alert rules.");
        return (organization, project);
    }

    private static string Purpose(string projectId, string ruleId) => "alert-webhook/" + projectId + "/" + ruleId;

    public static AlertRuleSet Read(TerraBaseDocument document, string projectId)
    {
        var saved = AccountService.Read<AlertRuleSet>(document);
        if (document.Id != projectId || saved.ProjectId != projectId || saved.Rules is null || saved.Rules.Count > 20 ||
            saved.Rules.Any(rule => !TerraScaleIds.IsValid(rule.Id) || !Metrics.Contains(rule.Metric) || rule.Events is null))
            throw new IOException("Alert rule identity is invalid.");
        return saved;
    }
}
