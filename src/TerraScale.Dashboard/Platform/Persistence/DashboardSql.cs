namespace TerraScale.Dashboard.Platform.Persistence;

internal static class DashboardSql
{
    internal static class Accounts
    {
        public const string Select = "SELECT id, active_organization_id, active_project_id, created_at, email, erasure_id, locale, name, onboarding_completed, onboarding_step, organization_ids, personal_claim_until, personal_organization_id, phone, subject_hash, user_id, version FROM accounts";

        public const string Insert = "INSERT INTO accounts (id, active_organization_id, active_project_id, created_at, email, erasure_id, locale, name, onboarding_completed, onboarding_step, organization_ids, personal_claim_until, personal_organization_id, phone, subject_hash, user_id, version) VALUES (@Id, @ActiveOrganizationId, @ActiveProjectId, @CreatedAt, @Email, @ErasureId, @Locale, @Name, @OnboardingCompleted, @OnboardingStep, @OrganizationIds, @PersonalClaimUntil, @PersonalOrganizationId, @Phone, @SubjectHash, @UserId, @Version)";

        public const string Update = "UPDATE accounts SET active_organization_id = @ActiveOrganizationId, active_project_id = @ActiveProjectId, created_at = @CreatedAt, email = @Email, erasure_id = @ErasureId, locale = @Locale, name = @Name, onboarding_completed = @OnboardingCompleted, onboarding_step = @OnboardingStep, organization_ids = @OrganizationIds, personal_claim_until = @PersonalClaimUntil, personal_organization_id = @PersonalOrganizationId, phone = @Phone, subject_hash = @SubjectHash, user_id = @UserId, version = @Version WHERE id = @Id AND version = @OriginalVersion";

        public const string Delete = "DELETE FROM accounts WHERE id = @Id AND version = @OriginalVersion";
    }

    internal static class AuditEvents
    {
        public const string Select = "SELECT id, action, actor, ip, occurred_at, organization_id, risk, source, target, target_type FROM audit_events";

        public const string Insert = "INSERT INTO audit_events (id, action, actor, ip, occurred_at, organization_id, risk, source, target, target_type) VALUES (@Id, @Action, @Actor, @Ip, @OccurredAt, @OrganizationId, @Risk, @Source, @Target, @TargetType)";

        public const string Update = "UPDATE audit_events SET action = @Action, actor = @Actor, ip = @Ip, occurred_at = @OccurredAt, organization_id = @OrganizationId, risk = @Risk, source = @Source, target = @Target, target_type = @TargetType WHERE id = @Id";

        public const string Delete = "DELETE FROM audit_events WHERE id = @Id";
    }

    internal static class InvitationReservations
    {
        public const string Select = "SELECT id, email, invitation_id, known, organization_id, project_id, reserved_at FROM invitation_reservations";

        public const string Insert = "INSERT INTO invitation_reservations (id, email, invitation_id, known, organization_id, project_id, reserved_at) VALUES (@Id, @Email, @InvitationId, @Known, @OrganizationId, @ProjectId, @ReservedAt)";

        public const string Update = "UPDATE invitation_reservations SET email = @Email, invitation_id = @InvitationId, known = @Known, organization_id = @OrganizationId, project_id = @ProjectId, reserved_at = @ReservedAt WHERE id = @Id";

        public const string Delete = "DELETE FROM invitation_reservations WHERE id = @Id";
    }

    internal static class Notifications
    {
        public const string Select = "SELECT account_id, id, billing_month, body, created_at, organization_id, project_id, severity, status, title, type, url FROM notifications";

        public const string Insert = "INSERT INTO notifications (account_id, id, billing_month, body, created_at, organization_id, project_id, severity, status, title, type, url) VALUES (@AccountId, @Id, @BillingMonth, @Body, @CreatedAt, @OrganizationId, @ProjectId, @Severity, @Status, @Title, @Type, @Url)";

        public const string Update = "UPDATE notifications SET billing_month = @BillingMonth, body = @Body, created_at = @CreatedAt, organization_id = @OrganizationId, project_id = @ProjectId, severity = @Severity, status = @Status, title = @Title, type = @Type, url = @Url WHERE account_id = @AccountId AND id = @Id";

        public const string Delete = "DELETE FROM notifications WHERE account_id = @AccountId AND id = @Id";
    }

    internal static class OrganizationProjects
    {
        public const string Select = "SELECT id, is_default, organization_id, read_limit, regions, replication_limit, spend_cap_cents, status, write_limit FROM organization_projects";

        public const string Insert = "INSERT INTO organization_projects (id, is_default, organization_id, read_limit, regions, replication_limit, spend_cap_cents, status, write_limit) VALUES (@Id, @IsDefault, @OrganizationId, @ReadLimit, @Regions, @ReplicationLimit, @SpendCapCents, @Status, @WriteLimit)";

        public const string Update = "UPDATE organization_projects SET is_default = @IsDefault, organization_id = @OrganizationId, read_limit = @ReadLimit, regions = @Regions, replication_limit = @ReplicationLimit, spend_cap_cents = @SpendCapCents, status = @Status, write_limit = @WriteLimit WHERE id = @Id";

        public const string Delete = "DELETE FROM organization_projects WHERE id = @Id";
    }

    internal static class Organizations
    {
        public const string Select = "SELECT id, archived, audit_retention_days, billing_status, deleted_project_ids, deletion_requested, erasure_id, internal_billing, internal_billing_alerts, kind, paid_excess, payment_grace_until, payment_method_validated, pending_project_deletions, plan_id, spend_cap_cents, status, version FROM organizations";

        public const string Insert = "INSERT INTO organizations (id, archived, audit_retention_days, billing_status, deleted_project_ids, deletion_requested, erasure_id, internal_billing, internal_billing_alerts, kind, paid_excess, payment_grace_until, payment_method_validated, pending_project_deletions, plan_id, spend_cap_cents, status, version) VALUES (@Id, @Archived, @AuditRetentionDays, @BillingStatus, @DeletedProjectIds, @DeletionRequested, @ErasureId, @InternalBilling, @InternalBillingAlerts, @Kind, @PaidExcess, @PaymentGraceUntil, @PaymentMethodValidated, @PendingProjectDeletions, @PlanId, @SpendCapCents, @Status, @Version)";

        public const string Update = "UPDATE organizations SET archived = @Archived, audit_retention_days = @AuditRetentionDays, billing_status = @BillingStatus, deleted_project_ids = @DeletedProjectIds, deletion_requested = @DeletionRequested, erasure_id = @ErasureId, internal_billing = @InternalBilling, internal_billing_alerts = @InternalBillingAlerts, kind = @Kind, paid_excess = @PaidExcess, payment_grace_until = @PaymentGraceUntil, payment_method_validated = @PaymentMethodValidated, pending_project_deletions = @PendingProjectDeletions, plan_id = @PlanId, spend_cap_cents = @SpendCapCents, status = @Status, version = @Version WHERE id = @Id AND version = @OriginalVersion";

        public const string Delete = "DELETE FROM organizations WHERE id = @Id AND version = @OriginalVersion";
    }

    internal static class ProjectDatabases
    {
        public const string Select = "SELECT id, all_regions, backup_retention_days, branched_from, conflict_of, created_at, endpoint, expires_at, name, organization_id, placement_revision, previous_name, project_id, read_limit, region, regions, replication_limit, spend_cap_cents, status, write_limit FROM project_databases";

        public const string Insert = "INSERT INTO project_databases (id, all_regions, backup_retention_days, branched_from, conflict_of, created_at, endpoint, expires_at, name, organization_id, placement_revision, previous_name, project_id, read_limit, region, regions, replication_limit, spend_cap_cents, status, write_limit) VALUES (@Id, @AllRegions, @BackupRetentionDays, @BranchedFrom, @ConflictOf, @CreatedAt, @Endpoint, @ExpiresAt, @Name, @OrganizationId, @PlacementRevision, @PreviousName, @ProjectId, @ReadLimit, @Region, @Regions, @ReplicationLimit, @SpendCapCents, @Status, @WriteLimit)";

        public const string Update = "UPDATE project_databases SET all_regions = @AllRegions, backup_retention_days = @BackupRetentionDays, branched_from = @BranchedFrom, conflict_of = @ConflictOf, created_at = @CreatedAt, endpoint = @Endpoint, expires_at = @ExpiresAt, name = @Name, organization_id = @OrganizationId, placement_revision = @PlacementRevision, previous_name = @PreviousName, project_id = @ProjectId, read_limit = @ReadLimit, region = @Region, regions = @Regions, replication_limit = @ReplicationLimit, spend_cap_cents = @SpendCapCents, status = @Status, write_limit = @WriteLimit WHERE id = @Id";

        public const string Delete = "DELETE FROM project_databases WHERE id = @Id";
    }

    internal static class AccessTokens
    {
        public const string Select = "SELECT id, created_at, credential_id, expires_at, last_four, mcp_client_id, name, revoked_at, secret_hash, user_id FROM access_tokens";

        public const string Insert = "INSERT INTO access_tokens (id, created_at, credential_id, expires_at, last_four, mcp_client_id, name, revoked_at, secret_hash, user_id) VALUES (@Id, @CreatedAt, @CredentialId, @ExpiresAt, @LastFour, @McpClientId, @Name, @RevokedAt, @SecretHash, @UserId)";

        public const string Update = "UPDATE access_tokens SET created_at = @CreatedAt, credential_id = @CredentialId, expires_at = @ExpiresAt, last_four = @LastFour, mcp_client_id = @McpClientId, name = @Name, revoked_at = @RevokedAt, secret_hash = @SecretHash, user_id = @UserId WHERE id = @Id";

        public const string Delete = "DELETE FROM access_tokens WHERE id = @Id";
    }

    internal static class AuthFlows
    {
        public const string Select = "SELECT id, ciphertext, created_at, expires_at, nonce, purpose, tag, token_hash FROM auth_flows";

        public const string Insert = "INSERT INTO auth_flows (id, ciphertext, created_at, expires_at, nonce, purpose, tag, token_hash) VALUES (@Id, unhex(@CiphertextHex), @CreatedAt, @ExpiresAt, unhex(@NonceHex), @Purpose, unhex(@TagHex), @TokenHash)";

        public const string Update = "UPDATE auth_flows SET ciphertext = unhex(@CiphertextHex), created_at = @CreatedAt, expires_at = @ExpiresAt, nonce = unhex(@NonceHex), purpose = @Purpose, tag = unhex(@TagHex), token_hash = @TokenHash WHERE id = @Id";

        public const string Delete = "DELETE FROM auth_flows WHERE id = @Id";
    }

    internal static class PendingRestores
    {
        public const string Select = "SELECT id, created_at, organization_id, project_id, source_database_id FROM pending_restores";

        public const string Insert = "INSERT INTO pending_restores (id, created_at, organization_id, project_id, source_database_id) VALUES (@Id, @CreatedAt, @OrganizationId, @ProjectId, @SourceDatabaseId)";

        public const string Update = "UPDATE pending_restores SET created_at = @CreatedAt, organization_id = @OrganizationId, project_id = @ProjectId, source_database_id = @SourceDatabaseId WHERE id = @Id";

        public const string Delete = "DELETE FROM pending_restores WHERE id = @Id";
    }

    internal static class Billing
    {
        public const string Select = "SELECT organization_id, checkout_id, checkout_project_id, customer_id, effective_interval, effective_plan, erasure_id, expiry_last_logged_at, expiry_unavailable_since, internal_reservation, interval, kind, overage_starts_at, plan, renewal_at, request_id, return_path, revision, started_at, state, status, subscription_id, tax_location_status, trial_used, version FROM billing";

        public const string Insert = "INSERT INTO billing (organization_id, checkout_id, checkout_project_id, customer_id, effective_interval, effective_plan, erasure_id, expiry_last_logged_at, expiry_unavailable_since, internal_reservation, interval, kind, overage_starts_at, plan, renewal_at, request_id, return_path, revision, started_at, state, status, subscription_id, tax_location_status, trial_used, version) VALUES (@OrganizationId, @CheckoutId, @CheckoutProjectId, @CustomerId, @EffectiveInterval, @EffectivePlan, @ErasureId, @ExpiryLastLoggedAt, @ExpiryUnavailableSince, @InternalReservation, @Interval, @Kind, @OverageStartsAt, @Plan, @RenewalAt, @RequestId, @ReturnPath, @Revision, @StartedAt, @State, @Status, @SubscriptionId, @TaxLocationStatus, @TrialUsed, @Version)";

        public const string Update = "UPDATE billing SET checkout_id = @CheckoutId, checkout_project_id = @CheckoutProjectId, customer_id = @CustomerId, effective_interval = @EffectiveInterval, effective_plan = @EffectivePlan, erasure_id = @ErasureId, expiry_last_logged_at = @ExpiryLastLoggedAt, expiry_unavailable_since = @ExpiryUnavailableSince, internal_reservation = @InternalReservation, interval = @Interval, kind = @Kind, overage_starts_at = @OverageStartsAt, plan = @Plan, renewal_at = @RenewalAt, request_id = @RequestId, return_path = @ReturnPath, revision = @Revision, started_at = @StartedAt, state = @State, status = @Status, subscription_id = @SubscriptionId, tax_location_status = @TaxLocationStatus, trial_used = @TrialUsed, version = @Version WHERE organization_id = @OrganizationId AND version = @OriginalVersion";

        public const string Delete = "DELETE FROM billing WHERE organization_id = @OrganizationId AND version = @OriginalVersion";
    }

    internal static class BillingChanges
    {
        public const string Select = "SELECT id, action, interval, organization_id, period_end, plan, request_id, revision, source_interval, source_plan_key, source_price_id, status, status_changed_at, subscription_id, target_price_id, version FROM billing_changes";

        public const string Insert = "INSERT INTO billing_changes (id, action, interval, organization_id, period_end, plan, request_id, revision, source_interval, source_plan_key, source_price_id, status, status_changed_at, subscription_id, target_price_id, version) VALUES (@Id, @Action, @Interval, @OrganizationId, @PeriodEnd, @Plan, @RequestId, @Revision, @SourceInterval, @SourcePlanKey, @SourcePriceId, @Status, @StatusChangedAt, @SubscriptionId, @TargetPriceId, @Version)";

        public const string Update = "UPDATE billing_changes SET action = @Action, interval = @Interval, organization_id = @OrganizationId, period_end = @PeriodEnd, plan = @Plan, request_id = @RequestId, revision = @Revision, source_interval = @SourceInterval, source_plan_key = @SourcePlanKey, source_price_id = @SourcePriceId, status = @Status, status_changed_at = @StatusChangedAt, subscription_id = @SubscriptionId, target_price_id = @TargetPriceId, version = @Version WHERE id = @Id AND version = @OriginalVersion";

        public const string Delete = "DELETE FROM billing_changes WHERE id = @Id AND version = @OriginalVersion";
    }

    internal static class BillingSubscriptions
    {
        public const string Select = "SELECT organization_id, subscription_id, checked_at, customer_id, ended_at, started_at, status, version FROM billing_subscriptions";

        public const string Insert = "INSERT INTO billing_subscriptions (organization_id, subscription_id, checked_at, customer_id, ended_at, started_at, status, version) VALUES (@OrganizationId, @SubscriptionId, @CheckedAt, @CustomerId, @EndedAt, @StartedAt, @Status, @Version)";

        public const string Update = "UPDATE billing_subscriptions SET checked_at = @CheckedAt, customer_id = @CustomerId, ended_at = @EndedAt, started_at = @StartedAt, status = @Status, version = @Version WHERE organization_id = @OrganizationId AND subscription_id = @SubscriptionId AND version = @OriginalVersion";

        public const string Delete = "DELETE FROM billing_subscriptions WHERE organization_id = @OrganizationId AND subscription_id = @SubscriptionId AND version = @OriginalVersion";
    }

    internal static class BillingTerms
    {
        public const string Select = "SELECT organization_id, subscription_id, effective_from, pricing_version, basis, currency, learned_at, operation_rate, operations_allowance, plan, plan_name, storage_allowance, storage_rate FROM billing_terms";

        public const string Insert = "INSERT INTO billing_terms (organization_id, subscription_id, effective_from, pricing_version, basis, currency, learned_at, operation_rate, operations_allowance, plan, plan_name, storage_allowance, storage_rate) VALUES (@OrganizationId, @SubscriptionId, @EffectiveFrom, @PricingVersion, @Basis, @Currency, @LearnedAt, @OperationRate, @OperationsAllowance, @Plan, @PlanName, @StorageAllowance, @StorageRate)";

        public const string Update = "UPDATE billing_terms SET currency = @Currency, learned_at = @LearnedAt, operation_rate = @OperationRate, operations_allowance = @OperationsAllowance, plan = @Plan, plan_name = @PlanName, storage_allowance = @StorageAllowance, storage_rate = @StorageRate WHERE organization_id = @OrganizationId AND subscription_id = @SubscriptionId AND effective_from = @EffectiveFrom AND pricing_version = @PricingVersion AND basis = @Basis";

        public const string Delete = "DELETE FROM billing_terms WHERE organization_id = @OrganizationId AND subscription_id = @SubscriptionId AND effective_from = @EffectiveFrom AND pricing_version = @PricingVersion AND basis = @Basis";
    }

    internal static class InternalBilling
    {
        public const string Select = "SELECT id, organization_id, plan_id, scope FROM internal_billing";

        public const string Insert = "INSERT INTO internal_billing (id, organization_id, plan_id, scope) VALUES (@Id, @OrganizationId, @PlanId, @Scope)";

        public const string Update = "UPDATE internal_billing SET organization_id = @OrganizationId, plan_id = @PlanId, scope = @Scope WHERE id = @Id";

        public const string Delete = "DELETE FROM internal_billing WHERE id = @Id";
    }

    internal static class BillingOverageCharges
    {
        public const string Select = "SELECT id, accepted_at, allowance, amount_cents, claim_owner, claim_until, currency, customer_id, description, frozen_at, meter, month, organization_id, plan_name, pricing_version, provider_amount_cents, provider_id, quantity, skip_reason, state, subscription_id, unit_rate, usage FROM billing_overage_charges";

        public const string Insert = "INSERT INTO billing_overage_charges (id, accepted_at, allowance, amount_cents, claim_owner, claim_until, currency, customer_id, description, frozen_at, meter, month, organization_id, plan_name, pricing_version, provider_amount_cents, provider_id, quantity, skip_reason, state, subscription_id, unit_rate, usage) VALUES (@Id, @AcceptedAt, @Allowance, @AmountCents, @ClaimOwner, @ClaimUntil, @Currency, @CustomerId, @Description, @FrozenAt, @Meter, @Month, @OrganizationId, @PlanName, @PricingVersion, @ProviderAmountCents, @ProviderId, @Quantity, @SkipReason, @State, @SubscriptionId, @UnitRate, @Usage)";

        public const string Update = "UPDATE billing_overage_charges SET accepted_at = @AcceptedAt, allowance = @Allowance, amount_cents = @AmountCents, claim_owner = @ClaimOwner, claim_until = @ClaimUntil, currency = @Currency, customer_id = @CustomerId, description = @Description, frozen_at = @FrozenAt, meter = @Meter, month = @Month, organization_id = @OrganizationId, plan_name = @PlanName, pricing_version = @PricingVersion, provider_amount_cents = @ProviderAmountCents, provider_id = @ProviderId, quantity = @Quantity, skip_reason = @SkipReason, state = @State, subscription_id = @SubscriptionId, unit_rate = @UnitRate, usage = @Usage WHERE id = @Id";

        public const string Delete = "DELETE FROM billing_overage_charges WHERE id = @Id";
    }

    internal static class PendingOrganizationCheckouts
    {
        public const string Select = "SELECT draft_id, checkout_id, created_at, customer_id, interval, name, organization_id, paid_at, plan, plan_id, refund_notice_at, refund_status, refusal_reason, renewal_at, state, subscription_id, subscription_status, user_id, version FROM pending_organization_checkouts";

        public const string Insert = "INSERT INTO pending_organization_checkouts (draft_id, checkout_id, created_at, customer_id, interval, name, organization_id, paid_at, plan, plan_id, refund_notice_at, refund_status, refusal_reason, renewal_at, state, subscription_id, subscription_status, user_id, version) VALUES (@DraftId, @CheckoutId, @CreatedAt, @CustomerId, @Interval, @Name, @OrganizationId, @PaidAt, @Plan, @PlanId, @RefundNoticeAt, @RefundStatus, @RefusalReason, @RenewalAt, @State, @SubscriptionId, @SubscriptionStatus, @UserId, @Version)";

        public const string Update = "UPDATE pending_organization_checkouts SET checkout_id = @CheckoutId, created_at = @CreatedAt, customer_id = @CustomerId, interval = @Interval, name = @Name, organization_id = @OrganizationId, paid_at = @PaidAt, plan = @Plan, plan_id = @PlanId, refund_notice_at = @RefundNoticeAt, refund_status = @RefundStatus, refusal_reason = @RefusalReason, renewal_at = @RenewalAt, state = @State, subscription_id = @SubscriptionId, subscription_status = @SubscriptionStatus, user_id = @UserId, version = @Version WHERE draft_id = @DraftId AND version = @OriginalVersion";

        public const string Delete = "DELETE FROM pending_organization_checkouts WHERE draft_id = @DraftId AND version = @OriginalVersion";
    }

    internal static class BillingSkippedMeters
    {
        public const string Select = "SELECT organization_id, month, meter, reason FROM billing_skipped_meters";

        public const string Insert = "INSERT INTO billing_skipped_meters (organization_id, month, meter, reason) VALUES (@OrganizationId, @Month, @Meter, @Reason)";

        public const string Update = "UPDATE billing_skipped_meters SET reason = @Reason WHERE organization_id = @OrganizationId AND month = @Month AND meter = @Meter";

        public const string Delete = "DELETE FROM billing_skipped_meters WHERE organization_id = @OrganizationId AND month = @Month AND meter = @Meter";
    }

    internal static class StripeCheckouts
    {
        public const string Select = "SELECT id, checkout_id, created_at, customer_id, interval, organization_id, plan, project_id, request_id, return_path FROM stripe_checkouts";

        public const string Insert = "INSERT INTO stripe_checkouts (id, checkout_id, created_at, customer_id, interval, organization_id, plan, project_id, request_id, return_path) VALUES (@Id, @CheckoutId, @CreatedAt, @CustomerId, @Interval, @OrganizationId, @Plan, @ProjectId, @RequestId, @ReturnPath)";

        public const string Update = "UPDATE stripe_checkouts SET checkout_id = @CheckoutId, created_at = @CreatedAt, customer_id = @CustomerId, interval = @Interval, organization_id = @OrganizationId, plan = @Plan, project_id = @ProjectId, request_id = @RequestId, return_path = @ReturnPath WHERE id = @Id";

        public const string Delete = "DELETE FROM stripe_checkouts WHERE id = @Id";
    }

    internal static class StripeCustomers
    {
        public const string Select = "SELECT id, created_at, customer_id, organization_id FROM stripe_customers";

        public const string Insert = "INSERT INTO stripe_customers (id, created_at, customer_id, organization_id) VALUES (@Id, @CreatedAt, @CustomerId, @OrganizationId)";

        public const string Update = "UPDATE stripe_customers SET created_at = @CreatedAt, customer_id = @CustomerId, organization_id = @OrganizationId WHERE id = @Id";

        public const string Delete = "DELETE FROM stripe_customers WHERE id = @Id";
    }

    internal static class StripeEventReceipts
    {
        public const string Select = "SELECT id, attempts, completed_at, dead_lettered_at, event_created, event_type, invoice_id, organization_id, processing_until, received_at, subscription_id, version FROM stripe_event_receipts";

        public const string Insert = "INSERT INTO stripe_event_receipts (id, attempts, completed_at, dead_lettered_at, event_created, event_type, invoice_id, organization_id, processing_until, received_at, subscription_id, version) VALUES (@Id, @Attempts, @CompletedAt, @DeadLetteredAt, @EventCreated, @EventType, @InvoiceId, @OrganizationId, @ProcessingUntil, @ReceivedAt, @SubscriptionId, @Version)";

        public const string Update = "UPDATE stripe_event_receipts SET attempts = @Attempts, completed_at = @CompletedAt, dead_lettered_at = @DeadLetteredAt, event_created = @EventCreated, event_type = @EventType, invoice_id = @InvoiceId, organization_id = @OrganizationId, processing_until = @ProcessingUntil, received_at = @ReceivedAt, subscription_id = @SubscriptionId, version = @Version WHERE id = @Id AND version = @OriginalVersion";

        public const string Delete = "DELETE FROM stripe_event_receipts WHERE id = @Id AND version = @OriginalVersion";
    }

    internal static class SavedQueries
    {
        public const string Select = "SELECT id, created_at, database_id, name, organization_id, project_id, sql FROM saved_queries";

        public const string Insert = "INSERT INTO saved_queries (id, created_at, database_id, name, organization_id, project_id, sql) VALUES (@Id, @CreatedAt, @DatabaseId, @Name, @OrganizationId, @ProjectId, @Sql)";

        public const string Update = "UPDATE saved_queries SET created_at = @CreatedAt, database_id = @DatabaseId, name = @Name, organization_id = @OrganizationId, project_id = @ProjectId, sql = @Sql WHERE id = @Id";

        public const string Delete = "DELETE FROM saved_queries WHERE id = @Id";
    }

    internal static class SqlWorkspaces
    {
        public const string Select = "SELECT id, database_id, draft, history, history_epoch, organization_id, project_id, receipt, user_id, version FROM sql_workspaces";

        public const string Insert = "INSERT INTO sql_workspaces (id, database_id, draft, history, history_epoch, organization_id, project_id, receipt, user_id, version) VALUES (@Id, @DatabaseId, @Draft, @History, @HistoryEpoch, @OrganizationId, @ProjectId, @Receipt, @UserId, @Version)";

        public const string Update = "UPDATE sql_workspaces SET database_id = @DatabaseId, draft = @Draft, history = @History, history_epoch = @HistoryEpoch, organization_id = @OrganizationId, project_id = @ProjectId, receipt = @Receipt, user_id = @UserId, version = @Version WHERE id = @Id AND version = @OriginalVersion";

        public const string Delete = "DELETE FROM sql_workspaces WHERE id = @Id AND version = @OriginalVersion";
    }

    internal static class LogDrainCursors
    {
        public const string Select = "SELECT id, cursor, database_id, drain_id FROM log_drain_cursors";

        public const string Insert = "INSERT INTO log_drain_cursors (id, cursor, database_id, drain_id) VALUES (@Id, @Cursor, @DatabaseId, @DrainId)";

        public const string Update = "UPDATE log_drain_cursors SET cursor = @Cursor, database_id = @DatabaseId, drain_id = @DrainId WHERE id = @Id";

        public const string Delete = "DELETE FROM log_drain_cursors WHERE id = @Id";
    }

    internal static class LogDrains
    {
        public const string Select = "SELECT id, created_at, header_ciphertext, header_name, header_nonce, header_tag, last_error_at, last_error_status, last_gap_at, last_success_at, organization_id, project_id, revision, url FROM log_drains";

        public const string Insert = "INSERT INTO log_drains (id, created_at, header_ciphertext, header_name, header_nonce, header_tag, last_error_at, last_error_status, last_gap_at, last_success_at, organization_id, project_id, revision, url) VALUES (@Id, @CreatedAt, @HeaderCiphertext, @HeaderName, @HeaderNonce, @HeaderTag, @LastErrorAt, @LastErrorStatus, @LastGapAt, @LastSuccessAt, @OrganizationId, @ProjectId, @Revision, @Url)";

        public const string Update = "UPDATE log_drains SET created_at = @CreatedAt, header_ciphertext = @HeaderCiphertext, header_name = @HeaderName, header_nonce = @HeaderNonce, header_tag = @HeaderTag, last_error_at = @LastErrorAt, last_error_status = @LastErrorStatus, last_gap_at = @LastGapAt, last_success_at = @LastSuccessAt, organization_id = @OrganizationId, project_id = @ProjectId, revision = @Revision, url = @Url WHERE id = @Id AND revision = @OriginalRevision";

        public const string Delete = "DELETE FROM log_drains WHERE id = @Id AND revision = @OriginalRevision";
    }

    internal static class AlertEvents
    {
        public const string Select = "SELECT id, alert_id, at, database_name, email_status, rule_id, state, value, webhook_attempts, webhook_status FROM alert_events";

        public const string Insert = "INSERT INTO alert_events (id, alert_id, at, database_name, email_status, rule_id, state, value, webhook_attempts, webhook_status) VALUES (@Id, @AlertId, @At, @DatabaseName, @EmailStatus, @RuleId, @State, @Value, @WebhookAttempts, @WebhookStatus)";

        public const string Update = "UPDATE alert_events SET alert_id = @AlertId, at = @At, database_name = @DatabaseName, email_status = @EmailStatus, rule_id = @RuleId, state = @State, value = @Value, webhook_attempts = @WebhookAttempts, webhook_status = @WebhookStatus WHERE id = @Id";

        public const string Delete = "DELETE FROM alert_events WHERE id = @Id";
    }

    internal static class AlertRules
    {
        public const string Select = "SELECT id, alert_id, created_at, database_id, email, metric, name, organization_id, project_id, since, slot, state, threshold, value, version, webhook_ciphertext, webhook_nonce, webhook_tag, webhook_url FROM alert_rules";

        public const string Insert = "INSERT INTO alert_rules (id, alert_id, created_at, database_id, email, metric, name, organization_id, project_id, since, slot, state, threshold, value, version, webhook_ciphertext, webhook_nonce, webhook_tag, webhook_url) VALUES (@Id, @AlertId, @CreatedAt, @DatabaseId, @Email, @Metric, @Name, @OrganizationId, @ProjectId, @Since, @Slot, @State, @Threshold, @Value, @Version, @WebhookCiphertext, @WebhookNonce, @WebhookTag, @WebhookUrl)";

        public const string Update = "UPDATE alert_rules SET alert_id = @AlertId, created_at = @CreatedAt, database_id = @DatabaseId, email = @Email, metric = @Metric, name = @Name, organization_id = @OrganizationId, project_id = @ProjectId, since = @Since, slot = @Slot, state = @State, threshold = @Threshold, value = @Value, version = @Version, webhook_ciphertext = @WebhookCiphertext, webhook_nonce = @WebhookNonce, webhook_tag = @WebhookTag, webhook_url = @WebhookUrl WHERE id = @Id AND version = @OriginalVersion";

        public const string Delete = "DELETE FROM alert_rules WHERE id = @Id AND version = @OriginalVersion";
    }

    internal static class AllowanceAlerts
    {
        public const string Select = "SELECT id, allowance, billing_month, created_at, email_status, metric, organization_id, threshold, used FROM allowance_alerts";

        public const string Insert = "INSERT INTO allowance_alerts (id, allowance, billing_month, created_at, email_status, metric, organization_id, threshold, used) VALUES (@Id, @Allowance, @BillingMonth, @CreatedAt, @EmailStatus, @Metric, @OrganizationId, @Threshold, @Used)";

        public const string Update = "UPDATE allowance_alerts SET allowance = @Allowance, billing_month = @BillingMonth, created_at = @CreatedAt, email_status = @EmailStatus, metric = @Metric, organization_id = @OrganizationId, threshold = @Threshold, used = @Used WHERE id = @Id";

        public const string Delete = "DELETE FROM allowance_alerts WHERE id = @Id";
    }

    internal static class AccountDeletions
    {
        public const string Select = "SELECT id, ciphertext, completed_until, created_at, key_hash, nonce, tag, user_id, version FROM account_deletions";

        public const string Insert = "INSERT INTO account_deletions (id, ciphertext, completed_until, created_at, key_hash, nonce, tag, user_id, version) VALUES (@Id, unhex(@CiphertextHex), @CompletedUntil, @CreatedAt, @KeyHash, unhex(@NonceHex), unhex(@TagHex), @UserId, @Version)";

        public const string Update = "UPDATE account_deletions SET ciphertext = unhex(@CiphertextHex), completed_until = @CompletedUntil, created_at = @CreatedAt, key_hash = @KeyHash, nonce = unhex(@NonceHex), tag = unhex(@TagHex), user_id = @UserId, version = @Version WHERE id = @Id AND version = @OriginalVersion";

        public const string Delete = "DELETE FROM account_deletions WHERE id = @Id AND version = @OriginalVersion";
    }

    internal static class DeletionCleanup
    {
        public const string Select = "SELECT id, acknowledged_at, billing_settled_at, created_at, organization_id, scope, subject_id, targets_complete FROM deletion_cleanup";

        public const string Insert = "INSERT INTO deletion_cleanup (id, acknowledged_at, billing_settled_at, created_at, organization_id, scope, subject_id, targets_complete) VALUES (@Id, @AcknowledgedAt, @BillingSettledAt, @CreatedAt, @OrganizationId, @Scope, @SubjectId, @TargetsComplete)";

        public const string Update = "UPDATE deletion_cleanup SET acknowledged_at = @AcknowledgedAt, billing_settled_at = @BillingSettledAt, created_at = @CreatedAt, organization_id = @OrganizationId, scope = @Scope, subject_id = @SubjectId, targets_complete = @TargetsComplete WHERE id = @Id";

        public const string Delete = "DELETE FROM deletion_cleanup WHERE id = @Id";
    }

    internal static class DeletionCleanupTargets
    {
        public const string Select = "SELECT cleanup_id, kind, target_id FROM deletion_cleanup_targets";

        public const string Insert = "INSERT INTO deletion_cleanup_targets (cleanup_id, kind, target_id) VALUES (@CleanupId, @Kind, @TargetId)";

        public const string Delete = "DELETE FROM deletion_cleanup_targets WHERE cleanup_id = @CleanupId AND kind = @Kind AND target_id = @TargetId";
    }

    internal static class Sessions
    {
        public const string Select = "SELECT id, ciphertext, device, expires_at, kind, nonce, tag, token_hash, user_id, version FROM sessions";

        public const string Insert = "INSERT INTO sessions (id, ciphertext, device, expires_at, kind, nonce, tag, token_hash, user_id, version) VALUES (@Id, unhex(@CiphertextHex), @Device, @ExpiresAt, @Kind, unhex(@NonceHex), unhex(@TagHex), @TokenHash, @UserId, @Version)";

        public const string Update = "UPDATE sessions SET ciphertext = unhex(@CiphertextHex), device = @Device, expires_at = @ExpiresAt, kind = @Kind, nonce = unhex(@NonceHex), tag = unhex(@TagHex), token_hash = @TokenHash, user_id = @UserId, version = @Version WHERE id = @Id AND version = @OriginalVersion";

        public const string Delete = "DELETE FROM sessions WHERE id = @Id AND version = @OriginalVersion";
    }

    internal static class McpClients
    {
        public const string Select = "SELECT id, closed_at, created_at, first_sign_in_completed_at, last_activity_at, name, redirect_uris, version FROM mcp_clients";

        public const string Insert = "INSERT INTO mcp_clients (id, closed_at, created_at, first_sign_in_completed_at, last_activity_at, name, redirect_uris, version) VALUES (@Id, @ClosedAt, @CreatedAt, @FirstSignInCompletedAt, @LastActivityAt, @Name, @RedirectUris, @Version)";

        public const string Update = "UPDATE mcp_clients SET closed_at = @ClosedAt, created_at = @CreatedAt, first_sign_in_completed_at = @FirstSignInCompletedAt, last_activity_at = @LastActivityAt, name = @Name, redirect_uris = @RedirectUris, version = @Version WHERE id = @Id AND version = @OriginalVersion";

        public const string Delete = "DELETE FROM mcp_clients WHERE id = @Id AND version = @OriginalVersion";
    }

    internal static class McpCodes
    {
        public const string Select = "SELECT id, client_id, client_name, code_challenge, code_hash, credential_id, expires_at, redirect_uri, resource, token_expires_at, user_id FROM mcp_codes";

        public const string Insert = "INSERT INTO mcp_codes (id, client_id, client_name, code_challenge, code_hash, credential_id, expires_at, redirect_uri, resource, token_expires_at, user_id) VALUES (@Id, @ClientId, @ClientName, @CodeChallenge, @CodeHash, @CredentialId, @ExpiresAt, @RedirectUri, @Resource, @TokenExpiresAt, @UserId)";

        public const string Update = "UPDATE mcp_codes SET client_id = @ClientId, client_name = @ClientName, code_challenge = @CodeChallenge, code_hash = @CodeHash, credential_id = @CredentialId, expires_at = @ExpiresAt, redirect_uri = @RedirectUri, resource = @Resource, token_expires_at = @TokenExpiresAt, user_id = @UserId WHERE id = @Id";

        public const string Delete = "DELETE FROM mcp_codes WHERE id = @Id";
    }

    internal static class BackgroundJobs
    {
        public const string Select = "SELECT id, attempted_at, available_at, checkpoint, cursor, delivery_id, failures, lease_until, next_cursor, owner, payload, succeeded_at FROM background_jobs";

        public const string Insert = "INSERT INTO background_jobs (id, attempted_at, available_at, checkpoint, cursor, delivery_id, failures, lease_until, next_cursor, owner, payload, succeeded_at) VALUES (@Id, @AttemptedAt, @AvailableAt, @Checkpoint, @Cursor, @DeliveryId, @Failures, @LeaseUntil, @NextCursor, @Owner, unhex(@PayloadHex), @SucceededAt)";

        public const string Update = "UPDATE background_jobs SET attempted_at = @AttemptedAt, available_at = @AvailableAt, checkpoint = @Checkpoint, cursor = @Cursor, delivery_id = @DeliveryId, failures = @Failures, lease_until = @LeaseUntil, next_cursor = @NextCursor, owner = @Owner, payload = unhex(@PayloadHex), succeeded_at = @SucceededAt WHERE id = @Id AND checkpoint = @OriginalCheckpoint AND owner = @OriginalOwner";

        public const string Delete = "DELETE FROM background_jobs WHERE id = @Id AND checkpoint = @OriginalCheckpoint AND owner = @OriginalOwner";
    }
}
