CREATE TABLE accounts (
    id TEXT NOT NULL,
    active_organization_id TEXT NOT NULL,
    active_project_id TEXT NOT NULL,
    created_at TEXT NOT NULL,
    email TEXT NOT NULL,
    erasure_id TEXT,
    locale TEXT NOT NULL,
    name TEXT NOT NULL,
    onboarding_completed INTEGER NOT NULL,
    onboarding_step TEXT NOT NULL,
    organization_ids TEXT NOT NULL,
    personal_claim_until TEXT,
    personal_organization_id TEXT NOT NULL,
    phone TEXT NOT NULL,
    subject_hash TEXT NOT NULL,
    user_id TEXT,
    version TEXT NOT NULL,
    CONSTRAINT pk_accounts PRIMARY KEY (id),
    CONSTRAINT ck_accounts_erasure CHECK ((erasure_id IS NULL AND length(user_id) BETWEEN 1 AND 256) OR (length(erasure_id) = 26 AND user_id IS NULL AND name = '' AND email = '' AND phone = '' AND organization_ids = '[]' AND personal_organization_id = '' AND active_organization_id = '' AND active_project_id = '')),
    CONSTRAINT ck_accounts_id CHECK (length(id) = 26),
    CONSTRAINT ck_accounts_organization_ids CHECK (json_type(organization_ids) = 'array'),
    CONSTRAINT ck_accounts_personal_organization_id CHECK (length(personal_organization_id) IN (0, 26)),
    CONSTRAINT ck_accounts_subject_hash CHECK (length(subject_hash) = 64),
    CONSTRAINT ck_accounts_version CHECK (length(version) = 26)
);
CREATE UNIQUE INDEX ix_accounts_subject_hash ON accounts (subject_hash);
CREATE UNIQUE INDEX ix_accounts_user_id ON accounts (user_id);

CREATE TABLE notifications (
    account_id TEXT NOT NULL,
    id TEXT NOT NULL,
    billing_month TEXT NOT NULL,
    body TEXT NOT NULL,
    created_at TEXT NOT NULL,
    organization_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    severity TEXT NOT NULL,
    status TEXT NOT NULL,
    title TEXT NOT NULL,
    type TEXT NOT NULL,
    url TEXT NOT NULL,
    CONSTRAINT pk_notifications PRIMARY KEY (account_id, id),
    CONSTRAINT fk_notifications_accounts_account_id FOREIGN KEY (account_id) REFERENCES accounts (id) ON DELETE CASCADE,
    CONSTRAINT ck_notifications_id CHECK (length(id) BETWEEN 1 AND 128),
    CONSTRAINT ck_notifications_status CHECK (lower(status) IN ('unread', 'read'))
);
CREATE INDEX ix_notifications_organization_id_project_id ON notifications (organization_id, project_id);

CREATE TABLE organizations (
    id TEXT NOT NULL,
    archived INTEGER NOT NULL,
    audit_retention_days INTEGER,
    billing_status TEXT NOT NULL,
    deleted_project_ids TEXT NOT NULL,
    deletion_requested INTEGER NOT NULL,
    erasure_id TEXT,
    internal_billing TEXT,
    internal_billing_alerts TEXT NOT NULL,
    kind TEXT NOT NULL,
    paid_excess INTEGER NOT NULL,
    payment_grace_until TEXT,
    payment_method_validated INTEGER NOT NULL,
    pending_project_deletions TEXT NOT NULL,
    plan_id TEXT NOT NULL,
    spend_cap_cents INTEGER NOT NULL,
    status TEXT NOT NULL,
    version TEXT NOT NULL,
    CONSTRAINT pk_organizations PRIMARY KEY (id),
    CONSTRAINT ck_organizations_audit_retention CHECK (audit_retention_days IS NULL OR audit_retention_days BETWEEN 1 AND 90),
    CONSTRAINT ck_organizations_erasure CHECK (erasure_id IS NULL OR (length(erasure_id) = 26 AND internal_billing IS NULL AND internal_billing_alerts = '{}' AND pending_project_deletions = '{}')),
    CONSTRAINT ck_organizations_id CHECK (length(id) = 26),
    CONSTRAINT ck_organizations_kind CHECK (kind IN ('personal', 'business')),
    CONSTRAINT ck_organizations_project_markers CHECK (json_type(pending_project_deletions) = 'object' AND json_type(deleted_project_ids) = 'array' AND json_type(internal_billing_alerts) = 'object'),
    CONSTRAINT ck_organizations_version CHECK (length(version) = 26)
);

CREATE TABLE audit_events (
    id TEXT NOT NULL,
    action TEXT NOT NULL,
    actor TEXT NOT NULL,
    ip TEXT,
    occurred_at TEXT NOT NULL,
    organization_id TEXT NOT NULL,
    risk TEXT,
    source TEXT NOT NULL,
    target TEXT NOT NULL,
    target_type TEXT NOT NULL,
    CONSTRAINT pk_audit_events PRIMARY KEY (id),
    CONSTRAINT fk_audit_events_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES organizations (id) ON DELETE CASCADE,
    CONSTRAINT ck_audit_events_action CHECK (length(action) > 0),
    CONSTRAINT ck_audit_events_id CHECK (length(id) = 26)
);
CREATE INDEX ix_audit_events_organization_id_occurred_at ON audit_events (organization_id, occurred_at);

CREATE TABLE invitation_reservations (
    id TEXT NOT NULL,
    email TEXT NOT NULL,
    invitation_id TEXT NOT NULL,
    known TEXT NOT NULL,
    organization_id TEXT NOT NULL,
    project_id TEXT,
    reserved_at TEXT NOT NULL,
    CONSTRAINT pk_invitation_reservations PRIMARY KEY (id),
    CONSTRAINT fk_invitation_reservations_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES organizations (id) ON DELETE CASCADE,
    CONSTRAINT ck_invitation_reservations_email CHECK (length(email) > 0),
    CONSTRAINT ck_invitation_reservations_id CHECK (length(id) = 26),
    CONSTRAINT ck_invitation_reservations_known CHECK (json_type(known) = 'array')
);
CREATE UNIQUE INDEX ix_invitation_reservations_organization_id_invitation_id ON invitation_reservations (organization_id, invitation_id) WHERE invitation_id <> '';

CREATE TABLE organization_projects (
    id TEXT NOT NULL,
    is_default INTEGER NOT NULL,
    organization_id TEXT NOT NULL,
    read_limit INTEGER NOT NULL,
    regions TEXT NOT NULL,
    replication_limit INTEGER NOT NULL,
    spend_cap_cents INTEGER NOT NULL,
    status TEXT NOT NULL,
    write_limit INTEGER NOT NULL,
    CONSTRAINT pk_organization_projects PRIMARY KEY (id),
    CONSTRAINT fk_organization_projects_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES organizations (id) ON DELETE CASCADE,
    CONSTRAINT ck_organization_projects_id CHECK (length(id) = 26),
    CONSTRAINT ck_organization_projects_limits CHECK (read_limit >= 0 AND write_limit >= 0 AND replication_limit >= 0),
    CONSTRAINT ck_organization_projects_regions CHECK (json_type(regions) = 'array')
);
CREATE INDEX ix_organization_projects_organization_id ON organization_projects (organization_id);

CREATE TABLE project_databases (
    id TEXT NOT NULL,
    all_regions INTEGER NOT NULL,
    backup_retention_days INTEGER NOT NULL,
    branched_from TEXT,
    conflict_of TEXT,
    created_at TEXT NOT NULL,
    endpoint TEXT NOT NULL,
    expires_at TEXT,
    name TEXT NOT NULL,
    organization_id TEXT NOT NULL,
    placement_revision INTEGER NOT NULL,
    previous_name TEXT,
    project_id TEXT NOT NULL,
    read_limit INTEGER NOT NULL,
    region TEXT NOT NULL,
    regions TEXT NOT NULL,
    replication_limit INTEGER NOT NULL,
    spend_cap_cents INTEGER NOT NULL,
    status TEXT NOT NULL,
    write_limit INTEGER NOT NULL,
    CONSTRAINT pk_project_databases PRIMARY KEY (id),
    CONSTRAINT fk_project_databases_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES organizations (id) ON DELETE CASCADE,
    CONSTRAINT fk_project_databases_organization_projects_project_id FOREIGN KEY (project_id) REFERENCES organization_projects (id) ON DELETE CASCADE,
    CONSTRAINT ck_project_databases_id CHECK (length(id) = 26),
    CONSTRAINT ck_project_databases_limits CHECK (read_limit >= 0 AND write_limit >= 0 AND replication_limit >= 0 AND backup_retention_days >= 0),
    CONSTRAINT ck_project_databases_name CHECK (length(name) > 0),
    CONSTRAINT ck_project_databases_placement_revision CHECK (placement_revision >= 0),
    CONSTRAINT ck_project_databases_regions CHECK (json_type(regions) = 'array')
);
CREATE INDEX ix_project_databases_organization_id ON project_databases (organization_id);
CREATE UNIQUE INDEX ix_project_databases_project_id_name ON project_databases (project_id, name);

CREATE TABLE auth_flows (
    id TEXT NOT NULL,
    ciphertext BLOB NOT NULL,
    created_at TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    nonce BLOB NOT NULL,
    purpose TEXT NOT NULL,
    tag BLOB NOT NULL,
    token_hash TEXT NOT NULL,
    CONSTRAINT pk_auth_flows PRIMARY KEY (id),
    CONSTRAINT ck_auth_flows_id CHECK (length(id) = 26),
    CONSTRAINT ck_auth_flows_lifetime CHECK (expires_at > created_at),
    CONSTRAINT ck_auth_flows_nonce CHECK (length(nonce) = 12),
    CONSTRAINT ck_auth_flows_purpose CHECK (purpose IN ('ts-dashboard-oidc', 'ts-dashboard-step-up', 'ts-dashboard-alert-secret')),
    CONSTRAINT ck_auth_flows_tag CHECK (length(tag) = 16),
    CONSTRAINT ck_auth_flows_token_hash CHECK (length(token_hash) = 64)
);
CREATE INDEX ix_auth_flows_expires_at ON auth_flows (expires_at);
CREATE UNIQUE INDEX ix_auth_flows_token_hash ON auth_flows (token_hash);

CREATE TABLE pending_restores (
    id TEXT NOT NULL,
    created_at TEXT NOT NULL,
    organization_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    source_database_id TEXT NOT NULL,
    CONSTRAINT pk_pending_restores PRIMARY KEY (id),
    CONSTRAINT ck_pending_restores_id CHECK (length(id) = 26 AND length(organization_id) = 26 AND length(project_id) = 26 AND length(source_database_id) = 26)
);
CREATE INDEX ix_pending_restores_organization_id_project_id ON pending_restores (organization_id, project_id);

CREATE TABLE billing (
    organization_id TEXT NOT NULL,
    checkout_id TEXT NOT NULL,
    checkout_project_id TEXT NOT NULL,
    customer_id TEXT NOT NULL,
    effective_interval TEXT NOT NULL,
    effective_plan TEXT NOT NULL,
    erasure_id TEXT,
    expiry_last_logged_at TEXT,
    expiry_unavailable_since TEXT,
    internal_reservation TEXT,
    interval TEXT NOT NULL,
    kind TEXT NOT NULL,
    overage_starts_at TEXT NOT NULL,
    plan TEXT NOT NULL,
    renewal_at TEXT,
    request_id TEXT NOT NULL,
    return_path TEXT NOT NULL,
    revision INTEGER NOT NULL,
    started_at TEXT NOT NULL,
    state TEXT NOT NULL,
    status TEXT NOT NULL,
    subscription_id TEXT NOT NULL,
    tax_location_status TEXT NOT NULL,
    trial_used INTEGER NOT NULL,
    version TEXT NOT NULL,
    CONSTRAINT pk_billing PRIMARY KEY (organization_id),
    CONSTRAINT ck_billing_customer CHECK (customer_id = '' OR customer_id LIKE 'cus\\_%' ESCAPE '\\'),
    CONSTRAINT ck_billing_erasure CHECK (erasure_id IS NULL AND length(request_id) > 0 OR length(erasure_id) = 26 AND length(request_id) = 0),
    CONSTRAINT ck_billing_id CHECK (length(organization_id) = 26 AND length(version) = 26),
    CONSTRAINT ck_billing_internal CHECK ((kind = 'internal') = (internal_reservation IS NOT NULL) AND (kind = 'stripe' OR customer_id = '' AND checkout_id = '' AND subscription_id = '')),
    CONSTRAINT ck_billing_kind CHECK (kind IN ('stripe', 'internal') AND state IN ('reserved', 'active', 'deleting')),
    CONSTRAINT ck_billing_revision CHECK (revision >= 0)
);
CREATE UNIQUE INDEX ix_billing_customer_id ON billing (customer_id) WHERE customer_id <> '';

CREATE TABLE billing_changes (
    id TEXT NOT NULL,
    action TEXT NOT NULL,
    interval TEXT NOT NULL,
    organization_id TEXT NOT NULL,
    period_end TEXT NOT NULL,
    plan TEXT NOT NULL,
    request_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    source_interval TEXT NOT NULL,
    source_plan_key TEXT NOT NULL,
    source_price_id TEXT NOT NULL,
    status TEXT NOT NULL,
    subscription_id TEXT NOT NULL,
    target_price_id TEXT NOT NULL,
    version TEXT NOT NULL,
    CONSTRAINT pk_billing_changes PRIMARY KEY (id),
    CONSTRAINT fk_billing_changes_billing_organization_id FOREIGN KEY (organization_id) REFERENCES billing (organization_id) ON DELETE CASCADE,
    CONSTRAINT ck_billing_changes_action CHECK (action IN ('change', 'undo') AND status IN ('reserved', 'applying', 'applied', 'abandoned')),
    CONSTRAINT ck_billing_changes_id CHECK (length(id) = 26 AND length(version) = 26 AND length(organization_id) = 26),
    CONSTRAINT ck_billing_changes_request CHECK (length(request_id) > 0 AND length(subscription_id) > 0 AND revision >= 1)
);
CREATE UNIQUE INDEX ix_billing_changes_organization_id ON billing_changes (organization_id);

CREATE TABLE billing_subscriptions (
    organization_id TEXT NOT NULL,
    subscription_id TEXT NOT NULL,
    checked_at TEXT NOT NULL,
    customer_id TEXT NOT NULL,
    ended_at TEXT,
    started_at TEXT NOT NULL,
    status TEXT NOT NULL,
    version TEXT NOT NULL,
    CONSTRAINT pk_billing_subscriptions PRIMARY KEY (organization_id, subscription_id),
    CONSTRAINT fk_billing_subscriptions_billing_organization_id FOREIGN KEY (organization_id) REFERENCES billing (organization_id) ON DELETE CASCADE,
    CONSTRAINT ck_billing_subscriptions CHECK (length(subscription_id) > 0 AND length(customer_id) > 0)
);

CREATE TABLE billing_terms (
    organization_id TEXT NOT NULL,
    subscription_id TEXT NOT NULL,
    effective_from TEXT NOT NULL,
    pricing_version TEXT NOT NULL,
    basis TEXT NOT NULL,
    currency TEXT NOT NULL,
    learned_at TEXT NOT NULL,
    operation_rate TEXT NOT NULL,
    operations_allowance INTEGER NOT NULL,
    plan TEXT NOT NULL,
    plan_name TEXT NOT NULL,
    storage_allowance INTEGER NOT NULL,
    storage_rate TEXT NOT NULL,
    CONSTRAINT pk_billing_terms PRIMARY KEY (organization_id, subscription_id, effective_from, pricing_version, basis),
    CONSTRAINT fk_billing_terms_billing_organization_id FOREIGN KEY (organization_id) REFERENCES billing (organization_id) ON DELETE CASCADE,
    CONSTRAINT ck_billing_terms CHECK (length(subscription_id) > 0 AND basis IN ('start_date', 'event', 'first_check', 'unestablished'))
);

CREATE TABLE internal_billing (
    id TEXT NOT NULL,
    organization_id TEXT NOT NULL,
    plan_id TEXT NOT NULL,
    scope TEXT NOT NULL,
    CONSTRAINT pk_internal_billing PRIMARY KEY (id),
    CONSTRAINT fk_internal_billing_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES organizations (id) ON DELETE CASCADE,
    CONSTRAINT ck_internal_billing_id CHECK (length(id) = 26 AND length(organization_id) = 26 AND length(plan_id) = 26),
    CONSTRAINT ck_internal_billing_scope CHECK (scope = 'organization')
);
CREATE INDEX ix_internal_billing_organization_id ON internal_billing (organization_id);
CREATE UNIQUE INDEX ix_internal_billing_scope ON internal_billing (scope);

CREATE TABLE billing_overage_charges (
    id TEXT NOT NULL,
    accepted_at TEXT,
    allowance INTEGER NOT NULL,
    amount_cents INTEGER NOT NULL,
    claim_owner TEXT,
    claim_until TEXT,
    currency TEXT NOT NULL,
    customer_id TEXT NOT NULL,
    description TEXT NOT NULL,
    frozen_at TEXT NOT NULL,
    meter TEXT NOT NULL,
    month TEXT NOT NULL,
    organization_id TEXT NOT NULL,
    plan_name TEXT NOT NULL,
    pricing_version TEXT NOT NULL,
    provider_amount_cents INTEGER,
    provider_id TEXT,
    quantity INTEGER NOT NULL,
    skip_reason TEXT,
    state TEXT NOT NULL,
    subscription_id TEXT NOT NULL,
    unit_rate TEXT NOT NULL,
    usage INTEGER NOT NULL,
    CONSTRAINT pk_billing_overage_charges PRIMARY KEY (id),
    CONSTRAINT fk_billing_overage_charges_billing_organization_id FOREIGN KEY (organization_id) REFERENCES billing (organization_id) ON DELETE CASCADE,
    CONSTRAINT ck_overage_charge CHECK (usage >= 0 AND allowance >= 0 AND quantity >= 0 AND amount_cents >= 0 AND unit_rate >= 0 AND state IN ('pending', 'charged', 'adopted', 'mismatch', 'skipped', 'nothing_due') AND (provider_id IS NULL) = (accepted_at IS NULL) AND (provider_id IS NULL) = (provider_amount_cents IS NULL) AND (provider_id IS NOT NULL) = (state IN ('charged', 'adopted', 'mismatch')) AND (skip_reason IS NOT NULL) = (state = 'skipped') AND (state = 'nothing_due') = (amount_cents = 0) AND (claim_owner IS NULL) = (claim_until IS NULL))
);
CREATE UNIQUE INDEX ix_billing_overage_charges_provider_id ON billing_overage_charges (provider_id) WHERE provider_id IS NOT NULL;
CREATE INDEX ix_billing_overage_charges_state_id ON billing_overage_charges (state, id);
CREATE UNIQUE INDEX ix_billing_overage_charges_organization_id_month_meter ON billing_overage_charges (organization_id, month, meter);

CREATE TABLE pending_organization_checkouts (
    draft_id TEXT NOT NULL,
    checkout_id TEXT NOT NULL,
    created_at TEXT NOT NULL,
    customer_id TEXT NOT NULL,
    interval TEXT NOT NULL,
    name TEXT NOT NULL,
    organization_id TEXT NOT NULL,
    paid_at TEXT,
    plan TEXT NOT NULL,
    plan_id TEXT NOT NULL,
    refund_notice_at TEXT,
    refund_status TEXT NOT NULL,
    refusal_reason TEXT NOT NULL,
    renewal_at TEXT,
    state TEXT NOT NULL,
    subscription_id TEXT NOT NULL,
    subscription_status TEXT NOT NULL,
    user_id TEXT NOT NULL,
    version TEXT NOT NULL,
    CONSTRAINT pk_pending_organization_checkouts PRIMARY KEY (draft_id),
    CONSTRAINT ck_pending_organization_checkouts_checkout CHECK (length(checkout_id) > 0),
    CONSTRAINT ck_pending_organization_checkouts_creating CHECK (state <> 'creating' OR paid_at IS NOT NULL AND length(customer_id) > 0 AND length(subscription_id) > 0 AND subscription_status IN ('active', 'trialing') AND length(plan_id) > 0 AND renewal_at IS NOT NULL AND refusal_reason = ''),
    CONSTRAINT ck_pending_organization_checkouts_id CHECK (length(user_id) = 26 AND length(draft_id) = 26),
    CONSTRAINT ck_pending_organization_checkouts_interval CHECK (interval IN ('monthly', 'annual')),
    CONSTRAINT ck_pending_organization_checkouts_name CHECK (length(name) BETWEEN 1 AND 128),
    CONSTRAINT ck_pending_organization_checkouts_organization CHECK (organization_id = '' OR length(organization_id) = 26),
    CONSTRAINT ck_pending_organization_checkouts_plan CHECK (length(plan) BETWEEN 1 AND 128),
    CONSTRAINT ck_pending_organization_checkouts_refusal CHECK (state NOT IN ('canceling', 'refund_pending') OR length(refusal_reason) > 0),
    CONSTRAINT ck_pending_organization_checkouts_state CHECK (state IN ('pending', 'creating', 'canceling', 'refund_pending'))
);
CREATE UNIQUE INDEX ix_pending_organization_checkouts_checkout_id ON pending_organization_checkouts (checkout_id);
CREATE UNIQUE INDEX ix_pending_organization_checkouts_user_id ON pending_organization_checkouts (user_id) WHERE state <> 'refund_pending';

CREATE TABLE billing_skipped_meters (
    organization_id TEXT NOT NULL,
    month TEXT NOT NULL,
    meter TEXT NOT NULL,
    reason TEXT NOT NULL,
    CONSTRAINT pk_billing_skipped_meters PRIMARY KEY (organization_id, month, meter),
    CONSTRAINT fk_billing_skipped_meters_billing_organization_id FOREIGN KEY (organization_id) REFERENCES billing (organization_id) ON DELETE CASCADE
);

CREATE TABLE stripe_customers (
    id TEXT NOT NULL,
    created_at TEXT NOT NULL,
    customer_id TEXT NOT NULL,
    organization_id TEXT NOT NULL,
    CONSTRAINT pk_stripe_customers PRIMARY KEY (id),
    CONSTRAINT fk_stripe_customers_billing_organization_id FOREIGN KEY (organization_id) REFERENCES billing (organization_id) ON DELETE CASCADE,
    CONSTRAINT ck_stripe_customers_customer CHECK (customer_id LIKE 'cus\\_%' ESCAPE '\\'),
    CONSTRAINT ck_stripe_customers_id CHECK (length(id) = 26 AND length(organization_id) = 26)
);
CREATE UNIQUE INDEX ix_stripe_customers_organization_id ON stripe_customers (organization_id);

CREATE TABLE stripe_checkouts (
    id TEXT NOT NULL,
    checkout_id TEXT NOT NULL,
    created_at TEXT NOT NULL,
    customer_id TEXT NOT NULL,
    interval TEXT NOT NULL,
    organization_id TEXT NOT NULL,
    plan TEXT NOT NULL,
    project_id TEXT NOT NULL,
    request_id TEXT NOT NULL,
    return_path TEXT NOT NULL,
    CONSTRAINT pk_stripe_checkouts PRIMARY KEY (id),
    CONSTRAINT fk_stripe_checkouts_stripe_customers_customer_id FOREIGN KEY (customer_id) REFERENCES stripe_customers (customer_id) ON DELETE CASCADE,
    CONSTRAINT fk_stripe_checkouts_billing_organization_id FOREIGN KEY (organization_id) REFERENCES billing (organization_id) ON DELETE CASCADE,
    CONSTRAINT ck_stripe_checkouts_checkout CHECK (checkout_id LIKE 'cs\\_%' ESCAPE '\\' AND length(request_id) > 0),
    CONSTRAINT ck_stripe_checkouts_id CHECK (length(id) = 26 AND length(organization_id) = 26),
    CONSTRAINT ck_stripe_checkouts_project CHECK (length(project_id) IN (0, 26))
);
CREATE UNIQUE INDEX ix_stripe_checkouts_checkout_id ON stripe_checkouts (checkout_id);
CREATE INDEX ix_stripe_checkouts_customer_id ON stripe_checkouts (customer_id);
CREATE INDEX ix_stripe_checkouts_organization_id ON stripe_checkouts (organization_id);

CREATE TABLE stripe_event_receipts (
    id TEXT NOT NULL,
    completed_at TEXT,
    event_created TEXT NOT NULL,
    event_type TEXT NOT NULL,
    invoice_id TEXT NOT NULL,
    organization_id TEXT NOT NULL,
    processing_until TEXT NOT NULL,
    received_at TEXT NOT NULL,
    subscription_id TEXT NOT NULL,
    version TEXT NOT NULL,
    CONSTRAINT pk_stripe_event_receipts PRIMARY KEY (id)
);
CREATE INDEX ix_stripe_event_receipts_received_at ON stripe_event_receipts (received_at);

CREATE TABLE saved_queries (
    id TEXT NOT NULL,
    created_at TEXT NOT NULL,
    database_id TEXT NOT NULL,
    name TEXT NOT NULL,
    organization_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    sql TEXT NOT NULL,
    CONSTRAINT pk_saved_queries PRIMARY KEY (id),
    CONSTRAINT ck_saved_queries_id CHECK (length(id) = 26),
    CONSTRAINT ck_saved_queries_name CHECK (length(CAST(name AS BLOB)) BETWEEN 1 AND 128),
    CONSTRAINT ck_saved_queries_scope CHECK (length(organization_id) = 26 AND length(project_id) = 26 AND length(database_id) IN (0, 26)),
    CONSTRAINT ck_saved_queries_sql CHECK (length(sql) > 0)
);
CREATE UNIQUE INDEX ix_saved_queries_organization_id_project_id_database_id_name ON saved_queries (organization_id, project_id, database_id, name);

CREATE TABLE sql_workspaces (
    id TEXT NOT NULL,
    database_id TEXT NOT NULL,
    draft TEXT NOT NULL,
    history TEXT NOT NULL,
    history_epoch TEXT NOT NULL,
    organization_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    receipt TEXT,
    user_id TEXT NOT NULL,
    version TEXT NOT NULL,
    CONSTRAINT pk_sql_workspaces PRIMARY KEY (id),
    CONSTRAINT ck_sql_workspaces_draft CHECK (length(CAST(draft AS BLOB)) <= 16384),
    CONSTRAINT ck_sql_workspaces_history CHECK (json_array_length(history) <= 20),
    CONSTRAINT ck_sql_workspaces_id CHECK (length(id) = 26),
    CONSTRAINT ck_sql_workspaces_scope CHECK (length(organization_id) = 26 AND length(project_id) = 26 AND length(database_id) = 26),
    CONSTRAINT ck_sql_workspaces_version CHECK (length(version) = 26)
);
CREATE INDEX ix_sql_workspaces_user_id ON sql_workspaces (user_id);
CREATE UNIQUE INDEX ix_sql_workspaces_organization_id_project_id_database_id_user_id ON sql_workspaces (organization_id, project_id, database_id, user_id);

CREATE TABLE log_drains (
    id TEXT NOT NULL,
    created_at TEXT NOT NULL,
    header_ciphertext TEXT,
    header_name TEXT,
    header_nonce TEXT,
    header_tag TEXT,
    last_error_at TEXT,
    last_error_status INTEGER,
    last_gap_at TEXT,
    last_success_at TEXT,
    organization_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    revision TEXT NOT NULL,
    url TEXT NOT NULL,
    CONSTRAINT pk_log_drains PRIMARY KEY (id),
    CONSTRAINT ck_log_drains_error CHECK ((last_error_at IS NULL) = (last_error_status IS NULL) AND (last_error_status IS NULL OR last_error_status BETWEEN 0 AND 599)),
    CONSTRAINT ck_log_drains_header CHECK ((header_name IS NULL AND header_nonce IS NULL AND header_ciphertext IS NULL AND header_tag IS NULL) OR (length(header_name) BETWEEN 1 AND 64 AND header_nonce IS NOT NULL AND header_ciphertext IS NOT NULL AND header_tag IS NOT NULL)),
    CONSTRAINT ck_log_drains_id CHECK (length(id) = 26 AND length(organization_id) = 26 AND length(project_id) = 26),
    CONSTRAINT ck_log_drains_revision CHECK (length(revision) = 26),
    CONSTRAINT ck_log_drains_url CHECK (url LIKE 'https://%' AND length(url) <= 2048)
);
CREATE INDEX ix_log_drains_organization_id ON log_drains (organization_id);
CREATE UNIQUE INDEX ix_log_drains_project_id ON log_drains (project_id);

CREATE TABLE log_drain_cursors (
    id TEXT NOT NULL,
    cursor TEXT NOT NULL,
    database_id TEXT NOT NULL,
    drain_id TEXT NOT NULL,
    CONSTRAINT pk_log_drain_cursors PRIMARY KEY (id),
    CONSTRAINT fk_log_drain_cursors_log_drains_drain_id FOREIGN KEY (drain_id) REFERENCES log_drains (id) ON DELETE CASCADE,
    CONSTRAINT ck_log_drain_cursors_cursor CHECK (length(cursor) = 26),
    CONSTRAINT ck_log_drain_cursors_id CHECK (length(id) = 26 AND length(drain_id) = 26 AND length(database_id) = 26)
);
CREATE UNIQUE INDEX ix_log_drain_cursors_drain_id_database_id ON log_drain_cursors (drain_id, database_id);

CREATE TABLE alert_rules (
    id TEXT NOT NULL,
    alert_id TEXT,
    created_at TEXT NOT NULL,
    database_id TEXT,
    email INTEGER NOT NULL,
    metric TEXT NOT NULL,
    name TEXT NOT NULL,
    organization_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    since TEXT,
    slot INTEGER NOT NULL,
    state TEXT NOT NULL,
    threshold REAL NOT NULL,
    value REAL,
    version TEXT NOT NULL,
    webhook_ciphertext TEXT,
    webhook_nonce TEXT,
    webhook_tag TEXT,
    webhook_url TEXT,
    CONSTRAINT pk_alert_rules PRIMARY KEY (id),
    CONSTRAINT ck_alert_rules_database CHECK (database_id IS NULL OR length(database_id) = 26),
    CONSTRAINT ck_alert_rules_id CHECK (length(id) = 26 AND length(organization_id) = 26 AND length(project_id) = 26),
    CONSTRAINT ck_alert_rules_metric CHECK (metric IN ('p95_latency_ms', 'error_rate_percent', 'storage_gb', 'operation_units_percent')),
    CONSTRAINT ck_alert_rules_name CHECK (length(name) BETWEEN 1 AND 64),
    CONSTRAINT ck_alert_rules_slot CHECK (slot >= 0 AND slot < 20),
    CONSTRAINT ck_alert_rules_state CHECK ((state = 'ok' AND (since IS NULL) = (alert_id IS NULL)) OR (state = 'firing' AND since IS NOT NULL AND length(alert_id) = 26)),
    CONSTRAINT ck_alert_rules_threshold CHECK (threshold > 0 AND threshold <= 1e12),
    CONSTRAINT ck_alert_rules_version CHECK (length(version) = 26),
    CONSTRAINT ck_alert_rules_webhook CHECK ((webhook_url IS NULL AND webhook_nonce IS NULL AND webhook_ciphertext IS NULL AND webhook_tag IS NULL) OR (webhook_url LIKE 'https://%' AND webhook_nonce IS NOT NULL AND webhook_ciphertext IS NOT NULL AND webhook_tag IS NOT NULL))
);
CREATE INDEX ix_alert_rules_organization_id ON alert_rules (organization_id);
CREATE UNIQUE INDEX ix_alert_rules_project_id_slot ON alert_rules (project_id, slot);

CREATE TABLE alert_events (
    id TEXT NOT NULL,
    alert_id TEXT NOT NULL,
    at TEXT NOT NULL,
    database_name TEXT,
    email_status TEXT NOT NULL,
    rule_id TEXT NOT NULL,
    state TEXT NOT NULL,
    value REAL NOT NULL,
    webhook_attempts INTEGER NOT NULL,
    webhook_status TEXT NOT NULL,
    CONSTRAINT pk_alert_events PRIMARY KEY (id),
    CONSTRAINT fk_alert_events_alert_rules_rule_id FOREIGN KEY (rule_id) REFERENCES alert_rules (id) ON DELETE CASCADE,
    CONSTRAINT ck_alert_events_email_status CHECK (email_status IN ('pending', 'none', 'accepted', 'unknown', 'rejected', 'no_recipients')),
    CONSTRAINT ck_alert_events_id CHECK (length(id) = 26 AND length(rule_id) = 26 AND length(alert_id) = 26),
    CONSTRAINT ck_alert_events_pending CHECK (email_status = 'pending' OR webhook_status = 'pending'),
    CONSTRAINT ck_alert_events_state CHECK (state IN ('firing', 'resolved')),
    CONSTRAINT ck_alert_events_webhook_attempts CHECK (webhook_attempts BETWEEN 0 AND 3),
    CONSTRAINT ck_alert_events_webhook_status CHECK (webhook_status IN ('pending', 'none', 'delivered', 'failed'))
);
CREATE INDEX ix_alert_events_rule_id_id ON alert_events (rule_id, id);

CREATE TABLE allowance_alerts (
    id TEXT NOT NULL,
    allowance INTEGER NOT NULL,
    billing_month TEXT NOT NULL,
    created_at TEXT NOT NULL,
    email_status TEXT NOT NULL,
    metric TEXT NOT NULL,
    organization_id TEXT NOT NULL,
    threshold INTEGER NOT NULL,
    used INTEGER NOT NULL,
    CONSTRAINT pk_allowance_alerts PRIMARY KEY (id),
    CONSTRAINT ck_allowance_alerts_amounts CHECK (used >= 0 AND allowance > 0),
    CONSTRAINT ck_allowance_alerts_billing_month CHECK (length(billing_month) = 7 AND substr(billing_month, 5, 1) = '-'),
    CONSTRAINT ck_allowance_alerts_email_status CHECK (email_status IN ('pending', 'accepted', 'unknown', 'rejected', 'no_recipients')),
    CONSTRAINT ck_allowance_alerts_id CHECK (length(id) = 26 AND length(organization_id) = 26),
    CONSTRAINT ck_allowance_alerts_metric CHECK (metric IN ('operations', 'storage', 'spend')),
    CONSTRAINT ck_allowance_alerts_threshold CHECK (threshold IN (80, 100) AND (metric <> 'spend' OR threshold = 100))
);
CREATE UNIQUE INDEX ix_allowance_alerts_organization_id_billing_month_metric_threshold ON allowance_alerts (organization_id, billing_month, metric, threshold);

CREATE TABLE account_deletions (
    id TEXT NOT NULL,
    ciphertext BLOB,
    completed_until TEXT,
    created_at TEXT NOT NULL,
    key_hash TEXT NOT NULL,
    nonce BLOB,
    tag BLOB,
    user_id TEXT,
    version TEXT NOT NULL,
    CONSTRAINT pk_account_deletions PRIMARY KEY (id),
    CONSTRAINT ck_account_deletions_id CHECK (length(id) = 26 AND length(version) = 26),
    CONSTRAINT ck_account_deletions_key_hash CHECK (length(key_hash) = 64 AND key_hash NOT GLOB '*[^0-9a-f]*'),
    CONSTRAINT ck_account_deletions_state CHECK ((completed_until IS NULL AND length(user_id) > 0 AND length(nonce) = 12 AND length(ciphertext) > 0 AND length(tag) = 16) OR (completed_until IS NOT NULL AND user_id IS NULL AND nonce IS NULL AND ciphertext IS NULL AND tag IS NULL))
);
CREATE INDEX ix_account_deletions_completed_until ON account_deletions (completed_until);
CREATE UNIQUE INDEX ix_account_deletions_key_hash ON account_deletions (key_hash);
CREATE INDEX ix_account_deletions_user_id ON account_deletions (user_id);

CREATE TABLE deletion_cleanup (
    id TEXT NOT NULL,
    acknowledged_at TEXT,
    billing_settled_at TEXT,
    created_at TEXT NOT NULL,
    organization_id TEXT,
    scope TEXT NOT NULL,
    subject_id TEXT NOT NULL,
    CONSTRAINT pk_deletion_cleanup PRIMARY KEY (id),
    CONSTRAINT ck_deletion_cleanup_id CHECK (length(id) = 26 AND length(subject_id) = 26),
    CONSTRAINT ck_deletion_cleanup_scope CHECK ((scope = 'account' AND organization_id IS NULL) OR (scope = 'organization' AND (organization_id IS NULL OR organization_id <> subject_id)) OR (scope = 'project' AND length(organization_id) = 26))
);
CREATE INDEX ix_deletion_cleanup_scope_subject_id ON deletion_cleanup (scope, subject_id);

CREATE TABLE deletion_cleanup_targets (
    cleanup_id TEXT NOT NULL,
    kind TEXT NOT NULL,
    target_id TEXT NOT NULL,
    CONSTRAINT pk_deletion_cleanup_targets PRIMARY KEY (cleanup_id, kind, target_id),
    CONSTRAINT fk_deletion_cleanup_targets_deletion_cleanup_cleanup_id FOREIGN KEY (cleanup_id) REFERENCES deletion_cleanup (id) ON DELETE CASCADE,
    CONSTRAINT ck_deletion_cleanup_targets_kind CHECK ((kind = 'project' AND length(target_id) = 26) OR (kind = 'customer' AND length(target_id) BETWEEN 1 AND 255))
);

CREATE TABLE sessions (
    id TEXT NOT NULL,
    ciphertext BLOB NOT NULL,
    device TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    kind TEXT NOT NULL,
    nonce BLOB NOT NULL,
    tag BLOB NOT NULL,
    token_hash TEXT,
    user_id TEXT NOT NULL,
    version TEXT NOT NULL,
    CONSTRAINT pk_sessions PRIMARY KEY (id),
    CONSTRAINT ck_sessions_id CHECK (length(id) = 26),
    CONSTRAINT ck_sessions_kind CHECK ((kind = 'browser' AND length(token_hash) = 64) OR (kind = 'credential' AND token_hash IS NULL)),
    CONSTRAINT ck_sessions_nonce CHECK (length(nonce) = 12),
    CONSTRAINT ck_sessions_tag CHECK (length(tag) = 16),
    CONSTRAINT ck_sessions_user_id CHECK (length(user_id) BETWEEN 1 AND 128),
    CONSTRAINT ck_sessions_version CHECK (length(version) = 26)
);
CREATE UNIQUE INDEX ix_sessions_token_hash ON sessions (token_hash);
CREATE INDEX ix_sessions_kind_id_expires_at ON sessions (kind, id, expires_at);
CREATE INDEX ix_sessions_user_id_kind_expires_at ON sessions (user_id, kind, expires_at);

CREATE TABLE mcp_clients (
    id TEXT NOT NULL,
    closed_at TEXT,
    created_at TEXT NOT NULL,
    first_sign_in_completed_at TEXT,
    last_activity_at TEXT NOT NULL,
    name TEXT NOT NULL,
    redirect_uris TEXT NOT NULL,
    version TEXT NOT NULL,
    CONSTRAINT pk_mcp_clients PRIMARY KEY (id),
    CONSTRAINT ck_mcp_clients_activity CHECK (last_activity_at >= created_at),
    CONSTRAINT ck_mcp_clients_closed CHECK (closed_at IS NULL OR closed_at >= last_activity_at),
    CONSTRAINT ck_mcp_clients_first_sign_in CHECK ((first_sign_in_completed_at IS NULL AND last_activity_at = created_at) OR (first_sign_in_completed_at BETWEEN created_at AND last_activity_at)),
    CONSTRAINT ck_mcp_clients_id CHECK (length(id) = 26 AND id = upper(id)),
    CONSTRAINT ck_mcp_clients_name CHECK (length(name) BETWEEN 1 AND 256),
    CONSTRAINT ck_mcp_clients_redirect_uris CHECK (json_array_length(redirect_uris) BETWEEN 1 AND 10),
    CONSTRAINT ck_mcp_clients_version CHECK (length(version) = 26)
);
CREATE INDEX ix_mcp_clients_first_sign_in_completed_at_created_at ON mcp_clients (first_sign_in_completed_at, created_at);

CREATE TABLE access_tokens (
    id TEXT NOT NULL,
    created_at TEXT NOT NULL,
    credential_id TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    last_four TEXT NOT NULL,
    mcp_client_id TEXT,
    name TEXT NOT NULL,
    revoked_at TEXT,
    secret_hash TEXT NOT NULL,
    user_id TEXT NOT NULL,
    CONSTRAINT pk_access_tokens PRIMARY KEY (id),
    CONSTRAINT fk_access_tokens_sessions_credential_id FOREIGN KEY (credential_id) REFERENCES sessions (id) ON DELETE CASCADE,
    CONSTRAINT fk_access_tokens_mcp_clients_mcp_client_id FOREIGN KEY (mcp_client_id) REFERENCES mcp_clients (id) ON DELETE CASCADE,
    CONSTRAINT ck_access_tokens_id CHECK (length(id) = 26),
    CONSTRAINT ck_access_tokens_last_four CHECK (length(last_four) = 4),
    CONSTRAINT ck_access_tokens_lifetime CHECK (expires_at > created_at),
    CONSTRAINT ck_access_tokens_name CHECK (length(name) BETWEEN 1 AND 256),
    CONSTRAINT ck_access_tokens_secret_hash CHECK (length(secret_hash) = 64)
);
CREATE UNIQUE INDEX ix_access_tokens_credential_id ON access_tokens (credential_id);
CREATE INDEX ix_access_tokens_mcp_client_id ON access_tokens (mcp_client_id);
CREATE UNIQUE INDEX ix_access_tokens_secret_hash ON access_tokens (secret_hash);
CREATE INDEX ix_access_tokens_user_id_expires_at ON access_tokens (user_id, expires_at);

CREATE TABLE mcp_codes (
    id TEXT NOT NULL,
    client_id TEXT NOT NULL,
    client_name TEXT NOT NULL,
    code_challenge TEXT NOT NULL,
    code_hash TEXT NOT NULL,
    credential_id TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    redirect_uri TEXT NOT NULL,
    resource TEXT NOT NULL,
    token_expires_at TEXT NOT NULL,
    user_id TEXT NOT NULL,
    CONSTRAINT pk_mcp_codes PRIMARY KEY (id),
    CONSTRAINT fk_mcp_codes_mcp_clients_client_id FOREIGN KEY (client_id) REFERENCES mcp_clients (id) ON DELETE CASCADE,
    CONSTRAINT fk_mcp_codes_sessions_credential_id FOREIGN KEY (credential_id) REFERENCES sessions (id) ON DELETE CASCADE,
    CONSTRAINT ck_mcp_codes_code_challenge CHECK (length(code_challenge) = 43),
    CONSTRAINT ck_mcp_codes_code_hash CHECK (length(code_hash) = 64),
    CONSTRAINT ck_mcp_codes_id CHECK (length(id) = 26),
    CONSTRAINT ck_mcp_codes_token_expiry CHECK (token_expires_at > expires_at)
);
CREATE INDEX ix_mcp_codes_client_id ON mcp_codes (client_id);
CREATE UNIQUE INDEX ix_mcp_codes_code_hash ON mcp_codes (code_hash);
CREATE INDEX ix_mcp_codes_credential_id ON mcp_codes (credential_id);
CREATE INDEX ix_mcp_codes_expires_at ON mcp_codes (expires_at);

CREATE TABLE background_jobs (
    id TEXT NOT NULL,
    attempted_at TEXT,
    available_at TEXT NOT NULL,
    checkpoint TEXT NOT NULL,
    cursor TEXT,
    delivery_id TEXT,
    failures INTEGER NOT NULL,
    lease_until TEXT NOT NULL,
    next_cursor TEXT,
    owner TEXT NOT NULL,
    payload BLOB,
    succeeded_at TEXT,
    CONSTRAINT pk_background_jobs PRIMARY KEY (id),
    CONSTRAINT ck_background_jobs_delivery CHECK ((payload IS NULL AND next_cursor IS NULL AND delivery_id IS NULL) OR (payload IS NOT NULL AND (next_cursor IS NULL OR length(next_cursor) = 26) AND length(delivery_id) = 26)),
    CONSTRAINT ck_background_jobs_id CHECK (length(id) BETWEEN 1 AND 256 AND length(owner) = 26 AND length(checkpoint) = 26)
);
