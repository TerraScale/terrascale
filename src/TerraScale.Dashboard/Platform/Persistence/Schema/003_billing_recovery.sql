ALTER TABLE stripe_event_receipts ADD COLUMN attempts INTEGER NOT NULL DEFAULT 0 CHECK (attempts >= 0);
ALTER TABLE stripe_event_receipts ADD COLUMN dead_lettered_at TEXT;
CREATE INDEX ix_stripe_event_receipts_pending ON stripe_event_receipts (processing_until) WHERE completed_at IS NULL;
ALTER TABLE billing_changes ADD COLUMN status_changed_at TEXT;
CREATE INDEX ix_billing_changes_open ON billing_changes (id) WHERE status IN ('reserved', 'applying');
CREATE TABLE stripe_owner_stamps (
    organization_id TEXT NOT NULL,
    subscription_id TEXT NOT NULL,
    stamped_at TEXT NOT NULL,
    CONSTRAINT pk_stripe_owner_stamps PRIMARY KEY (organization_id, subscription_id),
    CONSTRAINT fk_stripe_owner_stamps_billing_organization_id FOREIGN KEY (organization_id) REFERENCES billing (organization_id) ON DELETE CASCADE,
    CONSTRAINT ck_stripe_owner_stamps_ids CHECK (length(organization_id) = 26 AND length(subscription_id) > 0)
);
