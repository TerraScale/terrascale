using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TerraScale.Authentication.Storage.Migrations
{
    public partial class InitialSchema : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "account_deletion_receipts",
                columns: table => new
                {
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    token_hash = table.Column<string>(type: "TEXT", nullable: false),
                    subject_id = table.Column<string>(type: "TEXT", nullable: false),
                    generation = table.Column<string>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    organization_ids = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_deletion_receipts", x => new { x.instance_id, x.token_hash });
                });

            migrationBuilder.CreateTable(
                name: "challenges",
                columns: table => new
                {
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    purpose = table.Column<string>(type: "TEXT", nullable: false),
                    token_hash = table.Column<string>(type: "TEXT", nullable: false),
                    subject_id = table.Column<string>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    consumed = table.Column<bool>(type: "INTEGER", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_challenges", x => new { x.instance_id, x.purpose, x.token_hash });
                    table.CheckConstraint("ck_challenges_token_hash", "length(token_hash) = 64");
                });

            migrationBuilder.CreateTable(
                name: "closure_cancellations",
                columns: table => new
                {
                    generation = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_closure_cancellations", x => x.generation);
                });

            migrationBuilder.CreateTable(
                name: "closure_commits",
                columns: table => new
                {
                    generation = table.Column<string>(type: "TEXT", nullable: false),
                    committed_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_closure_commits", x => x.generation);
                });

            migrationBuilder.CreateTable(
                name: "closure_jobs",
                columns: table => new
                {
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    scope = table.Column<string>(type: "TEXT", nullable: false),
                    subject_id = table.Column<string>(type: "TEXT", nullable: false),
                    id = table.Column<string>(type: "TEXT", nullable: true),
                    organization_id = table.Column<string>(type: "TEXT", nullable: true),
                    requested_by = table.Column<string>(type: "TEXT", nullable: false),
                    requested_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    next_attempt_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    account_id = table.Column<string>(type: "TEXT", nullable: true),
                    attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    failure_code = table.Column<string>(type: "TEXT", nullable: true),
                    fence_seen = table.Column<bool>(type: "INTEGER", nullable: false),
                    generation = table.Column<string>(type: "TEXT", nullable: true),
                    committed_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    project_closure_version = table.Column<int>(type: "INTEGER", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_closure_jobs", x => new { x.instance_id, x.scope, x.subject_id });
                    table.CheckConstraint("ck_closure_jobs_scope", "scope IN ('project', 'organization', 'account')");
                    table.CheckConstraint("ck_closure_jobs_version", "project_closure_version IN (1, 2)");
                });

            migrationBuilder.CreateTable(
                name: "credential_claims",
                columns: table => new
                {
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    key = table.Column<string>(type: "TEXT", nullable: false),
                    owner_id = table.Column<string>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_credential_claims", x => new { x.instance_id, x.kind, x.key });
                });

            migrationBuilder.CreateTable(
                name: "deletion_cursors",
                columns: table => new
                {
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    after = table.Column<string>(type: "TEXT", nullable: true),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deletion_cursors", x => x.name);
                });

            migrationBuilder.CreateTable(
                name: "deletion_jobs",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    scope = table.Column<string>(type: "TEXT", nullable: false),
                    subject_id = table.Column<string>(type: "TEXT", nullable: false),
                    organization_id = table.Column<string>(type: "TEXT", nullable: true),
                    requested_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    due_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    next_attempt_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    failure_code = table.Column<string>(type: "TEXT", nullable: true),
                    auth_logical_verified_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    fence_expires_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    history_status = table.Column<string>(type: "TEXT", nullable: true),
                    history_completed_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    erasure_id = table.Column<string>(type: "TEXT", nullable: true),
                    closure_generation = table.Column<string>(type: "TEXT", nullable: true),
                    dashboard_verified_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deletion_jobs", x => x.id);
                    table.CheckConstraint("ck_deletion_jobs_scope", "scope IN ('project', 'organization', 'account')");
                });

            migrationBuilder.CreateTable(
                name: "email_budgets",
                columns: table => new
                {
                    key = table.Column<string>(type: "TEXT", nullable: false),
                    paused_until = table.Column<DateTime>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_budgets", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "email_configs",
                columns: table => new
                {
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    version = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    actor = table.Column<string>(type: "TEXT", nullable: true),
                    change = table.Column<string>(type: "TEXT", nullable: true),
                    branding = table.Column<string>(type: "TEXT", nullable: false),
                    templates = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_configs", x => new { x.instance_id, x.version });
                    table.CheckConstraint("ck_email_configs_version", "version > 0");
                });

            migrationBuilder.CreateTable(
                name: "email_deliveries",
                columns: table => new
                {
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    key = table.Column<string>(type: "TEXT", nullable: false),
                    recipient = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_deliveries", x => new { x.kind, x.key, x.recipient });
                    table.CheckConstraint("ck_email_deliveries_status", "status IN ('pending', 'sending', 'accepted', 'rejected', 'unknown')");
                });

            migrationBuilder.CreateTable(
                name: "email_reservations",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    purpose = table.Column<int>(type: "INTEGER", nullable: false),
                    tenant = table.Column<string>(type: "TEXT", nullable: false),
                    attempt_id = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_reservations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mail_outbox",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    locale = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    lease_until = table.Column<DateTime>(type: "TEXT", nullable: false),
                    payload = table.Column<string>(type: "TEXT", nullable: true),
                    attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    tenant = table.Column<string>(type: "TEXT", nullable: true),
                    purpose = table.Column<int>(type: "INTEGER", nullable: true),
                    code_key = table.Column<string>(type: "TEXT", nullable: true),
                    issue_id = table.Column<string>(type: "TEXT", nullable: true),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mail_outbox", x => x.id);
                    table.CheckConstraint("ck_mail_outbox_state", "state IN ('queued', 'ready', 'sending', 'sent', 'rejected', 'failed', 'suppressed', 'unknown')");
                });

            migrationBuilder.CreateTable(
                name: "oidc_applications",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    application_type = table.Column<string>(type: "TEXT", nullable: true),
                    client_id = table.Column<string>(type: "TEXT", nullable: false),
                    client_secret = table.Column<string>(type: "TEXT", nullable: true),
                    secret_bindings = table.Column<string>(type: "TEXT", nullable: false),
                    previous_secret_hash = table.Column<string>(type: "TEXT", nullable: true),
                    previous_secret_expires_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    client_type = table.Column<string>(type: "TEXT", nullable: true),
                    consent_type = table.Column<string>(type: "TEXT", nullable: true),
                    display_name = table.Column<string>(type: "TEXT", nullable: true),
                    permissions = table.Column<string>(type: "TEXT", nullable: false),
                    post_logout_redirect_uris = table.Column<string>(type: "TEXT", nullable: false),
                    properties = table.Column<string>(type: "TEXT", nullable: false),
                    redirect_uris = table.Column<string>(type: "TEXT", nullable: false),
                    requirements = table.Column<string>(type: "TEXT", nullable: false),
                    settings = table.Column<string>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oidc_applications", x => new { x.instance_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "oidc_authorizations",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    application_id = table.Column<string>(type: "TEXT", nullable: true),
                    creation_date = table.Column<DateTime>(type: "TEXT", nullable: true),
                    properties = table.Column<string>(type: "TEXT", nullable: false),
                    scopes = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: true),
                    subject = table.Column<string>(type: "TEXT", nullable: true),
                    type = table.Column<string>(type: "TEXT", nullable: true),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oidc_authorizations", x => x.id);
                    table.CheckConstraint("ck_oidc_authorizations_status", "status IN ('valid', 'revoked')");
                });

            migrationBuilder.CreateTable(
                name: "organizations",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    created_by = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    owners = table.Column<int>(type: "INTEGER", nullable: false),
                    projects = table.Column<int>(type: "INTEGER", nullable: false),
                    transfer = table.Column<string>(type: "TEXT", nullable: true),
                    require_mfa = table.Column<bool>(type: "INTEGER", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organizations", x => x.id);
                    table.CheckConstraint("ck_organizations_counts", "owners >= 0 AND projects >= 0");
                });

            migrationBuilder.CreateTable(
                name: "passcodes",
                columns: table => new
                {
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    key = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    email = table.Column<string>(type: "TEXT", nullable: false),
                    code_hash = table.Column<string>(type: "TEXT", nullable: false),
                    auth_epoch = table.Column<long>(type: "INTEGER", nullable: false),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    failed_attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    consumed = table.Column<bool>(type: "INTEGER", nullable: false),
                    window_started_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    issued_in_window = table.Column<int>(type: "INTEGER", nullable: false),
                    purpose = table.Column<string>(type: "TEXT", nullable: true),
                    password_hash = table.Column<string>(type: "TEXT", nullable: true),
                    pending_token_hash = table.Column<string>(type: "TEXT", nullable: true),
                    auth_stamp = table.Column<string>(type: "TEXT", nullable: true),
                    issue_id = table.Column<string>(type: "TEXT", nullable: false),
                    issuance_bindings = table.Column<string>(type: "TEXT", nullable: false),
                    resets_authentication = table.Column<bool>(type: "INTEGER", nullable: false),
                    pruned = table.Column<bool>(type: "INTEGER", nullable: false),
                    step_up_request_id = table.Column<string>(type: "TEXT", nullable: true),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_passcodes", x => new { x.instance_id, x.key });
                    table.CheckConstraint("ck_passcodes_failed_attempts", "failed_attempts >= 0");
                    table.CheckConstraint("ck_passcodes_purpose", "purpose IS NULL OR purpose IN ('password-reset', 'sign-up', 'step-up')");
                });

            migrationBuilder.CreateTable(
                name: "rate_limits",
                columns: table => new
                {
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    key = table.Column<string>(type: "TEXT", nullable: false),
                    window = table.Column<long>(type: "INTEGER", nullable: false),
                    used = table.Column<int>(type: "INTEGER", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rate_limits", x => new { x.instance_id, x.key });
                    table.CheckConstraint("ck_rate_limits_counts", "window >= 0 AND used >= 0");
                });

            migrationBuilder.CreateTable(
                name: "signing_keys",
                columns: table => new
                {
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    master_key_id = table.Column<string>(type: "TEXT", nullable: false),
                    algorithm = table.Column<string>(type: "TEXT", nullable: false),
                    secret_box = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    activates_at = table.Column<long>(type: "INTEGER", nullable: false),
                    x = table.Column<string>(type: "TEXT", nullable: false),
                    y = table.Column<string>(type: "TEXT", nullable: false),
                    public_metadata_mac = table.Column<string>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_signing_keys", x => new { x.instance_id, x.id });
                    table.CheckConstraint("ck_signing_keys_algorithm", "algorithm = 'ES256'");
                });

            migrationBuilder.CreateTable(
                name: "storage_probes",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    checked_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_storage_probes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "support_deliveries",
                columns: table => new
                {
                    request_id = table.Column<string>(type: "TEXT", nullable: false),
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    acknowledgement_status = table.Column<string>(type: "TEXT", nullable: true),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_support_deliveries", x => x.request_id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false),
                    instance_id = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false),
                    email = table.Column<string>(type: "TEXT", maxLength: 254, nullable: false),
                    auth_epoch = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    password_hash = table.Column<string>(type: "TEXT", nullable: true),
                    platform_role = table.Column<string>(type: "TEXT", nullable: true),
                    auth_stamp = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false),
                    disabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                    table.CheckConstraint("ck_users_auth_epoch", "auth_epoch >= 0");
                    table.CheckConstraint("ck_users_email", "length(email) BETWEEN 3 AND 254 AND email = lower(email)");
                    table.CheckConstraint("ck_users_platform_role", "platform_role IS NULL OR platform_role = 'admin'");
                });

            migrationBuilder.CreateTable(
                name: "oidc_tokens",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    bindings = table.Column<string>(type: "TEXT", nullable: false),
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    application_id = table.Column<string>(type: "TEXT", nullable: true),
                    authorization_id = table.Column<string>(type: "TEXT", nullable: true),
                    creation_date = table.Column<DateTime>(type: "TEXT", nullable: true),
                    expiration_date = table.Column<DateTime>(type: "TEXT", nullable: true),
                    payload = table.Column<string>(type: "TEXT", nullable: true),
                    properties = table.Column<string>(type: "TEXT", nullable: false),
                    redemption_date = table.Column<DateTime>(type: "TEXT", nullable: true),
                    reference_id = table.Column<string>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", nullable: true),
                    subject = table.Column<string>(type: "TEXT", nullable: true),
                    type = table.Column<string>(type: "TEXT", nullable: true),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oidc_tokens", x => x.id);
                    table.ForeignKey(
                        name: "FK_oidc_tokens_oidc_authorizations_authorization_id",
                        column: x => x.authorization_id,
                        principalTable: "oidc_authorizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    actor = table.Column<string>(type: "TEXT", nullable: false),
                    action = table.Column<string>(type: "TEXT", nullable: false),
                    target = table.Column<string>(type: "TEXT", nullable: true),
                    detail = table.Column<string>(type: "TEXT", nullable: true),
                    occurred_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_audit_events_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    created_by = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    closure_id = table.Column<string>(type: "TEXT", nullable: true),
                    allowed_cidrs = table.Column<string>(type: "TEXT", nullable: true),
                    name_key = table.Column<string>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projects", x => x.id);
                    table.ForeignKey(
                        name: "FK_projects_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sso_connections",
                columns: table => new
                {
                    domain = table.Column<string>(type: "TEXT", nullable: false),
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    protocol = table.Column<string>(type: "TEXT", nullable: false),
                    default_role = table.Column<string>(type: "TEXT", nullable: false),
                    verification_token = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    verified_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    issuer = table.Column<string>(type: "TEXT", nullable: true),
                    client_id = table.Column<string>(type: "TEXT", nullable: true),
                    client_secret_box = table.Column<string>(type: "TEXT", nullable: true),
                    idp_entity_id = table.Column<string>(type: "TEXT", nullable: true),
                    sso_url = table.Column<string>(type: "TEXT", nullable: true),
                    certificate = table.Column<string>(type: "TEXT", nullable: true),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sso_connections", x => x.domain);
                    table.CheckConstraint("ck_sso_connections_protocol", "protocol IN ('oidc', 'saml')");
                    table.ForeignKey(
                        name: "FK_sso_connections_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "email_changes",
                columns: table => new
                {
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    auth_epoch = table.Column<long>(type: "INTEGER", nullable: false),
                    auth_stamp = table.Column<string>(type: "TEXT", nullable: true),
                    new_email = table.Column<string>(type: "TEXT", nullable: false),
                    code_hash = table.Column<string>(type: "TEXT", nullable: false),
                    cancel_hash = table.Column<string>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    failed_attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    bindings = table.Column<string>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_changes", x => x.user_id);
                    table.CheckConstraint("ck_email_changes_failed_attempts", "failed_attempts >= 0");
                    table.ForeignKey(
                        name: "FK_email_changes_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "memberships",
                columns: table => new
                {
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    role = table.Column<string>(type: "TEXT", nullable: true),
                    joined_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memberships", x => new { x.organization_id, x.user_id });
                    table.CheckConstraint("ck_memberships_role", "role IS NULL OR role IN ('viewer', 'member', 'admin', 'owner')");
                    table.ForeignKey(
                        name: "FK_memberships_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_memberships_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mfa_credentials",
                columns: table => new
                {
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    secret_box = table.Column<string>(type: "TEXT", nullable: true),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    enrollment_epoch = table.Column<long>(type: "INTEGER", nullable: false),
                    enrollment_expires_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    last_accepted_step = table.Column<long>(type: "INTEGER", nullable: false),
                    recovery_hashes = table.Column<string>(type: "TEXT", nullable: false),
                    recovery_expires_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mfa_credentials", x => x.user_id);
                    table.CheckConstraint("ck_mfa_credentials_steps", "enrollment_epoch >= 0 AND last_accepted_step >= -1");
                    table.ForeignKey(
                        name: "FK_mfa_credentials_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "passkeys",
                columns: table => new
                {
                    credential_id = table.Column<string>(type: "TEXT", maxLength: 1400, nullable: false),
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    public_key_cose = table.Column<byte[]>(type: "BLOB", nullable: false),
                    sign_count = table.Column<uint>(type: "INTEGER", nullable: false),
                    transports = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: true),
                    aaguid = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    last_used_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_passkeys", x => new { x.instance_id, x.credential_id });
                    table.ForeignKey(
                        name: "FK_passkeys_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "provider_links",
                columns: table => new
                {
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    provider = table.Column<string>(type: "TEXT", nullable: false),
                    issuer = table.Column<string>(type: "TEXT", nullable: false),
                    client_id = table.Column<string>(type: "TEXT", nullable: false),
                    subject = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_links", x => new { x.instance_id, x.provider, x.issuer, x.client_id, x.subject });
                    table.ForeignKey(
                        name: "FK_provider_links_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    token_hash = table.Column<string>(type: "TEXT", nullable: false),
                    auth_epoch = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    revoked = table.Column<bool>(type: "INTEGER", nullable: false),
                    auth_stamp = table.Column<string>(type: "TEXT", nullable: true),
                    bindings = table.Column<string>(type: "TEXT", nullable: false),
                    mfa = table.Column<bool>(type: "INTEGER", nullable: false),
                    mfa_method = table.Column<string>(type: "TEXT", nullable: true),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sessions", x => x.id);
                    table.CheckConstraint("ck_sessions_token_hash", "length(token_hash) = 64");
                    table.ForeignKey(
                        name: "FK_sessions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "step_up_deletion_grants",
                columns: table => new
                {
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    receipt_hash = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    auth_epoch = table.Column<long>(type: "INTEGER", nullable: false),
                    auth_stamp = table.Column<string>(type: "TEXT", nullable: true),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    authorization_hash = table.Column<string>(type: "TEXT", nullable: false),
                    bindings = table.Column<string>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_step_up_deletion_grants", x => new { x.instance_id, x.receipt_hash });
                    table.ForeignKey(
                        name: "FK_step_up_deletion_grants_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "step_up_proofs",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    auth_epoch = table.Column<long>(type: "INTEGER", nullable: false),
                    auth_stamp = table.Column<string>(type: "TEXT", nullable: true),
                    token_hash = table.Column<string>(type: "TEXT", nullable: false),
                    purpose = table.Column<string>(type: "TEXT", nullable: false),
                    audience = table.Column<string>(type: "TEXT", nullable: false),
                    return_uri = table.Column<string>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    primary_verified = table.Column<bool>(type: "INTEGER", nullable: false),
                    approved = table.Column<bool>(type: "INTEGER", nullable: false),
                    consumed = table.Column<bool>(type: "INTEGER", nullable: false),
                    mail_requested = table.Column<bool>(type: "INTEGER", nullable: false),
                    bindings = table.Column<string>(type: "TEXT", nullable: true),
                    organization_id = table.Column<string>(type: "TEXT", nullable: true),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_step_up_proofs", x => x.id);
                    table.ForeignKey(
                        name: "FK_step_up_proofs_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "api_keys",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    project_id = table.Column<string>(type: "TEXT", nullable: true),
                    database = table.Column<string>(type: "TEXT", nullable: true),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    scopes = table.Column<string>(type: "TEXT", nullable: false),
                    secret_hash = table.Column<string>(type: "TEXT", nullable: false),
                    previous_secret_hash = table.Column<string>(type: "TEXT", nullable: true),
                    previous_secret_expires_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    created_by = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    allowed_cidrs = table.Column<string>(type: "TEXT", nullable: true),
                    secret_bindings = table.Column<string>(type: "TEXT", nullable: false),
                    previous_secret_bindings = table.Column<string>(type: "TEXT", nullable: false),
                    parent_key_id = table.Column<string>(type: "TEXT", nullable: true),
                    parent_generation = table.Column<string>(type: "TEXT", nullable: true),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_keys", x => x.id);
                    table.ForeignKey(
                        name: "FK_api_keys_api_keys_parent_key_id",
                        column: x => x.parent_key_id,
                        principalTable: "api_keys",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_api_keys_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_api_keys_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "invitations",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    project_id = table.Column<string>(type: "TEXT", nullable: true),
                    email = table.Column<string>(type: "TEXT", nullable: false),
                    role = table.Column<string>(type: "TEXT", nullable: false),
                    invited_by = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    token_hash = table.Column<string>(type: "TEXT", nullable: false),
                    decided_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    decided_by = table.Column<string>(type: "TEXT", nullable: true),
                    target = table.Column<string>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invitations", x => x.id);
                    table.CheckConstraint("ck_invitations_status", "status IN ('pending', 'accepted', 'declined', 'revoked', 'expired')");
                    table.ForeignKey(
                        name: "FK_invitations_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_invitations_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_auth_settings",
                columns: table => new
                {
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    passcode = table.Column<bool>(type: "INTEGER", nullable: false),
                    password = table.Column<bool>(type: "INTEGER", nullable: false),
                    sign_up = table.Column<bool>(type: "INTEGER", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: true),
                    logo_url = table.Column<string>(type: "TEXT", nullable: true),
                    theme_color = table.Column<string>(type: "TEXT", nullable: true),
                    providers = table.Column<string>(type: "TEXT", nullable: true),
                    title_size = table.Column<int>(type: "INTEGER", nullable: true),
                    default_theme = table.Column<string>(type: "TEXT", nullable: true),
                    footer_links = table.Column<string>(type: "TEXT", nullable: true),
                    background_color = table.Column<string>(type: "TEXT", nullable: true),
                    text_color = table.Column<string>(type: "TEXT", nullable: true),
                    configuration = table.Column<string>(type: "TEXT", nullable: true),
                    email_message = table.Column<string>(type: "TEXT", nullable: true),
                    subscribed_webhook_events = table.Column<string>(type: "TEXT", nullable: true),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_auth_settings", x => x.instance_id);
                    table.CheckConstraint("ck_project_auth_settings_title_size", "title_size IS NULL OR title_size BETWEEN 20 AND 40");
                    table.ForeignKey(
                        name: "FK_project_auth_settings_projects_instance_id",
                        column: x => x.instance_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_members",
                columns: table => new
                {
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    role = table.Column<string>(type: "TEXT", nullable: false),
                    granted_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_members", x => new { x.project_id, x.user_id });
                    table.CheckConstraint("ck_project_members_role", "role IN ('viewer', 'member', 'admin')");
                    table.ForeignKey(
                        name: "FK_project_members_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_project_members_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_project_members_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "webhook_endpoints",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    url = table.Column<string>(type: "TEXT", nullable: false),
                    event_types = table.Column<string>(type: "TEXT", nullable: false),
                    secret_box = table.Column<string>(type: "TEXT", nullable: false),
                    previous_secret_box = table.Column<string>(type: "TEXT", nullable: true),
                    previous_secret_expires_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_endpoints", x => x.id);
                    table.ForeignKey(
                        name: "FK_webhook_endpoints_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "webhook_outbox",
                columns: table => new
                {
                    event_id = table.Column<string>(type: "TEXT", nullable: false),
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    event_type = table.Column<string>(type: "TEXT", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    email = table.Column<string>(type: "TEXT", nullable: true),
                    provider = table.Column<string>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    next_attempt_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_outbox", x => x.event_id);
                    table.CheckConstraint("ck_webhook_outbox_attempts", "attempts >= 0");
                    table.ForeignKey(
                        name: "FK_webhook_outbox_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "api_key_usage",
                columns: table => new
                {
                    key_id = table.Column<string>(type: "TEXT", nullable: false),
                    instance_id = table.Column<string>(type: "TEXT", nullable: false),
                    organization_id = table.Column<string>(type: "TEXT", nullable: false),
                    project_id = table.Column<string>(type: "TEXT", nullable: true),
                    last_used_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    version = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_key_usage", x => x.key_id);
                    table.ForeignKey(
                        name: "FK_api_key_usage_api_keys_key_id",
                        column: x => x.key_id,
                        principalTable: "api_keys",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_organization_id_project_id_id",
                table: "api_keys",
                columns: new[] { "organization_id", "project_id", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_parent_key_id",
                table: "api_keys",
                column: "parent_key_id");

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_project_id",
                table: "api_keys",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_secret_hash",
                table: "api_keys",
                column: "secret_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_organization_id_id",
                table: "audit_events",
                columns: new[] { "organization_id", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_challenges_expires_at",
                table: "challenges",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_closure_jobs_account_id",
                table: "closure_jobs",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "IX_closure_jobs_next_attempt_at",
                table: "closure_jobs",
                column: "next_attempt_at");

            migrationBuilder.CreateIndex(
                name: "IX_credential_claims_instance_id_owner_id",
                table: "credential_claims",
                columns: new[] { "instance_id", "owner_id" });

            migrationBuilder.CreateIndex(
                name: "IX_deletion_jobs_next_attempt_at",
                table: "deletion_jobs",
                column: "next_attempt_at");

            migrationBuilder.CreateIndex(
                name: "IX_email_changes_expires_at",
                table: "email_changes",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_email_reservations_at",
                table: "email_reservations",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "IX_email_reservations_attempt_id",
                table: "email_reservations",
                column: "attempt_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_invitations_organization_id_project_id_id",
                table: "invitations",
                columns: new[] { "organization_id", "project_id", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_invitations_organization_id_target_email",
                table: "invitations",
                columns: new[] { "organization_id", "target", "email" },
                unique: true,
                filter: "status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "IX_invitations_project_id",
                table: "invitations",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_invitations_token_hash",
                table: "invitations",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mail_outbox_created_at",
                table: "mail_outbox",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_mail_outbox_state_lease_until",
                table: "mail_outbox",
                columns: new[] { "state", "lease_until" });

            migrationBuilder.CreateIndex(
                name: "IX_memberships_user_id",
                table: "memberships",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_oidc_authorizations_creation_date",
                table: "oidc_authorizations",
                column: "creation_date");

            migrationBuilder.CreateIndex(
                name: "IX_oidc_authorizations_instance_id_application_id",
                table: "oidc_authorizations",
                columns: new[] { "instance_id", "application_id" });

            migrationBuilder.CreateIndex(
                name: "IX_oidc_authorizations_instance_id_subject",
                table: "oidc_authorizations",
                columns: new[] { "instance_id", "subject" });

            migrationBuilder.CreateIndex(
                name: "IX_oidc_tokens_authorization_id",
                table: "oidc_tokens",
                column: "authorization_id");

            migrationBuilder.CreateIndex(
                name: "IX_oidc_tokens_expiration_date",
                table: "oidc_tokens",
                column: "expiration_date");

            migrationBuilder.CreateIndex(
                name: "IX_oidc_tokens_instance_id_application_id",
                table: "oidc_tokens",
                columns: new[] { "instance_id", "application_id" });

            migrationBuilder.CreateIndex(
                name: "IX_oidc_tokens_instance_id_reference_id",
                table: "oidc_tokens",
                columns: new[] { "instance_id", "reference_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oidc_tokens_instance_id_subject",
                table: "oidc_tokens",
                columns: new[] { "instance_id", "subject" });

            migrationBuilder.CreateIndex(
                name: "IX_passcodes_instance_id_user_id",
                table: "passcodes",
                columns: new[] { "instance_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_passcodes_window_started_at",
                table: "passcodes",
                column: "window_started_at");

            migrationBuilder.CreateIndex(
                name: "IX_passkeys_user_id",
                table: "passkeys",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_project_members_organization_id",
                table: "project_members",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_project_members_user_id_organization_id",
                table: "project_members",
                columns: new[] { "user_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_projects_closure_id",
                table: "projects",
                column: "closure_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_projects_organization_id_name_key",
                table: "projects",
                columns: new[] { "organization_id", "name_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_links_user_id",
                table: "provider_links",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_expires_at",
                table: "sessions",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_instance_id_id",
                table: "sessions",
                columns: new[] { "instance_id", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_sessions_token_hash",
                table: "sessions",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sessions_user_id",
                table: "sessions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_sso_connections_organization_id",
                table: "sso_connections",
                column: "organization_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_step_up_deletion_grants_expires_at",
                table: "step_up_deletion_grants",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_step_up_deletion_grants_user_id",
                table: "step_up_deletion_grants",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_step_up_proofs_expires_at",
                table: "step_up_proofs",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_step_up_proofs_user_id",
                table: "step_up_proofs",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_users_instance_id_email",
                table: "users",
                columns: new[] { "instance_id", "email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_webhook_endpoints_project_id_id",
                table: "webhook_endpoints",
                columns: new[] { "project_id", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_outbox_project_id",
                table: "webhook_outbox",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_outbox_status_next_attempt_at",
                table: "webhook_outbox",
                columns: new[] { "status", "next_attempt_at" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_deletion_receipts");

            migrationBuilder.DropTable(
                name: "api_key_usage");

            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "challenges");

            migrationBuilder.DropTable(
                name: "closure_cancellations");

            migrationBuilder.DropTable(
                name: "closure_commits");

            migrationBuilder.DropTable(
                name: "closure_jobs");

            migrationBuilder.DropTable(
                name: "credential_claims");

            migrationBuilder.DropTable(
                name: "deletion_cursors");

            migrationBuilder.DropTable(
                name: "deletion_jobs");

            migrationBuilder.DropTable(
                name: "email_budgets");

            migrationBuilder.DropTable(
                name: "email_changes");

            migrationBuilder.DropTable(
                name: "email_configs");

            migrationBuilder.DropTable(
                name: "email_deliveries");

            migrationBuilder.DropTable(
                name: "email_reservations");

            migrationBuilder.DropTable(
                name: "invitations");

            migrationBuilder.DropTable(
                name: "mail_outbox");

            migrationBuilder.DropTable(
                name: "memberships");

            migrationBuilder.DropTable(
                name: "mfa_credentials");

            migrationBuilder.DropTable(
                name: "oidc_applications");

            migrationBuilder.DropTable(
                name: "oidc_tokens");

            migrationBuilder.DropTable(
                name: "passcodes");

            migrationBuilder.DropTable(
                name: "passkeys");

            migrationBuilder.DropTable(
                name: "project_auth_settings");

            migrationBuilder.DropTable(
                name: "project_members");

            migrationBuilder.DropTable(
                name: "provider_links");

            migrationBuilder.DropTable(
                name: "rate_limits");

            migrationBuilder.DropTable(
                name: "sessions");

            migrationBuilder.DropTable(
                name: "signing_keys");

            migrationBuilder.DropTable(
                name: "sso_connections");

            migrationBuilder.DropTable(
                name: "step_up_deletion_grants");

            migrationBuilder.DropTable(
                name: "step_up_proofs");

            migrationBuilder.DropTable(
                name: "storage_probes");

            migrationBuilder.DropTable(
                name: "support_deliveries");

            migrationBuilder.DropTable(
                name: "webhook_endpoints");

            migrationBuilder.DropTable(
                name: "webhook_outbox");

            migrationBuilder.DropTable(
                name: "api_keys");

            migrationBuilder.DropTable(
                name: "oidc_authorizations");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "projects");

            migrationBuilder.DropTable(
                name: "organizations");
        }
    }
}
