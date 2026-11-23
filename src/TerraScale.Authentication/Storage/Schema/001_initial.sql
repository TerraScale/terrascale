CREATE TABLE account_deletion_receipts (
  instance_id TEXT NOT NULL,
  token_hash TEXT NOT NULL,
  subject_id TEXT NOT NULL,
  generation TEXT NOT NULL,
  expires_at TEXT NOT NULL,
  organization_ids TEXT NOT NULL,
  PRIMARY KEY (instance_id, token_hash)
);

CREATE TABLE challenges (
  instance_id TEXT NOT NULL,
  purpose TEXT NOT NULL,
  token_hash TEXT NOT NULL,
  subject_id TEXT NOT NULL,
  expires_at TEXT NOT NULL,
  consumed INTEGER NOT NULL,
  version TEXT NOT NULL,
  PRIMARY KEY (instance_id, purpose, token_hash),
  CONSTRAINT ck_challenges_token_hash CHECK (length(token_hash) = 64)
);

CREATE TABLE closure_cancellations (
  generation TEXT NOT NULL PRIMARY KEY
);

CREATE TABLE closure_commits (
  generation TEXT NOT NULL PRIMARY KEY,
  committed_at TEXT NOT NULL
);

CREATE TABLE closure_jobs (
  instance_id TEXT NOT NULL,
  scope TEXT NOT NULL,
  subject_id TEXT NOT NULL,
  id TEXT NULL,
  organization_id TEXT NULL,
  requested_by TEXT NOT NULL,
  requested_at TEXT NOT NULL,
  next_attempt_at TEXT NOT NULL,
  account_id TEXT NULL,
  attempts INTEGER NOT NULL,
  failure_code TEXT NULL,
  fence_seen INTEGER NOT NULL,
  generation TEXT NULL,
  committed_at TEXT NULL,
  project_closure_version INTEGER NOT NULL,
  version TEXT NOT NULL,
  PRIMARY KEY (instance_id, scope, subject_id),
  CONSTRAINT ck_closure_jobs_scope CHECK (scope IN ('project', 'organization', 'account')),
  CONSTRAINT ck_closure_jobs_version CHECK (project_closure_version IN (1, 2))
);

CREATE TABLE credential_claims (
  instance_id TEXT NOT NULL,
  kind TEXT NOT NULL,
  "key" TEXT NOT NULL,
  owner_id TEXT NOT NULL,
  version TEXT NOT NULL,
  PRIMARY KEY (instance_id, kind, "key")
);

CREATE TABLE deletion_cursors (
  name TEXT NOT NULL PRIMARY KEY,
  "after" TEXT NULL,
  version TEXT NOT NULL
);

CREATE TABLE deletion_jobs (
  id TEXT NOT NULL PRIMARY KEY,
  instance_id TEXT NOT NULL,
  scope TEXT NOT NULL,
  subject_id TEXT NOT NULL,
  organization_id TEXT NULL,
  requested_at TEXT NOT NULL,
  due_at TEXT NOT NULL,
  next_attempt_at TEXT NOT NULL,
  attempts INTEGER NOT NULL,
  failure_code TEXT NULL,
  auth_logical_verified_at TEXT NULL,
  fence_expires_at TEXT NULL,
  history_status TEXT NULL,
  history_completed_at TEXT NULL,
  erasure_id TEXT NULL,
  closure_generation TEXT NULL,
  dashboard_verified_at TEXT NULL,
  version TEXT NOT NULL,
  CONSTRAINT ck_deletion_jobs_scope CHECK (scope IN ('project', 'organization', 'account'))
);

CREATE TABLE email_budgets (
  "key" TEXT NOT NULL PRIMARY KEY,
  paused_until TEXT NOT NULL,
  version TEXT NOT NULL
);

CREATE TABLE email_configs (
  instance_id TEXT NOT NULL,
  version INTEGER NOT NULL,
  created_at TEXT NULL,
  actor TEXT NULL,
  change TEXT NULL,
  branding TEXT NOT NULL,
  templates TEXT NOT NULL,
  PRIMARY KEY (instance_id, version),
  CONSTRAINT ck_email_configs_version CHECK (version > 0)
);

CREATE TABLE email_deliveries (
  kind TEXT NOT NULL,
  "key" TEXT NOT NULL,
  recipient TEXT NOT NULL,
  status TEXT NOT NULL,
  lease_until TEXT NULL,
  version TEXT NOT NULL,
  PRIMARY KEY (kind, "key", recipient),
  CONSTRAINT ck_email_deliveries_status CHECK (status IN ('pending', 'sending', 'accepted', 'rejected', 'unknown'))
);

CREATE TABLE email_reservations (
  id TEXT NOT NULL PRIMARY KEY,
  at TEXT NOT NULL,
  purpose INTEGER NOT NULL,
  tenant TEXT NOT NULL,
  attempt_id TEXT NULL
);

CREATE TABLE mail_outbox (
  id TEXT NOT NULL PRIMARY KEY,
  state TEXT NOT NULL,
  locale TEXT NOT NULL,
  created_at TEXT NOT NULL,
  lease_until TEXT NOT NULL,
  payload TEXT NULL,
  attempts INTEGER NOT NULL,
  expires_at TEXT NULL,
  tenant TEXT NULL,
  purpose INTEGER NULL,
  code_key TEXT NULL,
  issue_id TEXT NULL,
  version TEXT NOT NULL,
  CONSTRAINT ck_mail_outbox_state CHECK (state IN ('queued', 'ready', 'sending', 'sent', 'rejected', 'failed', 'suppressed', 'unknown'))
);

CREATE TABLE oidc_applications (
  id TEXT NOT NULL,
  instance_id TEXT NOT NULL,
  application_type TEXT NULL,
  client_id TEXT NOT NULL,
  client_secret TEXT NULL,
  secret_bindings TEXT NOT NULL,
  previous_secret_hash TEXT NULL,
  previous_secret_expires_at TEXT NULL,
  client_type TEXT NULL,
  consent_type TEXT NULL,
  display_name TEXT NULL,
  permissions TEXT NOT NULL,
  post_logout_redirect_uris TEXT NOT NULL,
  properties TEXT NOT NULL,
  redirect_uris TEXT NOT NULL,
  allowed_origins TEXT NOT NULL,
  requirements TEXT NOT NULL,
  settings TEXT NOT NULL,
  version TEXT NOT NULL,
  PRIMARY KEY (instance_id, id)
);

CREATE TABLE oidc_authorizations (
  id TEXT NOT NULL PRIMARY KEY,
  instance_id TEXT NOT NULL,
  application_id TEXT NULL,
  creation_date TEXT NULL,
  properties TEXT NOT NULL,
  scopes TEXT NOT NULL,
  status TEXT NULL,
  subject TEXT NULL,
  type TEXT NULL,
  version TEXT NOT NULL,
  CONSTRAINT ck_oidc_authorizations_status CHECK (status IN ('valid', 'revoked'))
);

CREATE TABLE organizations (
  id TEXT NOT NULL PRIMARY KEY,
  instance_id TEXT NOT NULL,
  name TEXT NOT NULL,
  created_by TEXT NOT NULL,
  created_at TEXT NOT NULL,
  owners INTEGER NOT NULL,
  projects INTEGER NOT NULL,
  transfer TEXT NULL,
  require_mfa INTEGER NOT NULL,
  version TEXT NOT NULL,
  CONSTRAINT ck_organizations_counts CHECK (owners >= 0 AND projects >= 0)
);

CREATE TABLE passcodes (
  instance_id TEXT NOT NULL,
  "key" TEXT NOT NULL,
  user_id TEXT NOT NULL,
  email TEXT NOT NULL,
  code_hash TEXT NOT NULL,
  auth_epoch INTEGER NOT NULL,
  expires_at TEXT NOT NULL,
  failed_attempts INTEGER NOT NULL,
  consumed INTEGER NOT NULL,
  window_started_at TEXT NOT NULL,
  issued_in_window INTEGER NOT NULL,
  purpose TEXT NULL,
  password_hash TEXT NULL,
  pending_token_hash TEXT NULL,
  username TEXT NULL,
  auth_stamp TEXT NULL,
  issue_id TEXT NOT NULL,
  issuance_bindings TEXT NOT NULL,
  resets_authentication INTEGER NOT NULL,
  pruned INTEGER NOT NULL,
  step_up_request_id TEXT NULL,
  version TEXT NOT NULL,
  PRIMARY KEY (instance_id, "key"),
  CONSTRAINT ck_passcodes_failed_attempts CHECK (failed_attempts >= 0),
  CONSTRAINT ck_passcodes_purpose CHECK (purpose IS NULL OR purpose IN ('password-reset', 'sign-up', 'step-up', 'enrollment-inbox'))
);

CREATE TABLE rate_limits (
  instance_id TEXT NOT NULL,
  "key" TEXT NOT NULL,
  "window" INTEGER NOT NULL,
  used INTEGER NOT NULL,
  version TEXT NOT NULL,
  PRIMARY KEY (instance_id, "key"),
  CONSTRAINT ck_rate_limits_counts CHECK (window >= 0 AND used >= 0)
);

CREATE TABLE signing_keys (
  instance_id TEXT NOT NULL,
  id TEXT NOT NULL,
  master_key_id TEXT NOT NULL,
  algorithm TEXT NOT NULL,
  secret_box TEXT NOT NULL,
  created_at INTEGER NOT NULL,
  activates_at INTEGER NOT NULL,
  x TEXT NOT NULL,
  y TEXT NOT NULL,
  public_metadata_mac TEXT NOT NULL,
  version TEXT NOT NULL,
  PRIMARY KEY (instance_id, id),
  CONSTRAINT ck_signing_keys_algorithm CHECK (algorithm = 'ES256')
);

CREATE TABLE storage_probes (
  id TEXT NOT NULL PRIMARY KEY,
  checked_at TEXT NOT NULL,
  version TEXT NOT NULL
);

CREATE TABLE support_deliveries (
  request_id TEXT NOT NULL PRIMARY KEY,
  instance_id TEXT NOT NULL,
  user_id TEXT NOT NULL,
  status TEXT NOT NULL,
  acknowledgement_status TEXT NULL,
  version TEXT NOT NULL
);

CREATE TABLE users (
  id TEXT NOT NULL PRIMARY KEY,
  instance_id TEXT NOT NULL,
  email TEXT NOT NULL,
  auth_epoch INTEGER NOT NULL,
  created_at TEXT NOT NULL,
  password_hash TEXT NULL,
  platform_role TEXT NULL,
  first_name TEXT NULL,
  last_name TEXT NULL,
  image_url TEXT NULL,
  external_id TEXT NULL,
  public_metadata TEXT NOT NULL DEFAULT '{}',
  private_metadata TEXT NOT NULL DEFAULT '{}',
  unsafe_metadata TEXT NOT NULL DEFAULT '{}',
  custom_claims TEXT NULL,
  email_verified INTEGER NOT NULL DEFAULT 1,
  username TEXT NULL,
  auth_stamp TEXT NOT NULL,
  session_stamp TEXT NULL,
  disabled INTEGER NOT NULL,
  is_anonymous INTEGER NOT NULL,
  version TEXT NOT NULL,
  CONSTRAINT ck_users_auth_epoch CHECK (auth_epoch >= 0),
  CONSTRAINT ck_users_email CHECK (length(email) BETWEEN 3 AND 254 AND email = lower(email)),
  CONSTRAINT ck_users_platform_role CHECK (platform_role IS NULL OR platform_role = 'admin'),
  CONSTRAINT ck_users_username CHECK (username IS NULL OR (length(username) BETWEEN 3 AND 32 AND username = lower(username) AND username NOT GLOB '*[^a-z0-9_.]*'))
);

CREATE TABLE oidc_tokens (
  id TEXT NOT NULL PRIMARY KEY,
  bindings TEXT NOT NULL,
  instance_id TEXT NOT NULL,
  application_id TEXT NULL,
  authorization_id TEXT NULL,
  creation_date TEXT NULL,
  expiration_date TEXT NULL,
  payload TEXT NULL,
  properties TEXT NOT NULL,
  redemption_date TEXT NULL,
  reference_id TEXT NULL,
  status TEXT NULL,
  subject TEXT NULL,
  type TEXT NULL,
  version TEXT NOT NULL,
  CONSTRAINT fk_oidc_tokens_oidc_authorizations_authorization_id FOREIGN KEY (authorization_id) REFERENCES oidc_authorizations (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE audit_events (
  id TEXT NOT NULL PRIMARY KEY,
  instance_id TEXT NOT NULL,
  organization_id TEXT NOT NULL,
  actor TEXT NOT NULL,
  "action" TEXT NOT NULL,
  target TEXT NULL,
  detail TEXT NULL,
  occurred_at TEXT NOT NULL,
  CONSTRAINT fk_audit_events_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES organizations (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE projects (
  id TEXT NOT NULL PRIMARY KEY,
  instance_id TEXT NOT NULL,
  organization_id TEXT NOT NULL,
  name TEXT NOT NULL,
  created_by TEXT NOT NULL,
  created_at TEXT NOT NULL,
  closure_id TEXT NULL,
  allowed_cidrs TEXT NULL,
  name_key TEXT NOT NULL,
  version TEXT NOT NULL,
  CONSTRAINT fk_projects_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES organizations (id) ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE sso_connections (
  domain TEXT NOT NULL PRIMARY KEY,
  instance_id TEXT NOT NULL,
  organization_id TEXT NOT NULL,
  protocol TEXT NOT NULL,
  default_role TEXT NOT NULL,
  verification_token TEXT NOT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  verified_at TEXT NULL,
  issuer TEXT NULL,
  client_id TEXT NULL,
  client_secret_box TEXT NULL,
  idp_entity_id TEXT NULL,
  sso_url TEXT NULL,
  certificate TEXT NULL,
  version TEXT NOT NULL,
  CONSTRAINT ck_sso_connections_protocol CHECK (protocol IN ('oidc', 'saml')),
  CONSTRAINT fk_sso_connections_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES organizations (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE account_locks (
  instance_id TEXT NOT NULL,
  subject TEXT NOT NULL,
  user_id TEXT NULL,
  failures INTEGER NOT NULL,
  locked_until TEXT NULL,
  unlock_hash TEXT NULL,
  last_attempt TEXT NOT NULL,
  version TEXT NOT NULL,
  PRIMARY KEY (instance_id, subject),
  CONSTRAINT ck_account_locks_failures CHECK (failures BETWEEN 0 AND 10),
  CONSTRAINT fk_account_locks_users_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE agentid_clients (
  id TEXT NOT NULL PRIMARY KEY,
  owner_user_id TEXT NOT NULL,
  name TEXT NOT NULL,
  redirect_uris TEXT NOT NULL,
  secret_digest TEXT NOT NULL,
  created_at TEXT NOT NULL,
  previous_secret_digest TEXT NULL,
  previous_secret_expires_at TEXT NULL,
  version TEXT NOT NULL,
  CONSTRAINT ck_agentid_clients_previous_secret CHECK ((previous_secret_digest IS NULL) = (previous_secret_expires_at IS NULL)),
  CONSTRAINT fk_agentid_clients_users_owner_user_id FOREIGN KEY (owner_user_id) REFERENCES users (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE agentid_transactions (
  id TEXT NOT NULL PRIMARY KEY,
  request_hash TEXT NOT NULL,
  client_id TEXT NOT NULL,
  created_at TEXT NOT NULL,
  expires_at TEXT NOT NULL,
  agent_id TEXT NULL,
  owner_user_id TEXT NULL,
  kid TEXT NULL,
  proved_at TEXT NULL,
  completed_at TEXT NULL,
  version TEXT NOT NULL,
  CONSTRAINT ck_agentid_transactions_expiry CHECK (expires_at > created_at),
  CONSTRAINT ck_agentid_transactions_proof CHECK ((proved_at IS NULL) = (agent_id IS NULL) AND (agent_id IS NULL) = (owner_user_id IS NULL) AND (agent_id IS NULL) = (kid IS NULL) AND (completed_at IS NULL OR proved_at IS NOT NULL)),
  CONSTRAINT fk_agentid_transactions_users_owner_user_id FOREIGN KEY (owner_user_id) REFERENCES users (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE email_changes (
  user_id TEXT NOT NULL PRIMARY KEY,
  instance_id TEXT NOT NULL,
  auth_epoch INTEGER NOT NULL,
  auth_stamp TEXT NULL,
  new_email TEXT NOT NULL,
  code_hash TEXT NOT NULL,
  cancel_hash TEXT NOT NULL,
  expires_at TEXT NOT NULL,
  failed_attempts INTEGER NOT NULL,
  bindings TEXT NOT NULL,
  version TEXT NOT NULL,
  CONSTRAINT ck_email_changes_failed_attempts CHECK (failed_attempts >= 0),
  CONSTRAINT fk_email_changes_users_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE impersonations (
  id TEXT NOT NULL PRIMARY KEY,
  instance_id TEXT NOT NULL,
  organization_id TEXT NOT NULL,
  user_id TEXT NOT NULL,
  actor_id TEXT NOT NULL,
  actor_email TEXT NOT NULL,
  actor_stamp TEXT NOT NULL,
  actor_epoch INTEGER NOT NULL,
  user_stamp TEXT NOT NULL,
  user_epoch INTEGER NOT NULL,
  reason TEXT NOT NULL,
  client_id TEXT NOT NULL,
  ticket_hash TEXT NOT NULL,
  created_at TEXT NOT NULL,
  ticket_expires_at TEXT NOT NULL,
  redeemed_at TEXT NULL,
  expires_at TEXT NULL,
  ended_at TEXT NULL,
  cookie_hash TEXT NULL,
  ticket_owner TEXT NULL,
  version TEXT NOT NULL,
  CONSTRAINT ck_impersonations_expiry CHECK (expires_at IS NULL OR (redeemed_at IS NOT NULL AND unixepoch(expires_at) BETWEEN unixepoch(redeemed_at) AND unixepoch(redeemed_at) + 3600)),
  CONSTRAINT ck_impersonations_reason CHECK (length(reason) BETWEEN 1 AND 500),
  CONSTRAINT ck_impersonations_tenant CHECK (instance_id <> 'terrascale'),
  CONSTRAINT ck_impersonations_ticket CHECK (length(ticket_hash) = 64),
  CONSTRAINT fk_impersonations_users_actor_id FOREIGN KEY (actor_id) REFERENCES users (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
  CONSTRAINT fk_impersonations_users_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE known_devices (
  instance_id TEXT NOT NULL,
  user_id TEXT NOT NULL,
  fingerprint TEXT NOT NULL,
  last_seen TEXT NOT NULL,
  version TEXT NOT NULL,
  PRIMARY KEY (instance_id, user_id, fingerprint),
  CONSTRAINT fk_known_devices_users_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE memberships (
  organization_id TEXT NOT NULL,
  user_id TEXT NOT NULL,
  instance_id TEXT NOT NULL,
  role TEXT NULL,
  joined_at TEXT NOT NULL,
  version TEXT NOT NULL,
  PRIMARY KEY (organization_id, user_id),
  CONSTRAINT ck_memberships_role CHECK (role IS NULL OR role IN ('viewer', 'member', 'admin', 'owner')),
  CONSTRAINT fk_memberships_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES organizations (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
  CONSTRAINT fk_memberships_users_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE mfa_credentials (
  user_id TEXT NOT NULL PRIMARY KEY,
  instance_id TEXT NOT NULL,
  secret_box TEXT NULL,
  enabled INTEGER NOT NULL,
  enrollment_epoch INTEGER NOT NULL,
  enrollment_expires_at TEXT NULL,
  last_accepted_step INTEGER NOT NULL,
  recovery_hashes TEXT NOT NULL,
  recovery_expires_at TEXT NULL,
  version TEXT NOT NULL,
  CONSTRAINT ck_mfa_credentials_steps CHECK (enrollment_epoch >= 0 AND last_accepted_step >= -1),
  CONSTRAINT fk_mfa_credentials_users_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE passkeys (
  credential_id TEXT NOT NULL,
  instance_id TEXT NOT NULL,
  public_key_cose BLOB NOT NULL,
  sign_count INTEGER NOT NULL,
  transports TEXT NOT NULL,
  name TEXT NULL,
  aaguid TEXT NOT NULL,
  created_at TEXT NOT NULL,
  last_used_at TEXT NULL,
  user_id TEXT NOT NULL,
  version TEXT NOT NULL,
  PRIMARY KEY (instance_id, credential_id),
  CONSTRAINT fk_passkeys_users_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE provider_links (
  instance_id TEXT NOT NULL,
  provider TEXT NOT NULL,
  issuer TEXT NOT NULL,
  client_id TEXT NOT NULL,
  subject TEXT NOT NULL,
  user_id TEXT NOT NULL,
  version TEXT NOT NULL,
  PRIMARY KEY (instance_id, provider, issuer, client_id, subject),
  CONSTRAINT fk_provider_links_users_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE sessions (
  id TEXT NOT NULL PRIMARY KEY,
  instance_id TEXT NOT NULL,
  user_id TEXT NOT NULL,
  token_hash TEXT NOT NULL,
  auth_epoch INTEGER NOT NULL,
  created_at TEXT NOT NULL,
  expires_at TEXT NOT NULL,
  revoked INTEGER NOT NULL,
  last_activity_at TEXT NULL,
  verified_at TEXT NULL,
  session_stamp TEXT NULL,
  auth_stamp TEXT NULL,
  bindings TEXT NOT NULL,
  mfa INTEGER NOT NULL,
  mfa_method TEXT NULL,
  version TEXT NOT NULL,
  CONSTRAINT ck_sessions_token_hash CHECK (length(token_hash) = 64),
  CONSTRAINT fk_sessions_users_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE step_up_deletion_grants (
  instance_id TEXT NOT NULL,
  receipt_hash TEXT NOT NULL,
  user_id TEXT NOT NULL,
  auth_epoch INTEGER NOT NULL,
  auth_stamp TEXT NULL,
  expires_at TEXT NOT NULL,
  authorization_hash TEXT NOT NULL,
  bindings TEXT NOT NULL,
  version TEXT NOT NULL,
  PRIMARY KEY (instance_id, receipt_hash),
  CONSTRAINT fk_step_up_deletion_grants_users_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE step_up_proofs (
  id TEXT NOT NULL PRIMARY KEY,
  instance_id TEXT NOT NULL,
  user_id TEXT NOT NULL,
  auth_epoch INTEGER NOT NULL,
  auth_stamp TEXT NULL,
  token_hash TEXT NOT NULL,
  purpose TEXT NOT NULL,
  audience TEXT NOT NULL,
  return_uri TEXT NOT NULL,
  expires_at TEXT NOT NULL,
  primary_verified INTEGER NOT NULL,
  approved INTEGER NOT NULL,
  consumed INTEGER NOT NULL,
  mail_requested INTEGER NOT NULL,
  bindings TEXT NULL,
  organization_id TEXT NULL,
  version TEXT NOT NULL,
  CONSTRAINT fk_step_up_proofs_users_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE agent_policies (
  project_id TEXT NOT NULL PRIMARY KEY,
  allowed_scopes TEXT NOT NULL,
  version TEXT NOT NULL,
  CONSTRAINT fk_agent_policies_projects_project_id FOREIGN KEY (project_id) REFERENCES projects (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE agents (
  project_id TEXT NOT NULL,
  id TEXT NOT NULL,
  owner_user_id TEXT NOT NULL,
  name TEXT NOT NULL,
  status TEXT NOT NULL,
  allowed_scopes TEXT NOT NULL,
  allow_self_subject INTEGER NOT NULL,
  created_at TEXT NOT NULL,
  expires_at TEXT NULL,
  revoked_at TEXT NULL,
  last_used_at TEXT NULL,
  owner_auth_epoch INTEGER NOT NULL,
  owner_auth_stamp TEXT NOT NULL,
  share_owner_email INTEGER NOT NULL,
  version TEXT NOT NULL,
  PRIMARY KEY (project_id, id),
  CONSTRAINT ck_agents_expiry CHECK (expires_at IS NULL OR expires_at > created_at),
  CONSTRAINT ck_agents_status CHECK (status IN ('active', 'revoked') AND (status = 'revoked') = (revoked_at IS NOT NULL)),
  CONSTRAINT fk_agents_projects_project_id FOREIGN KEY (project_id) REFERENCES projects (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
  CONSTRAINT fk_agents_users_owner_user_id FOREIGN KEY (owner_user_id) REFERENCES users (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE api_keys (
  id TEXT NOT NULL PRIMARY KEY,
  instance_id TEXT NOT NULL,
  organization_id TEXT NOT NULL,
  project_id TEXT NULL,
  "database" TEXT NULL,
  name TEXT NOT NULL,
  scopes TEXT NOT NULL,
  secret_hash TEXT NOT NULL,
  previous_secret_hash TEXT NULL,
  previous_secret_expires_at TEXT NULL,
  created_by TEXT NOT NULL,
  created_at TEXT NOT NULL,
  expires_at TEXT NULL,
  allowed_cidrs TEXT NULL,
  secret_bindings TEXT NOT NULL,
  previous_secret_bindings TEXT NOT NULL,
  parent_key_id TEXT NULL,
  parent_generation TEXT NULL,
  version TEXT NOT NULL,
  CONSTRAINT fk_api_keys_api_keys_parent_key_id FOREIGN KEY (parent_key_id) REFERENCES api_keys (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
  CONSTRAINT fk_api_keys_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES organizations (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
  CONSTRAINT fk_api_keys_projects_project_id FOREIGN KEY (project_id) REFERENCES projects (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE invitations (
  id TEXT NOT NULL PRIMARY KEY,
  instance_id TEXT NOT NULL,
  organization_id TEXT NOT NULL,
  project_id TEXT NULL,
  email TEXT NOT NULL,
  role TEXT NOT NULL,
  invited_by TEXT NOT NULL,
  created_at TEXT NOT NULL,
  expires_at TEXT NOT NULL,
  status TEXT NOT NULL,
  token_hash TEXT NOT NULL,
  decided_at TEXT NULL,
  decided_by TEXT NULL,
  target TEXT NOT NULL,
  version TEXT NOT NULL,
  CONSTRAINT ck_invitations_status CHECK (status IN ('pending', 'accepted', 'declined', 'revoked', 'expired')),
  CONSTRAINT fk_invitations_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES organizations (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
  CONSTRAINT fk_invitations_projects_project_id FOREIGN KEY (project_id) REFERENCES projects (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE project_auth_settings (
  instance_id TEXT NOT NULL PRIMARY KEY,
  passcode INTEGER NOT NULL,
  password INTEGER NOT NULL,
  sign_up_mode TEXT NOT NULL,
  name TEXT NULL,
  logo_url TEXT NULL,
  theme_color TEXT NULL,
  providers TEXT NULL,
  title_size INTEGER NULL,
  default_theme TEXT NULL,
  footer_links TEXT NULL,
  background_color TEXT NULL,
  text_color TEXT NULL,
  configuration TEXT NULL,
  email_message TEXT NULL,
  subscribed_webhook_events TEXT NULL,
  restrictions TEXT NULL,
  magic_link INTEGER NOT NULL,
  username INTEGER NOT NULL,
  organization_picker INTEGER NOT NULL DEFAULT 0,
  attack_protection TEXT NOT NULL,
  version TEXT NOT NULL,
  CONSTRAINT ck_project_auth_settings_title_size CHECK (title_size IS NULL OR title_size BETWEEN 20 AND 40),
  CONSTRAINT fk_project_auth_settings_projects_instance_id FOREIGN KEY (instance_id) REFERENCES projects (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE project_invitations (
  id TEXT NOT NULL PRIMARY KEY,
  project_id TEXT NOT NULL,
  email TEXT NOT NULL,
  token_hash TEXT NOT NULL,
  redirect_url TEXT NULL,
  created_at TEXT NOT NULL,
  expires_at TEXT NOT NULL,
  status TEXT NOT NULL,
  version TEXT NOT NULL,
  CONSTRAINT ck_project_invitation_status CHECK (status IN ('pending', 'accepted', 'revoked')),
  CONSTRAINT fk_project_invitations_projects_project_id FOREIGN KEY (project_id) REFERENCES projects (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE project_members (
  project_id TEXT NOT NULL,
  user_id TEXT NOT NULL,
  instance_id TEXT NOT NULL,
  organization_id TEXT NOT NULL,
  role TEXT NOT NULL,
  granted_at TEXT NOT NULL,
  version TEXT NOT NULL,
  PRIMARY KEY (project_id, user_id),
  CONSTRAINT ck_project_members_role CHECK (role IN ('viewer', 'member', 'admin')),
  CONSTRAINT fk_project_members_organizations_organization_id FOREIGN KEY (organization_id) REFERENCES organizations (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
  CONSTRAINT fk_project_members_projects_project_id FOREIGN KEY (project_id) REFERENCES projects (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
  CONSTRAINT fk_project_members_users_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE project_organizations (
  project_id TEXT NOT NULL,
  id TEXT NOT NULL,
  name TEXT NOT NULL,
  slug TEXT NOT NULL,
  image_url TEXT NULL,
  public_metadata TEXT NOT NULL,
  private_metadata TEXT NOT NULL,
  max_members INTEGER NULL,
  domain_join TEXT NOT NULL,
  members INTEGER NOT NULL,
  admins INTEGER NOT NULL,
  created_at TEXT NOT NULL,
  version TEXT NOT NULL,
  PRIMARY KEY (project_id, id),
  CONSTRAINT ck_project_organizations_counts CHECK (admins >= 0 AND (members = 0 OR admins >= 1) AND members >= admins AND (max_members IS NULL OR members <= max_members)),
  CONSTRAINT ck_project_organizations_join CHECK (domain_join IN ('off', 'suggest', 'auto')),
  CONSTRAINT ck_project_organizations_slug CHECK (length(slug) BETWEEN 3 AND 63 AND slug = lower(slug)),
  CONSTRAINT fk_project_organizations_projects_project_id FOREIGN KEY (project_id) REFERENCES projects (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE project_waitlist (
  id TEXT NOT NULL PRIMARY KEY,
  project_id TEXT NOT NULL,
  email TEXT NOT NULL,
  created_at TEXT NOT NULL,
  status TEXT NOT NULL,
  version TEXT NOT NULL,
  CONSTRAINT ck_project_waitlist_status CHECK (status IN ('pending', 'approved', 'rejected')),
  CONSTRAINT fk_project_waitlist_projects_project_id FOREIGN KEY (project_id) REFERENCES projects (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE user_api_key_policies (
  project_id TEXT NOT NULL PRIMARY KEY,
  allowed_scopes TEXT NOT NULL,
  version TEXT NOT NULL,
  CONSTRAINT fk_user_api_key_policies_projects_project_id FOREIGN KEY (project_id) REFERENCES projects (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE user_api_keys (
  project_id TEXT NOT NULL,
  id TEXT NOT NULL,
  user_id TEXT NOT NULL,
  name TEXT NOT NULL,
  prefix TEXT NOT NULL,
  secret_hash TEXT NOT NULL,
  scopes TEXT NOT NULL,
  created_at TEXT NOT NULL,
  expires_at TEXT NULL,
  auth_epoch INTEGER NOT NULL,
  auth_stamp TEXT NOT NULL,
  version TEXT NOT NULL,
  PRIMARY KEY (project_id, id),
  CONSTRAINT ck_user_api_keys_expiry CHECK (expires_at IS NULL OR expires_at > created_at),
  CONSTRAINT fk_user_api_keys_projects_project_id FOREIGN KEY (project_id) REFERENCES projects (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
  CONSTRAINT fk_user_api_keys_users_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE webhook_endpoints (
  id TEXT NOT NULL PRIMARY KEY,
  instance_id TEXT NOT NULL,
  project_id TEXT NOT NULL,
  url TEXT NOT NULL,
  event_types TEXT NOT NULL,
  secret_box TEXT NOT NULL,
  previous_secret_box TEXT NULL,
  previous_secret_expires_at TEXT NULL,
  created_at TEXT NOT NULL,
  version TEXT NOT NULL,
  CONSTRAINT fk_webhook_endpoints_projects_project_id FOREIGN KEY (project_id) REFERENCES projects (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE webhook_outbox (
  event_id TEXT NOT NULL PRIMARY KEY,
  instance_id TEXT NOT NULL,
  project_id TEXT NOT NULL,
  event_type TEXT NOT NULL,
  occurred_at TEXT NOT NULL,
  user_id TEXT NOT NULL,
  email TEXT NULL,
  provider TEXT NULL,
  status TEXT NOT NULL,
  attempts INTEGER NOT NULL,
  next_attempt_at TEXT NOT NULL,
  session_id TEXT NULL,
  endpoint_id TEXT NULL,
  actor_id TEXT NULL,
  reason TEXT NULL,
  organization_id TEXT NULL,
  organization_role TEXT NULL,
  agent_id TEXT NULL,
  agent_key_id TEXT NULL,
  version TEXT NOT NULL,
  CONSTRAINT ck_webhook_outbox_attempts CHECK (attempts >= 0),
  CONSTRAINT fk_webhook_outbox_projects_project_id FOREIGN KEY (project_id) REFERENCES projects (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE sso_identity_links (
  domain TEXT NOT NULL,
  issuer TEXT NOT NULL,
  subject TEXT NOT NULL,
  organization_id TEXT NOT NULL,
  user_id TEXT NOT NULL,
  connection_updated_at TEXT NOT NULL,
  version TEXT NOT NULL,
  PRIMARY KEY (domain, issuer, subject),
  CONSTRAINT fk_sso_identity_links_memberships_organization_id_user_id FOREIGN KEY (organization_id, user_id) REFERENCES memberships (organization_id, user_id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
  CONSTRAINT fk_sso_identity_links_sso_connections_domain FOREIGN KEY (domain) REFERENCES sso_connections (domain) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE agent_assertions (
  project_id TEXT NOT NULL,
  agent_id TEXT NOT NULL,
  jti TEXT NOT NULL,
  keep_until TEXT NOT NULL,
  version TEXT NOT NULL,
  PRIMARY KEY (project_id, agent_id, jti),
  CONSTRAINT fk_agent_assertions_agents_project_id_agent_id FOREIGN KEY (project_id, agent_id) REFERENCES agents (project_id, id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE agent_enrollments (
  project_id TEXT NOT NULL,
  id TEXT NOT NULL,
  user_code_hash TEXT NOT NULL,
  name TEXT NOT NULL,
  requested_scopes TEXT NOT NULL,
  allow_self_subject INTEGER NOT NULL,
  public_jwk TEXT NOT NULL,
  thumbprint TEXT NOT NULL,
  status TEXT NOT NULL,
  created_at TEXT NOT NULL,
  expires_at TEXT NOT NULL,
  user_id TEXT NULL,
  agent_id TEXT NULL,
  kid TEXT NULL,
  last_poll_at TEXT NULL,
  last_proof_issued_at INTEGER NULL,
  version TEXT NOT NULL,
  PRIMARY KEY (project_id, id),
  CONSTRAINT ck_agent_enrollments_expiry CHECK (expires_at > created_at),
  CONSTRAINT ck_agent_enrollments_status CHECK (status IN ('pending', 'approved', 'denied', 'collected') AND (status = 'pending') = (user_id IS NULL) AND (status IN ('approved', 'collected')) = (agent_id IS NOT NULL AND kid IS NOT NULL)),
  CONSTRAINT fk_agent_enrollments_agents_project_id_agent_id FOREIGN KEY (project_id, agent_id) REFERENCES agents (project_id, id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
  CONSTRAINT fk_agent_enrollments_projects_project_id FOREIGN KEY (project_id) REFERENCES projects (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
  CONSTRAINT fk_agent_enrollments_users_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE agent_keys (
  project_id TEXT NOT NULL,
  kid TEXT NOT NULL,
  agent_id TEXT NOT NULL,
  public_jwk TEXT NOT NULL,
  thumbprint TEXT NOT NULL,
  created_at TEXT NOT NULL,
  revoked_at TEXT NULL,
  version TEXT NOT NULL,
  PRIMARY KEY (project_id, kid),
  CONSTRAINT fk_agent_keys_agents_project_id_agent_id FOREIGN KEY (project_id, agent_id) REFERENCES agents (project_id, id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE api_key_usage (
  key_id TEXT NOT NULL PRIMARY KEY,
  instance_id TEXT NOT NULL,
  organization_id TEXT NOT NULL,
  project_id TEXT NULL,
  last_used_at TEXT NOT NULL,
  version TEXT NOT NULL,
  CONSTRAINT fk_api_key_usage_api_keys_key_id FOREIGN KEY (key_id) REFERENCES api_keys (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE project_organization_dismissals (
  project_id TEXT NOT NULL,
  organization_id TEXT NOT NULL,
  user_id TEXT NOT NULL,
  version TEXT NOT NULL,
  PRIMARY KEY (project_id, organization_id, user_id),
  CONSTRAINT fk_project_organization_dismissals_project_organizations_project_id_organization_id FOREIGN KEY (project_id, organization_id) REFERENCES project_organizations (project_id, id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
  CONSTRAINT fk_project_organization_dismissals_users_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE project_organization_domains (
  project_id TEXT NOT NULL,
  organization_id TEXT NOT NULL,
  domain TEXT NOT NULL,
  verification_token TEXT NOT NULL,
  verified_at TEXT NULL,
  checked_at TEXT NULL,
  failed_checks INTEGER NOT NULL,
  lease_until TEXT NULL,
  version TEXT NOT NULL,
  PRIMARY KEY (project_id, organization_id, domain),
  CONSTRAINT fk_project_organization_domains_project_organizations_project_id_organization_id FOREIGN KEY (project_id, organization_id) REFERENCES project_organizations (project_id, id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE project_organization_invitations (
  project_id TEXT NOT NULL,
  organization_id TEXT NOT NULL,
  id TEXT NOT NULL,
  email TEXT NOT NULL,
  role TEXT NOT NULL,
  token_hash TEXT NOT NULL,
  status TEXT NOT NULL,
  created_at TEXT NOT NULL,
  expires_at TEXT NOT NULL,
  version TEXT NOT NULL,
  PRIMARY KEY (project_id, organization_id, id),
  CONSTRAINT fk_project_organization_invitations_project_organizations_project_id_organization_id FOREIGN KEY (project_id, organization_id) REFERENCES project_organizations (project_id, id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE project_organization_memberships (
  project_id TEXT NOT NULL,
  organization_id TEXT NOT NULL,
  user_id TEXT NOT NULL,
  role TEXT NOT NULL,
  created_at TEXT NOT NULL,
  version TEXT NOT NULL,
  PRIMARY KEY (project_id, organization_id, user_id),
  CONSTRAINT ck_project_organization_memberships_role CHECK (role IN ('admin', 'member')),
  CONSTRAINT fk_project_organization_memberships_project_organizations_project_id_organization_id FOREIGN KEY (project_id, organization_id) REFERENCES project_organizations (project_id, id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
  CONSTRAINT fk_project_organization_memberships_users_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE webhook_endpoint_states (
  endpoint_id TEXT NOT NULL PRIMARY KEY,
  project_id TEXT NOT NULL,
  first_failure_at TEXT NULL,
  disabled_at TEXT NULL,
  lease_until TEXT NOT NULL,
  last_served_at TEXT NOT NULL,
  version TEXT NOT NULL,
  CONSTRAINT fk_webhook_endpoint_states_webhook_endpoints_endpoint_id FOREIGN KEY (endpoint_id) REFERENCES webhook_endpoints (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE webhook_deliveries (
  id TEXT NOT NULL PRIMARY KEY,
  project_id TEXT NOT NULL,
  endpoint_id TEXT NOT NULL,
  event_id TEXT NOT NULL,
  status TEXT NOT NULL,
  attempts INTEGER NOT NULL,
  next_attempt_at TEXT NOT NULL,
  started_at TEXT NOT NULL,
  cycle_attempts INTEGER NOT NULL,
  version TEXT NOT NULL,
  CONSTRAINT fk_webhook_deliveries_webhook_endpoints_endpoint_id FOREIGN KEY (endpoint_id) REFERENCES webhook_endpoints (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
  CONSTRAINT fk_webhook_deliveries_webhook_outbox_event_id FOREIGN KEY (event_id) REFERENCES webhook_outbox (event_id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE TABLE webhook_attempts (
  id TEXT NOT NULL PRIMARY KEY,
  delivery_id TEXT NOT NULL,
  project_id TEXT NOT NULL,
  created_at TEXT NOT NULL,
  status TEXT NOT NULL,
  latency_ms INTEGER NOT NULL,
  response_code INTEGER NULL,
  response_body TEXT NULL,
  version TEXT NOT NULL,
  CONSTRAINT fk_webhook_attempts_webhook_deliveries_delivery_id FOREIGN KEY (delivery_id) REFERENCES webhook_deliveries (id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);

CREATE INDEX ix_account_locks_last_attempt ON account_locks (last_attempt);

CREATE INDEX ix_account_locks_user_id ON account_locks (user_id);

CREATE INDEX ix_agent_assertions_keep_until ON agent_assertions (keep_until);

CREATE INDEX ix_agent_enrollments_expires_at ON agent_enrollments (expires_at);

CREATE INDEX ix_agent_enrollments_project_id_agent_id ON agent_enrollments (project_id, agent_id);

CREATE UNIQUE INDEX ix_agent_enrollments_project_id_user_code_hash ON agent_enrollments (project_id, user_code_hash);

CREATE INDEX ix_agent_enrollments_user_id ON agent_enrollments (user_id);

CREATE INDEX ix_agent_keys_project_id_agent_id ON agent_keys (project_id, agent_id);

CREATE UNIQUE INDEX ix_agent_keys_project_id_thumbprint ON agent_keys (project_id, thumbprint);

CREATE INDEX ix_agentid_clients_owner_user_id ON agentid_clients (owner_user_id);

CREATE INDEX ix_agentid_transactions_expires_at ON agentid_transactions (expires_at);

CREATE INDEX ix_agentid_transactions_owner_user_id ON agentid_transactions (owner_user_id);

CREATE UNIQUE INDEX ix_agentid_transactions_request_hash ON agentid_transactions (request_hash);

CREATE INDEX ix_agents_owner_user_id ON agents (owner_user_id);

CREATE INDEX ix_agents_project_id_owner_user_id ON agents (project_id, owner_user_id);

CREATE INDEX ix_api_keys_organization_id_project_id_id ON api_keys (organization_id, project_id, id);

CREATE INDEX ix_api_keys_parent_key_id ON api_keys (parent_key_id);

CREATE INDEX ix_api_keys_project_id ON api_keys (project_id);

CREATE UNIQUE INDEX ix_api_keys_secret_hash ON api_keys (secret_hash);

CREATE INDEX ix_audit_events_organization_id_id ON audit_events (organization_id, id);

CREATE INDEX ix_challenges_expires_at ON challenges (expires_at);

CREATE INDEX ix_closure_jobs_account_id ON closure_jobs (account_id);

CREATE INDEX ix_closure_jobs_next_attempt_at ON closure_jobs (next_attempt_at);

CREATE INDEX ix_credential_claims_instance_id_owner_id ON credential_claims (instance_id, owner_id);

CREATE INDEX ix_deletion_jobs_next_attempt_at ON deletion_jobs (next_attempt_at);

CREATE INDEX ix_email_changes_expires_at ON email_changes (expires_at);

CREATE INDEX ix_email_deliveries_kind_status_lease_until ON email_deliveries (kind, status, lease_until);

CREATE INDEX ix_email_reservations_at ON email_reservations (at);

CREATE UNIQUE INDEX ix_email_reservations_attempt_id ON email_reservations (attempt_id);

CREATE INDEX ix_impersonations_actor_id ON impersonations (actor_id);

CREATE UNIQUE INDEX ix_impersonations_cookie_hash ON impersonations (cookie_hash);

CREATE INDEX ix_impersonations_instance_id_expires_at ON impersonations (instance_id, expires_at);

CREATE UNIQUE INDEX ix_impersonations_ticket_hash ON impersonations (ticket_hash);

CREATE INDEX ix_impersonations_user_id ON impersonations (user_id);

CREATE INDEX ix_invitations_organization_id_project_id_id ON invitations (organization_id, project_id, id);

CREATE UNIQUE INDEX ix_invitations_organization_id_target_email ON invitations (organization_id, target, email) WHERE status = 'pending';

CREATE INDEX ix_invitations_project_id ON invitations (project_id);

CREATE UNIQUE INDEX ix_invitations_token_hash ON invitations (token_hash);

CREATE INDEX ix_known_devices_last_seen ON known_devices (last_seen);

CREATE INDEX ix_known_devices_user_id ON known_devices (user_id);

CREATE INDEX ix_mail_outbox_created_at ON mail_outbox (created_at);

CREATE INDEX ix_mail_outbox_state_lease_until ON mail_outbox (state, lease_until);

CREATE INDEX ix_memberships_user_id ON memberships (user_id);

CREATE INDEX ix_oidc_authorizations_creation_date ON oidc_authorizations (creation_date);

CREATE INDEX ix_oidc_authorizations_instance_id_application_id ON oidc_authorizations (instance_id, application_id);

CREATE INDEX ix_oidc_authorizations_instance_id_subject ON oidc_authorizations (instance_id, subject);

CREATE INDEX ix_oidc_tokens_authorization_id ON oidc_tokens (authorization_id);

CREATE INDEX ix_oidc_tokens_expiration_date ON oidc_tokens (expiration_date);

CREATE INDEX ix_oidc_tokens_instance_id_application_id ON oidc_tokens (instance_id, application_id);

CREATE UNIQUE INDEX ix_oidc_tokens_instance_id_reference_id ON oidc_tokens (instance_id, reference_id);

CREATE INDEX ix_oidc_tokens_instance_id_subject ON oidc_tokens (instance_id, subject);

CREATE INDEX ix_passcodes_instance_id_user_id ON passcodes (instance_id, user_id);

CREATE INDEX ix_passcodes_window_started_at ON passcodes (window_started_at);

CREATE INDEX ix_passkeys_user_id ON passkeys (user_id);

CREATE INDEX ix_project_invitations_project_id_email_status ON project_invitations (project_id, email, status);

CREATE INDEX ix_project_invitations_project_id_id ON project_invitations (project_id, id);

CREATE UNIQUE INDEX ix_project_invitations_token_hash ON project_invitations (token_hash);

CREATE INDEX ix_project_members_organization_id ON project_members (organization_id);

CREATE INDEX ix_project_members_user_id_organization_id ON project_members (user_id, organization_id);

CREATE INDEX ix_project_organization_dismissals_user_id ON project_organization_dismissals (user_id);

CREATE INDEX ix_project_organization_domains_lease_until_checked_at ON project_organization_domains (lease_until, checked_at) WHERE verified_at IS NOT NULL;

CREATE UNIQUE INDEX ix_project_organization_domains_project_id_domain ON project_organization_domains (project_id, domain) WHERE verified_at IS NOT NULL;

CREATE UNIQUE INDEX ix_project_organization_invitations_token_hash ON project_organization_invitations (token_hash);

CREATE INDEX ix_project_organization_memberships_project_id_user_id ON project_organization_memberships (project_id, user_id);

CREATE INDEX ix_project_organization_memberships_user_id ON project_organization_memberships (user_id);

CREATE UNIQUE INDEX ix_project_organizations_project_id_slug ON project_organizations (project_id, slug);

CREATE UNIQUE INDEX ix_project_waitlist_project_id_email ON project_waitlist (project_id, email);

CREATE INDEX ix_project_waitlist_project_id_id ON project_waitlist (project_id, id);

CREATE UNIQUE INDEX ix_projects_closure_id ON projects (closure_id);

CREATE UNIQUE INDEX ix_projects_organization_id_name_key ON projects (organization_id, name_key);

CREATE INDEX ix_provider_links_user_id ON provider_links (user_id);

CREATE INDEX ix_sessions_expires_at ON sessions (expires_at);

CREATE INDEX ix_sessions_instance_id_id ON sessions (instance_id, id);

CREATE INDEX ix_sessions_revoked_expires_at ON sessions (expires_at) WHERE revoked;

CREATE UNIQUE INDEX ix_sessions_token_hash ON sessions (token_hash);

CREATE INDEX ix_sessions_user_id ON sessions (user_id);

CREATE UNIQUE INDEX ix_sso_connections_organization_id ON sso_connections (organization_id);

CREATE INDEX ix_sso_identity_links_organization_id_user_id ON sso_identity_links (organization_id, user_id);

CREATE INDEX ix_step_up_deletion_grants_expires_at ON step_up_deletion_grants (expires_at);

CREATE INDEX ix_step_up_deletion_grants_user_id ON step_up_deletion_grants (user_id);

CREATE INDEX ix_step_up_proofs_expires_at ON step_up_proofs (expires_at);

CREATE INDEX ix_step_up_proofs_user_id ON step_up_proofs (user_id);

CREATE UNIQUE INDEX ix_user_api_keys_project_id_prefix ON user_api_keys (project_id, prefix);

CREATE INDEX ix_user_api_keys_project_id_user_id ON user_api_keys (project_id, user_id);

CREATE INDEX ix_user_api_keys_user_id ON user_api_keys (user_id);

CREATE UNIQUE INDEX ix_users_instance_id_email ON users (instance_id, email);

CREATE UNIQUE INDEX ix_users_instance_id_external_id ON users (instance_id, external_id);

CREATE UNIQUE INDEX ix_users_instance_id_username ON users (instance_id, username);

CREATE INDEX ix_webhook_attempts_delivery_id_id ON webhook_attempts (delivery_id, id DESC);

CREATE INDEX ix_webhook_deliveries_endpoint_id_status_next_attempt_at ON webhook_deliveries (endpoint_id, status, next_attempt_at);

CREATE UNIQUE INDEX ix_webhook_deliveries_event_id_endpoint_id ON webhook_deliveries (event_id, endpoint_id);

CREATE INDEX ix_webhook_endpoint_states_disabled_at_lease_until ON webhook_endpoint_states (disabled_at, lease_until);

CREATE INDEX ix_webhook_endpoint_states_project_id_last_served_at ON webhook_endpoint_states (project_id, last_served_at DESC);

CREATE INDEX ix_webhook_endpoints_project_id_id ON webhook_endpoints (project_id, id);

CREATE INDEX ix_webhook_outbox_occurred_at ON webhook_outbox (occurred_at);

CREATE INDEX ix_webhook_outbox_project_id_event_id ON webhook_outbox (project_id, event_id);

CREATE INDEX ix_webhook_outbox_status_next_attempt_at ON webhook_outbox (status, next_attempt_at);

CREATE TABLE write_guards (
  id INTEGER NOT NULL PRIMARY KEY,
  expected INTEGER NOT NULL
);
