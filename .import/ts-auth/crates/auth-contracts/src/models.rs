//! JSON documents persisted by the C# server; field names and numeric timestamps are wire-compatible.
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Instance {
    pub id: String,
    pub slug: String,
    pub name: String,
    #[serde(default)]
    pub logo_url: Option<String>,
    #[serde(default)]
    pub theme_color: Option<String>,
    #[serde(default)]
    pub stripe_customer_id: Option<String>,
    pub created_at: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct User {
    pub id: String,
    pub instance_id: String,
    #[serde(default)]
    pub email: Option<String>,
    #[serde(default)]
    pub email_verified: bool,
    /// Once established by a trusted flow, an identity keeps its User ID
    /// across later email changes and re-verification.
    #[serde(default)]
    pub identity_established: bool,
    #[serde(default)]
    pub phone: Option<String>,
    #[serde(default)]
    pub username: Option<String>,
    #[serde(default)]
    pub password_hash: Option<String>,
    /// Invalidates credentials issued before a password or recovery change.
    #[serde(default)]
    pub auth_epoch: u64,
    #[serde(default)]
    pub name: Option<String>,
    #[serde(default)]
    pub image_url: Option<String>,
    #[serde(default)]
    pub banned: bool,
    #[serde(default)]
    pub ban_reason: Option<String>,
    #[serde(default)]
    pub deleted: bool,
    /// Internal owner and recovery phase while a SCIM identity is hidden.
    #[serde(default)]
    pub scim_provisioning: Option<ScimProvisioning>,
    /// Instance-wide directories with authority over this user's authentication.
    #[serde(default)]
    pub scim_instance_wide_directory_ids: Vec<String>,
    #[serde(default)]
    pub public_metadata: serde_json::Map<String, serde_json::Value>,
    #[serde(default)]
    pub private_metadata: serde_json::Map<String, serde_json::Value>,
    /// Internal quota reservation; never sourced from customer metadata.
    #[serde(default)]
    pub quota_seat: Option<u32>,
    #[serde(default)]
    pub created_at: i64,
    #[serde(default)]
    pub updated_at: i64,
    #[serde(default)]
    pub last_sign_in_at: Option<i64>,
    #[serde(default)]
    pub email_key: Option<String>,
    #[serde(default)]
    pub username_key: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ScimProvisioning {
    pub directory_id: String,
    #[serde(default)]
    pub phase: ScimProvisioningPhase,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ScimProvisioningPhase {
    #[default]
    Pending,
    Cleaning,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Passkey {
    pub id: String,
    pub instance_id: String,
    pub user_id: String,
    pub credential_key: String,
    pub public_key_cbor_base64: String,
    pub algorithm: String,
    pub sign_count: u32,
    #[serde(default)]
    pub transports: Option<String>,
    pub nickname: String,
    #[serde(default)]
    pub created_at: i64,
    #[serde(default)]
    pub last_used_at: Option<i64>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Session {
    pub id: String,
    pub instance_id: String,
    pub user_id: String,
    #[serde(default)]
    pub auth_epoch: u64,
    pub token_key: String,
    pub status: String,
    pub created_at: i64,
    pub expires_at: i64,
    #[serde(default)]
    pub last_active_at: Option<i64>,
    #[serde(default)]
    pub user_agent: Option<String>,
    #[serde(default)]
    pub ip_address: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OAuthClient {
    pub id: String,
    pub instance_id: String,
    pub client_id: String,
    pub client_key: String,
    pub name: String,
    #[serde(default)]
    pub client_secret_hash: Option<String>,
    pub redirect_uris: Vec<String>,
    #[serde(default)]
    pub allowed_origins: Vec<String>,
    pub token_endpoint_auth_method: String,
    #[serde(default)]
    #[serde(rename = "public")]
    pub r#public: bool,
    #[serde(default)]
    pub created_at: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AuthorizationCode {
    pub id: String,
    pub instance_id: String,
    pub client_id: String,
    pub user_id: String,
    #[serde(default)]
    pub user_auth_epoch: u64,
    pub code_key: String,
    pub redirect_uri: String,
    pub scopes: Vec<String>,
    #[serde(default)]
    pub code_challenge: Option<String>,
    #[serde(default)]
    pub code_challenge_method: Option<String>,
    #[serde(default)]
    pub nonce: Option<String>,
    pub expires_at: i64,
    #[serde(default)]
    pub consumed: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RefreshToken {
    pub id: String,
    pub instance_id: String,
    pub client_id: String,
    pub user_id: String,
    #[serde(default)]
    pub user_auth_epoch: u64,
    pub token_key: String,
    pub status: String,
    pub scopes: Vec<String>,
    pub created_at: i64,
    pub expires_at: i64,
    #[serde(default)]
    pub rotated_to: Option<String>,
    #[serde(default)]
    pub family_id: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ApiKey {
    pub id: String,
    pub instance_id: String,
    pub name: String,
    pub secret_key: String,
    pub scopes: Vec<String>,
    pub created_at: i64,
    #[serde(default)]
    pub last_used_at: Option<i64>,
    #[serde(default)]
    pub revoked: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MfaFactor {
    pub id: String,
    pub instance_id: String,
    pub user_id: String,
    pub r#type: String,
    #[serde(default)]
    pub secret_box: Option<String>,
    #[serde(default)]
    pub recovery_codes: Option<Vec<String>>,
    #[serde(default)]
    pub confirmed: bool,
    pub created_at: i64,
    #[serde(default)]
    pub last_used_totp_step: Option<i64>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Organization {
    pub id: String,
    pub instance_id: String,
    pub name: String,
    pub slug_key: String,
    #[serde(default)]
    pub status: OrganizationStatus,
    #[serde(default)]
    pub public_metadata: serde_json::Map<String, serde_json::Value>,
    #[serde(default)]
    pub created_at: i64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum OrganizationStatus {
    #[default]
    Active,
    Deleting,
    Deleted,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OrgMembership {
    pub id: String,
    pub instance_id: String,
    pub org_id: String,
    pub user_id: String,
    pub member_key: String,
    pub role: String,
    #[serde(default)]
    pub created_at: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Invitation {
    pub id: String,
    pub instance_id: String,
    pub org_id: String,
    pub email: String,
    pub role: String,
    pub status: String,
    pub expires_at: i64,
    #[serde(default)]
    pub created_at: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct VerificationToken {
    pub id: String,
    pub instance_id: String,
    #[serde(default)]
    pub user_id: Option<String>,
    pub purpose: String,
    /// Original first-factor method carried through a pending MFA flow.
    #[serde(default)]
    pub authentication_method: Option<String>,
    /// First-factor continuation target persisted with one-time state.
    #[serde(default)]
    pub authentication_next: Option<String>,
    /// User credential epoch proven by a pending first-factor challenge.
    #[serde(default)]
    pub user_auth_epoch: Option<u64>,
    pub token_key: String,
    pub expires_at: i64,
    #[serde(default)]
    pub consumed: bool,
    #[serde(default)]
    pub failed_attempts: i32,
    #[serde(default)]
    pub created_at: i64,
    #[serde(default)]
    pub email_snapshot: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct WebhookEndpoint {
    pub id: String,
    pub instance_id: String,
    pub url: String,
    pub events: Vec<String>,
    pub signing_secret_box: String,
    #[serde(default = "default_true")]
    pub enabled: bool,
    #[serde(default)]
    pub created_at: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct WebhookCursor {
    pub id: String,
    pub instance_id: String,
    pub last_created_at: i64,
    pub last_event_id: String,
    pub updated_at: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct WebhookDelivery {
    pub id: String,
    pub instance_id: String,
    pub endpoint_id: String,
    pub event_id: String,
    pub status: String,
    #[serde(default)]
    pub attempts: i32,
    #[serde(default)]
    pub last_error: Option<String>,
    #[serde(default)]
    pub updated_at: i64,
    #[serde(default)]
    pub next_attempt_at: Option<i64>,
    #[serde(default)]
    pub lease_until: Option<i64>,
    #[serde(default)]
    pub lease_token: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AuditEvent {
    pub id: String,
    pub instance_id: String,
    pub r#type: String,
    #[serde(default)]
    pub actor_id: Option<String>,
    #[serde(default)]
    pub data: serde_json::Map<String, serde_json::Value>,
    #[serde(default)]
    pub created_at: i64,
    #[serde(default)]
    pub integrity_version: Option<i32>,
    #[serde(default)]
    pub integrity_hash: Option<String>,
    #[serde(default)]
    pub integrity_signature: Option<String>,
    #[serde(default)]
    pub integrity_key_fingerprint: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SigningKeyRecord {
    pub id: String,
    pub instance_id: String,
    pub algorithm: String,
    pub secret_base64: String,
    #[serde(default)]
    pub active: bool,
    #[serde(default)]
    pub created_at: i64,
    #[serde(default)]
    pub retired_at: Option<i64>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Connection {
    pub id: String,
    pub instance_id: String,
    pub r#type: String,
    pub name: String,
    #[serde(default = "default_true")]
    pub enabled: bool,
    #[serde(default)]
    pub org_id: Option<String>,
    #[serde(default)]
    pub issuer_url: Option<String>,
    #[serde(default)]
    pub client_id: Option<String>,
    #[serde(default)]
    pub client_secret_box: Option<String>,
    #[serde(default)]
    pub authorization_endpoint: Option<String>,
    #[serde(default)]
    pub token_endpoint: Option<String>,
    #[serde(default)]
    pub userinfo_endpoint: Option<String>,
    #[serde(default)]
    pub idp_entity_id: Option<String>,
    #[serde(default)]
    pub sso_redirect_url: Option<String>,
    #[serde(default)]
    pub x509_certificate_pem: Option<String>,
    #[serde(default)]
    pub sp_entity_id: Option<String>,
    #[serde(default)]
    pub created_at: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ScimDirectory {
    pub id: String,
    pub instance_id: String,
    #[serde(default)]
    pub org_id: Option<String>,
    #[serde(default)]
    pub instance_wide: bool,
    #[serde(default)]
    pub status: ScimDirectoryStatus,
    pub token_key: String,
    #[serde(default)]
    pub created_at: i64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ScimDirectoryStatus {
    #[default]
    Active,
    Deleting,
    Deleted,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ScimUserLink {
    pub id: String,
    pub instance_id: String,
    pub directory_id: String,
    pub user_id: String,
    #[serde(default)]
    pub external_id: Option<String>,
    #[serde(default = "default_true")]
    pub active: bool,
    /// Durable deprovisioning fence until owned child cleanup completes.
    #[serde(default)]
    pub deleting: bool,
    /// Exclusive finisher for a deleting link; recovery requires quiescent repair.
    #[serde(default)]
    pub cleanup_owner: Option<String>,
    /// Distinguishes a replacement link from a prior incarnation of its ID.
    #[serde(default)]
    pub incarnation_id: Option<String>,
    /// Terminal deprovisioning marker retained to prevent same-ID ETag ABA.
    #[serde(default)]
    pub cleanup_complete: bool,
    #[serde(default)]
    pub profile_user_name: Option<String>,
    #[serde(default)]
    pub profile_name: Option<String>,
    #[serde(default)]
    pub profile_initialized: bool,
    #[serde(default)]
    pub created_at: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Subscription {
    pub id: String,
    pub instance_id: String,
    pub stripe_customer_id: String,
    #[serde(default)]
    pub stripe_subscription_id: Option<String>,
    pub plan: String,
    pub status: String,
    #[serde(default)]
    pub current_period_end: Option<i64>,
    #[serde(default)]
    pub created_at: i64,
    #[serde(default)]
    pub updated_at: i64,
    #[serde(default)]
    pub last_stripe_event_created: Option<i64>,
    #[serde(default)]
    pub last_stripe_event_id: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct JwtTemplate {
    pub id: String,
    pub instance_id: String,
    pub name: String,
    pub claims: std::collections::BTreeMap<String, String>,
    #[serde(default = "default_lifetime_seconds")]
    pub lifetime_seconds: i32,
    #[serde(default)]
    pub created_at: i64,
}

fn default_true() -> bool {
    true
}
fn default_lifetime_seconds() -> i32 {
    300
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OrgDomain {
    pub id: String,
    pub instance_id: String,
    pub org_id: String,
    pub domain: String,
    pub domain_key: String,
    pub created_at: i64,
}
