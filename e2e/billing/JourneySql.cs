using System.Globalization;
using Dapper;
using TerraScale.Dashboard.Platform.Persistence;

[module: DapperAot]
[module: TypeHandler(typeof(DateTimeOffset), typeof(UtcDateTimeOffsetHandler))]

internal static class JourneySql
{
    public const string Jobs = "SELECT id, owner, checkpoint, available_at, lease_until, failures, attempted_at, succeeded_at, cursor, next_cursor, delivery_id, payload FROM background_jobs";

    public const string Drains = "SELECT id, created_at, header_ciphertext, header_name, header_nonce, header_tag, last_error_at, last_error_status, last_gap_at, last_success_at, organization_id, project_id, revision, url FROM log_drains";

    public const string InsertDrain = "INSERT INTO log_drains (id, created_at, header_ciphertext, header_name, header_nonce, header_tag, last_error_at, last_error_status, last_gap_at, last_success_at, organization_id, project_id, revision, url) VALUES (@Id, @CreatedAt, @HeaderCiphertext, @HeaderName, @HeaderNonce, @HeaderTag, @LastErrorAt, @LastErrorStatus, @LastGapAt, @LastSuccessAt, @OrganizationId, @ProjectId, @Revision, @Url)";

    public const string Cursors = "SELECT id, cursor, database_id, drain_id FROM log_drain_cursors";

    public const string InsertCursor = "INSERT INTO log_drain_cursors (id, cursor, database_id, drain_id) VALUES (@Id, @Cursor, @DatabaseId, @DrainId)";

    public const string InsertAlertRule = "INSERT INTO alert_rules (id, alert_id, created_at, database_id, email, metric, name, organization_id, project_id, since, slot, state, threshold, value, version, webhook_ciphertext, webhook_nonce, webhook_tag, webhook_url) VALUES (@Id, @AlertId, @CreatedAt, @DatabaseId, @Email, @Metric, @Name, @OrganizationId, @ProjectId, @Since, @Slot, @State, @Threshold, @Value, @Version, @WebhookCiphertext, @WebhookNonce, @WebhookTag, @WebhookUrl)";

    public const string InsertAlertEvent = "INSERT INTO alert_events (id, alert_id, at, database_name, email_status, rule_id, state, value, webhook_attempts, webhook_status) VALUES (@Id, @AlertId, @At, @DatabaseName, @EmailStatus, @RuleId, @State, @Value, @WebhookAttempts, @WebhookStatus)";

    public const string Billing = "SELECT organization_id, checkout_id, checkout_project_id, customer_id, effective_interval, effective_plan, erasure_id, expiry_last_logged_at, expiry_unavailable_since, internal_reservation, interval, kind, overage_starts_at, plan, renewal_at, request_id, return_path, revision, started_at, state, status, subscription_id, tax_location_status, trial_used, version FROM billing";

    public const string InsertBilling = "INSERT INTO billing (organization_id, checkout_id, checkout_project_id, customer_id, effective_interval, effective_plan, erasure_id, expiry_last_logged_at, expiry_unavailable_since, internal_reservation, interval, kind, overage_starts_at, plan, renewal_at, request_id, return_path, revision, started_at, state, status, subscription_id, tax_location_status, trial_used, version) VALUES (@OrganizationId, @CheckoutId, @CheckoutProjectId, @CustomerId, @EffectiveInterval, @EffectivePlan, @ErasureId, @ExpiryLastLoggedAt, @ExpiryUnavailableSince, @InternalReservation, @Interval, @Kind, @OverageStartsAt, @Plan, @RenewalAt, @RequestId, @ReturnPath, @Revision, @StartedAt, @State, @Status, @SubscriptionId, @TaxLocationStatus, @TrialUsed, @Version)";

    public const string InsertStripeCustomer = "INSERT INTO stripe_customers (id, created_at, customer_id, organization_id) VALUES (@Id, @CreatedAt, @CustomerId, @OrganizationId)";

    public const string Charges = "SELECT id, accepted_at, allowance, amount_cents, claim_owner, claim_until, currency, customer_id, description, frozen_at, meter, month, organization_id, plan_name, pricing_version, provider_amount_cents, provider_id, quantity, skip_reason, state, subscription_id, unit_rate, usage FROM billing_overage_charges";

    public const string InsertReceipt = "INSERT INTO stripe_event_receipts (id, completed_at, event_created, event_type, invoice_id, organization_id, processing_until, received_at, subscription_id, version) VALUES (@Id, @CompletedAt, @EventCreated, @EventType, @InvoiceId, @OrganizationId, @ProcessingUntil, @ReceivedAt, @SubscriptionId, @Version)";

    private const string DatabaseNow = "SELECT strftime('%Y-%m-%d %H:%M:%f', 'now')";

    public static async Task<DateTime> NowAsync(DashboardDb storage, CancellationToken cancellationToken) =>
        DateTime.Parse(await storage.ReadAsync(connection => connection.QuerySingleAsync<string>(DatabaseNow), cancellationToken), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public static Task<BackgroundJobRow?> JobAsync(DashboardDb storage, string id, CancellationToken cancellationToken) =>
        storage.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<BackgroundJobRow>(Jobs + " WHERE id = @Id", new { Id = id }), cancellationToken);

    public static async Task<(BackgroundJobRow Row, DateTime Now)> LiveJobAsync(DashboardDb storage, string id, CancellationToken cancellationToken) =>
        (await JobAsync(storage, id, cancellationToken) ?? throw new InvalidOperationException("The background job " + id + " is missing."), await NowAsync(storage, cancellationToken));

    public static async Task<bool> JobExistsAsync(DashboardDb storage, string id, CancellationToken cancellationToken) =>
        await storage.ReadAsync(connection => connection.ExecuteScalarAsync<long>("SELECT count(*) FROM background_jobs WHERE id = @Id", new { Id = id }), cancellationToken) > 0;

    public static async Task DeleteJobAsync(DashboardDb storage, string id, CancellationToken cancellationToken) =>
        await storage.ReadAsync(connection => connection.ExecuteAsync("DELETE FROM background_jobs WHERE id = @Id", new { Id = id }), cancellationToken);

    public static async Task InsertSchemaVersionAsync(DashboardDb storage, string name, DateTime appliedAt, CancellationToken cancellationToken) =>
        await storage.ReadAsync(connection => connection.ExecuteAsync("INSERT INTO schema_versions (script_name, applied_at) VALUES (@Name, @AppliedAt)", new { Name = name, AppliedAt = appliedAt }), cancellationToken);

    public static async Task DeleteSchemaVersionAsync(DashboardDb storage, string name, CancellationToken cancellationToken) =>
        await storage.ReadAsync(connection => connection.ExecuteAsync("DELETE FROM schema_versions WHERE script_name = @Name", new { Name = name }), cancellationToken);
}
