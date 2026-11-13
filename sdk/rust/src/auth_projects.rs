//! Project tenant administration under `/v1/{project}`.
//!
//! Every call authenticates with the operator's own TerraScale account
//! sign-in token or project secret API key: the producer checks role or scope per
//! request, viewer for reads and admin for changes. Obtain a client with
//! [`crate::TerraScaleClient::projects`].

mod organizations;
pub use organizations::*;

use serde::de::DeserializeOwned;
use serde::{Deserialize, Serialize};
use serde_json::Value;

use crate::auth_idp::{path_id, percent_encode, validate_non_empty, validate_page_limit};
use crate::client::{SharedClient, SKIP_AUTH_MARKER, SKIP_SESSION_MARKER};
use crate::error::{Error, ErrorKind, Result};
use crate::transport::{Method, Request};

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProjectRestrictions {
    pub allowlist: Vec<String>,
    pub blocklist: Vec<String>,
    pub block_disposable_emails: bool,
    pub block_email_aliases: bool,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProjectInvitation {
    pub id: String,
    pub email: String,
    pub redirect_url: Option<String>,
    pub created_at: String,
    pub expires_at: String,
    pub status: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProjectInvitationPage {
    pub items: Vec<ProjectInvitation>,
    pub next: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProjectWaitlistEntry {
    pub id: String,
    pub project_id: String,
    pub email: String,
    pub created_at: String,
    pub status: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProjectWaitlistPage {
    pub items: Vec<ProjectWaitlistEntry>,
    pub next: Option<String>,
}

const MAX_EXPORT_CURSOR_BYTES: usize = 8192;

/// Sign-in methods; at least one of passcode, password and magic_link stays on.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProjectMethods {
    /// Whether the email passcode method is on.
    pub passcode: bool,
    /// Whether the password method is on.
    pub password: bool,
    /// Whether sign-up is allowed.
    pub sign_up_mode: String,
    /// Browser-bound magic links.
    #[serde(default)]
    pub magic_link: bool,
    /// Username and password sign-in.
    #[serde(default)]
    pub username: bool,
}

/// A labelled link underneath the hosted sign-in card.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProjectFooterLink {
    /// Link label.
    pub label: String,
    /// Link target URL.
    pub url: String,
}

/// Hosted page branding; null falls back to the project name and theme.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProjectBranding {
    /// Display name, when set.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    /// Logo URL, when set.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub logo_url: Option<String>,
    /// Theme color, when set.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub theme_color: Option<String>,
    /// Title size.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub title_size: Option<i32>,
    /// Default theme name.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub default_theme: Option<String>,
    /// Footer links.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub footer_links: Option<Vec<ProjectFooterLink>>,
    /// Background color.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub background_color: Option<String>,
    /// Text color.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub text_color: Option<String>,
    /// Email message.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub email_message: Option<String>,
}

/// Project policy in addition to each client's redirect registration.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProjectConfiguration {
    /// Allowed redirect URL prefixes.
    pub redirect_urls: Vec<String>,
    /// Absolute session lifetime in seconds.
    #[serde(default = "default_session_lifetime_seconds")]
    pub session_lifetime_seconds: i64,
    #[serde(default)]
    pub require_mfa: bool,
    pub allow_totp: bool,
    pub allow_passkey: bool,
    pub inactivity_timeout_minutes: Option<u32>,
    #[serde(default)]
    pub single_session: bool,
}

fn default_session_lifetime_seconds() -> i64 {
    1_209_600
}

/// How many users and OAuth clients the project has.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProjectCounts {
    /// End-user count.
    pub users: i64,
    /// OAuth client count.
    pub clients: i64,
}

/// A social sign-in provider; its secret is never answered.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProjectProvider {
    /// Provider name.
    pub provider: String,
    /// Whether the provider is configured.
    pub configured: bool,
    /// Client id at the provider, when configured.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub client_id: Option<String>,
    /// Issuer, when the provider has one.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub issuer: Option<String>,
    /// Apple team id, when the provider has one.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub team_id: Option<String>,
    /// Apple key id, when the provider has one.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub key_id: Option<String>,
    /// The callback URL to register at the provider.
    pub callback_url: String,
    /// Microsoft account audience.
    pub tenant: Option<String>,
    /// Custom OAuth2 endpoints and claims.
    pub oauth2: Option<ProjectOAuth2>,
}

/// A project's settings.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProjectSettings {
    #[serde(default)]
    pub organization_picker: bool,
    /// Project id.
    pub project_id: String,
    /// The project's token issuer.
    pub issuer: String,
    /// OIDC discovery URL.
    pub discovery_url: String,
    /// Hosted sign-in URL.
    pub sign_in_url: String,
    /// Sign-in methods.
    pub methods: ProjectMethods,
    /// Hosted page branding.
    pub branding: ProjectBranding,
    /// User and client counts.
    pub counts: ProjectCounts,
    /// Social sign-in providers.
    pub providers: Vec<ProjectProvider>,
    /// Session policy, when the project has one.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub configuration: Option<ProjectConfiguration>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub restrictions: Option<ProjectRestrictions>,
}

/// Settings changes; an omitted part stays unchanged.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct ProjectSettingsUpdate {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub organization_picker: Option<bool>,
    /// Sign-in method changes.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub methods: Option<ProjectMethods>,
    /// Branding changes.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub branding: Option<ProjectBranding>,
    /// Session policy changes.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub configuration: Option<ProjectConfiguration>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub restrictions: Option<ProjectRestrictions>,
}

/// A project end user.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProjectUser {
    pub first_name: Option<String>,
    pub last_name: Option<String>,
    pub image_url: Option<String>,
    pub external_id: Option<String>,
    pub email_verified: bool,
    pub public_metadata: serde_json::Map<String, Value>,
    pub private_metadata: serde_json::Map<String, Value>,
    pub unsafe_metadata: serde_json::Map<String, Value>,
    /// User id.
    pub id: String,
    /// Email address.
    pub email: String,
    /// Creation time (RFC 3339).
    pub created_at: String,
    /// Whether the user is disabled.
    pub disabled: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct UserProfile {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub first_name: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub last_name: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub image_url: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub external_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub public_metadata: Option<serde_json::Map<String, Value>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub private_metadata: Option<serde_json::Map<String, Value>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub unsafe_metadata: Option<serde_json::Map<String, Value>>,
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct CreateProjectUser {
    pub email: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub password: Option<String>,
    #[serde(flatten)]
    pub profile: UserProfile,
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct ImportProjectUser {
    pub email: String,
    pub email_verified: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub password_hash: Option<String>,
    #[serde(flatten)]
    pub profile: UserProfile,
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct UpdateProjectUser {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub first_name: Option<Value>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub last_name: Option<Value>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub image_url: Option<Value>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub external_id: Option<Value>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub public_metadata: Option<serde_json::Map<String, Value>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub private_metadata: Option<serde_json::Map<String, Value>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub unsafe_metadata: Option<serde_json::Map<String, Value>>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ImportProjectUsers {
    pub users: Vec<ImportProjectUser>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ImportedProjectUser {
    pub index: usize,
    pub user: ProjectUser,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct UserImportError {
    pub index: usize,
    pub code: String,
    pub message: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ImportProjectUsersResult {
    pub items: Vec<ImportedProjectUser>,
    pub errors: Vec<UserImportError>,
}

/// One cursor page of project end users.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProjectUserPage {
    /// Users in this page.
    pub items: Vec<ProjectUser>,
    /// Cursor for the next page; absent on the last page.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub next: Option<String>,
}

/// A verified public provider identity of an exported user.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProjectLinkedProvider {
    /// Provider name.
    pub provider: String,
    /// Client id at the provider.
    pub client_id: String,
    /// Subject id at the provider.
    pub subject: String,
}

/// A directory user in an export page; public identity fields only.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProjectDirectoryUser {
    /// User id.
    pub id: String,
    /// Email address.
    pub email: String,
    /// Whether email possession was proven.
    pub email_verified: bool,
    /// Creation time (RFC 3339).
    pub created_at: String,
    /// Whether the user is disabled.
    pub disabled: bool,
    /// Verified public provider identities.
    pub linked_providers: Vec<ProjectLinkedProvider>,
}

/// One bounded directory export page with its fixed creation cutoff.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProjectUserExportPage {
    /// Directory users in this page.
    pub items: Vec<ProjectDirectoryUser>,
    /// Cursor for the next page; absent when the export is complete.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub next: Option<String>,
    /// The fixed creation cutoff of this export.
    pub created_before: String,
}

/// A federated provider's credentials; an omitted `client_secret` keeps the
/// stored one.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProjectProviderUpdate {
    /// Client id at the provider.
    pub client_id: String,
    /// Client secret; omitted keeps the stored one.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub client_secret: Option<String>,
    /// Issuer, for the `oidc` provider.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub issuer: Option<String>,
    /// Team id, for the `apple` provider.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub team_id: Option<String>,
    /// Key id, for the `apple` provider.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub key_id: Option<String>,
    /// Microsoft account audience.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub tenant: Option<String>,
    /// Custom OAuth2 endpoints and claims.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub oauth2: Option<ProjectOAuth2>,
}

/// One live end-user session; revoking it signs the session out.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProjectSession {
    /// Session id.
    pub id: String,
    /// User id.
    pub user_id: String,
    /// Email address.
    pub email: String,
    /// Creation time (RFC 3339).
    pub created_at: String,
    /// Effective expiry (RFC 3339).
    pub expires_at: String,
}

/// One cursor page of live sessions.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProjectSessionPage {
    /// Sessions in this page.
    pub items: Vec<ProjectSession>,
    /// Cursor for the next page; absent on the last page.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub next: Option<String>,
}

/// A project's registered OAuth client; `client_secret` appears only on
/// confidential creation and rotation.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProjectClient {
    /// Client id.
    pub client_id: String,
    /// Display name.
    pub name: String,
    /// `public` or `confidential`.
    pub r#type: String,
    /// Exact redirect URIs.
    pub redirect_uris: Vec<String>,
    /// Creation time (RFC 3339).
    pub created_at: String,
    /// Client secret, answered exactly once for confidential clients.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub client_secret: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub allowed_scopes: Option<Vec<String>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub allowed_audiences: Option<Vec<String>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub token_lifetime_minutes: Option<i32>,
}

/// One cursor page of the project's OAuth clients.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProjectClientPage {
    /// Clients in this page.
    pub items: Vec<ProjectClient>,
    /// Cursor for the next page; absent on the last page.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub next: Option<String>,
}

/// Request body for OAuth client creation.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct CreateProjectClient {
    /// Display name.
    pub name: String,
    /// `public` or `confidential`.
    pub r#type: String,
    /// Exact redirect URIs.
    pub redirect_uris: Vec<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub allowed_scopes: Option<Vec<String>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub allowed_audiences: Option<Vec<String>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub token_lifetime_minutes: Option<i32>,
}

/// Request body for OAuth client updates.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct UpdateProjectClient {
    /// Display name.
    pub name: String,
    /// Exact redirect URIs.
    pub redirect_uris: Vec<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub allowed_scopes: Option<Vec<String>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub allowed_audiences: Option<Vec<String>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub token_lifetime_minutes: Option<i32>,
}

/// A rotated client secret, answered exactly once; the previous secret keeps
/// working until `previous_secret_expires_at`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProjectClientSecret {
    /// Client id.
    pub client_id: String,
    /// The new client secret.
    pub client_secret: String,
    /// When the previous secret stops working, when a grace was given.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub previous_secret_expires_at: Option<String>,
}

fn validate_cursor(cursor: &str) -> Result<()> {
    validate_non_empty(cursor, "after")?;
    if cursor.trim().len() > MAX_EXPORT_CURSOR_BYTES {
        return Err(Error::new(
            ErrorKind::Validation,
            "invalid_after",
            "after must be a non-empty opaque cursor of at most 8192 bytes.",
        ));
    }
    Ok(())
}

fn provider_name(provider: &str) -> Result<String> {
    if matches!(
        provider,
        "google"
            | "github"
            | "apple"
            | "oidc"
            | "microsoft"
            | "gitlab"
            | "linkedin"
            | "slack"
            | "discord"
            | "facebook"
            | "x"
            | "oauth2"
    ) {
        Ok(provider.to_owned())
    } else {
        Err(Error::new(
            ErrorKind::Validation,
            "invalid_provider",
            "provider must be one of google, github, apple, microsoft, gitlab, linkedin, slack, discord, facebook, x, oidc, oauth2.",
        ))
    }
}

fn client_type(value: &str) -> Result<()> {
    if matches!(value, "public" | "confidential" | "machine") {
        Ok(())
    } else {
        Err(Error::new(
            ErrorKind::Validation,
            "invalid_client_type",
            "type must be public or confidential.",
        ))
    }
}

fn invalid_body(error: serde_json::Error) -> Error {
    Error::new(
        ErrorKind::Validation,
        "invalid_request",
        format!("Request body is not serializable: {error}"),
    )
}

/// Project tenant administration client. Obtain with
/// [`crate::TerraScaleClient::projects`]. Pass a sign-in token or a project secret
/// API key to management calls: auth:read for reads, auth:write for changes.
/// Operations requiring step-up accept only a person's sign-in token.
#[derive(Clone)]
pub struct ProjectsClient {
    shared: SharedClient,
    prefix: String,
}

impl std::fmt::Debug for ProjectsClient {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter
            .debug_struct("ProjectsClient")
            .field("prefix", &self.prefix)
            .finish()
    }
}

impl ProjectsClient {
    pub(crate) fn new(shared: SharedClient) -> Result<Self> {
        let Some(base_url) = shared.config().auth_base_url().map(str::to_owned) else {
            return Err(Error::new(
                ErrorKind::Validation,
                "auth_base_url_not_configured",
                "auth_base_url is not configured.",
            ));
        };
        Ok(Self {
            shared,
            prefix: format!("{base_url}/v1"),
        })
    }

    /// Read the project's settings (`GET /v1/{project}/settings`).
    pub async fn list_invitations(
        &self,
        sign_in: &str,
        project_id: &str,
        limit: Option<u32>,
        after: Option<&str>,
    ) -> Result<ProjectInvitationPage> {
        let query = self.page_query(None, limit, after)?;
        self.send_typed(
            "auth_projects.list_invitations",
            Method::Get,
            &format!("/{}/invitations{query}", self.project(project_id)?),
            None,
            sign_in,
        )
        .await
    }

    pub async fn list_waitlist(
        &self,
        sign_in: &str,
        project_id: &str,
        limit: Option<u32>,
        after: Option<&str>,
    ) -> Result<ProjectWaitlistPage> {
        let query = self.page_query(None, limit, after)?;
        self.send_typed(
            "auth_projects.list_waitlist",
            Method::Get,
            &format!("/{}/waitlist{query}", self.project(project_id)?),
            None,
            sign_in,
        )
        .await
    }

    pub async fn create_invitation(
        &self,
        sign_in: &str,
        project_id: &str,
        email: &str,
        redirect_url: Option<&str>,
    ) -> Result<ProjectInvitation> {
        self.send_typed(
            "auth_projects.create_invitation",
            Method::Post,
            &format!("/{}/invitations", self.project(project_id)?),
            Some(&serde_json::json!({"email": email, "redirect_url": redirect_url})),
            sign_in,
        )
        .await
    }

    pub async fn revoke_invitation(
        &self,
        sign_in: &str,
        project_id: &str,
        invitation_id: &str,
    ) -> Result<()> {
        self.send_no_content(
            "auth_projects.revoke_invitation",
            Method::Delete,
            &format!(
                "/{}/invitations/{}",
                self.project(project_id)?,
                path_id(invitation_id, "invitation_id")?
            ),
            sign_in,
        )
        .await
    }

    pub async fn approve_waitlist(
        &self,
        sign_in: &str,
        project_id: &str,
        entry_id: &str,
    ) -> Result<()> {
        self.send_no_content(
            "auth_projects.approve_waitlist",
            Method::Post,
            &format!(
                "/{}/waitlist/{}/approve",
                self.project(project_id)?,
                path_id(entry_id, "entry_id")?
            ),
            sign_in,
        )
        .await
    }

    pub async fn reject_waitlist(
        &self,
        sign_in: &str,
        project_id: &str,
        entry_id: &str,
    ) -> Result<()> {
        self.send_no_content(
            "auth_projects.reject_waitlist",
            Method::Post,
            &format!(
                "/{}/waitlist/{}/reject",
                self.project(project_id)?,
                path_id(entry_id, "entry_id")?
            ),
            sign_in,
        )
        .await
    }

    pub async fn get_settings(&self, sign_in: &str, project_id: &str) -> Result<ProjectSettings> {
        self.send_typed(
            "auth_projects.get_settings",
            Method::Get,
            &format!("/{}/settings", self.project(project_id)?),
            None,
            sign_in,
        )
        .await
    }

    /// Update the project's settings; omitted parts stay unchanged
    /// (`PUT /v1/{project}/settings`).
    pub async fn update_settings(
        &self,
        sign_in: &str,
        project_id: &str,
        request: &ProjectSettingsUpdate,
    ) -> Result<ProjectSettings> {
        self.send_typed(
            "auth_projects.update_settings",
            Method::Put,
            &format!("/{}/settings", self.project(project_id)?),
            Some(&serde_json::to_value(request).map_err(|error| {
                Error::new(ErrorKind::Validation, "invalid_request", error.to_string())
            })?),
            sign_in,
        )
        .await
    }

    /// List one cursor page of end users; `search` matches an email
    /// substring (`GET /v1/{project}/users`).
    pub async fn list_users(
        &self,
        sign_in: &str,
        project_id: &str,
        search: Option<&str>,
        limit: Option<u32>,
        after: Option<&str>,
    ) -> Result<ProjectUserPage> {
        let query = self.page_query(search, limit, after)?;
        self.send_typed(
            "auth_projects.list_users",
            Method::Get,
            &format!("/{}/users{query}", self.project(project_id)?),
            None,
            sign_in,
        )
        .await
    }

    pub async fn create_user(
        &self,
        sign_in: &str,
        project_id: &str,
        request: &CreateProjectUser,
    ) -> Result<ProjectUser> {
        self.send_typed(
            "auth_projects.create_user",
            Method::Post,
            &format!("/{}/users", self.project(project_id)?),
            Some(&serde_json::to_value(request).map_err(invalid_body)?),
            sign_in,
        )
        .await
    }

    pub async fn update_user(
        &self,
        sign_in: &str,
        project_id: &str,
        user_id: &str,
        request: &UpdateProjectUser,
    ) -> Result<ProjectUser> {
        self.send_typed(
            "auth_projects.update_user",
            Method::Patch,
            &self.user_path(project_id, user_id)?,
            Some(&serde_json::to_value(request).map_err(invalid_body)?),
            sign_in,
        )
        .await
    }

    pub async fn import_users(
        &self,
        sign_in: &str,
        project_id: &str,
        request: &ImportProjectUsers,
    ) -> Result<ImportProjectUsersResult> {
        self.send_typed(
            "auth_projects.import_users",
            Method::Post,
            &format!("/{}/users/import", self.project(project_id)?),
            Some(&serde_json::to_value(request).map_err(invalid_body)?),
            sign_in,
        )
        .await
    }

    /// Read one end user (`GET /v1/{project}/users/{user}`).
    pub async fn get_user(
        &self,
        sign_in: &str,
        project_id: &str,
        user_id: &str,
    ) -> Result<ProjectUser> {
        self.send_typed(
            "auth_projects.get_user",
            Method::Get,
            &self.user_path(project_id, user_id)?,
            None,
            sign_in,
        )
        .await
    }

    /// Disable an end user; every session and refresh token ends at once
    /// (`POST /v1/{project}/users/{user}/disable`).
    pub async fn disable_user(
        &self,
        sign_in: &str,
        project_id: &str,
        user_id: &str,
    ) -> Result<ProjectUser> {
        self.user_state(
            sign_in,
            project_id,
            user_id,
            "disable",
            "auth_projects.disable_user",
        )
        .await
    }

    /// Enable a disabled end user (`POST /v1/{project}/users/{user}/enable`).
    pub async fn enable_user(
        &self,
        sign_in: &str,
        project_id: &str,
        user_id: &str,
    ) -> Result<ProjectUser> {
        self.user_state(
            sign_in,
            project_id,
            user_id,
            "enable",
            "auth_projects.enable_user",
        )
        .await
    }

    /// Delete an end user; the address can sign up again
    /// (`DELETE /v1/{project}/users/{user}`).
    pub async fn delete_user(&self, sign_in: &str, project_id: &str, user_id: &str) -> Result<()> {
        self.send_no_content(
            "auth_projects.delete_user",
            Method::Delete,
            &self.user_path(project_id, user_id)?,
            sign_in,
        )
        .await
    }

    /// Read one bounded directory export page; pass the previous page's
    /// `next` as `after` until `next` is absent
    /// (`GET /v1/{project}/users/export`).
    pub async fn export_users(
        &self,
        sign_in: &str,
        project_id: &str,
        limit: Option<u32>,
        after: Option<&str>,
    ) -> Result<ProjectUserExportPage> {
        let query = self.page_query(None, limit, after)?;
        self.send_typed(
            "auth_projects.export_users",
            Method::Get,
            &format!("/{}/users/export{query}", self.project(project_id)?),
            None,
            sign_in,
        )
        .await
    }

    /// Configure a federated sign-in provider; an omitted `client_secret`
    /// keeps the stored one (`PUT /v1/{project}/providers/{provider}`).
    pub async fn set_provider(
        &self,
        sign_in: &str,
        project_id: &str,
        provider: &str,
        request: &ProjectProviderUpdate,
    ) -> Result<ProjectSettings> {
        validate_non_empty(&request.client_id, "client_id")?;
        self.send_typed(
            "auth_projects.set_provider",
            Method::Put,
            &format!(
                "/{}/providers/{}",
                self.project(project_id)?,
                provider_name(provider)?
            ),
            Some(&serde_json::to_value(request).map_err(invalid_body)?),
            sign_in,
        )
        .await
    }

    /// Remove a federated sign-in provider; answers the settings without it
    /// (`DELETE /v1/{project}/providers/{provider}`).
    pub async fn delete_provider(
        &self,
        sign_in: &str,
        project_id: &str,
        provider: &str,
    ) -> Result<ProjectSettings> {
        self.send_typed(
            "auth_projects.delete_provider",
            Method::Delete,
            &format!(
                "/{}/providers/{}",
                self.project(project_id)?,
                provider_name(provider)?
            ),
            None,
            sign_in,
        )
        .await
    }

    /// List live sessions, of one user when `user_id` is given
    /// (`GET /v1/{project}/sessions`).
    pub async fn list_sessions(
        &self,
        sign_in: &str,
        project_id: &str,
        user_id: Option<&str>,
        limit: Option<u32>,
        after: Option<&str>,
    ) -> Result<ProjectSessionPage> {
        let mut query = Vec::new();
        if let Some(user_id) = user_id {
            query.push(format!("user={}", path_id(user_id, "user_id")?));
        }
        if let Some(limit) = limit {
            validate_page_limit(limit, 100, "session")?;
            query.push(format!("limit={limit}"));
        }
        if let Some(cursor) = after {
            validate_cursor(cursor)?;
            query.push(format!("after={}", percent_encode(cursor.trim())));
        }
        let suffix = if query.is_empty() {
            String::new()
        } else {
            format!("?{}", query.join("&"))
        };
        self.send_typed(
            "auth_projects.list_sessions",
            Method::Get,
            &format!("/{}/sessions{suffix}", self.project(project_id)?),
            None,
            sign_in,
        )
        .await
    }

    /// Revoke one live session, signing it out
    /// (`DELETE /v1/{project}/sessions/{session}`).
    pub async fn revoke_session(
        &self,
        sign_in: &str,
        project_id: &str,
        session_id: &str,
    ) -> Result<()> {
        self.send_no_content(
            "auth_projects.revoke_session",
            Method::Delete,
            &format!(
                "/{}/sessions/{}",
                self.project(project_id)?,
                path_id(session_id, "session_id")?
            ),
            sign_in,
        )
        .await
    }

    pub async fn sign_out_user(
        &self,
        sign_in: &str,
        project_id: &str,
        user_id: &str,
    ) -> Result<()> {
        self.send_no_content(
            "auth_projects.sign_out_user",
            Method::Post,
            &format!(
                "/{}/users/{}/sign-out",
                self.project(project_id)?,
                path_id(user_id, "user_id")?
            ),
            sign_in,
        )
        .await
    }

    /// List the project's OAuth clients in one page
    /// (`GET /v1/{project}/clients`).
    pub async fn list_clients(&self, sign_in: &str, project_id: &str) -> Result<ProjectClientPage> {
        self.send_typed(
            "auth_projects.list_clients",
            Method::Get,
            &format!("/{}/clients", self.project(project_id)?),
            None,
            sign_in,
        )
        .await
    }

    /// Register an OAuth client; the answer carries `client_secret` exactly
    /// once for a confidential client (`POST /v1/{project}/clients`).
    pub async fn create_client(
        &self,
        sign_in: &str,
        project_id: &str,
        request: &CreateProjectClient,
        step_up_proof: &str,
    ) -> Result<ProjectClient> {
        client_type(&request.r#type)?;
        self.send_proved(
            "auth_projects.create_client",
            Method::Post,
            &format!("/{}/clients", self.project(project_id)?),
            Some(&serde_json::to_value(request).map_err(invalid_body)?),
            step_up_proof,
            sign_in,
        )
        .await
    }

    /// Rename an OAuth client or replace its exact redirect URIs
    /// (`PUT /v1/{project}/clients/{client_id}`).
    pub async fn update_client(
        &self,
        sign_in: &str,
        project_id: &str,
        client_id: &str,
        request: &UpdateProjectClient,
        step_up_proof: &str,
    ) -> Result<ProjectClient> {
        self.send_proved(
            "auth_projects.update_client",
            Method::Put,
            &format!(
                "/{}/clients/{}",
                self.project(project_id)?,
                path_id(client_id, "client_id")?
            ),
            Some(&serde_json::to_value(request).map_err(invalid_body)?),
            step_up_proof,
            sign_in,
        )
        .await
    }

    /// Rotate a confidential client's secret; the answer carries it exactly
    /// once and the previous secret keeps working for `grace_seconds`
    /// (default 3600, 0 to 86400)
    /// (`POST /v1/{project}/clients/{client_id}/secret/rotate`).
    pub async fn rotate_client_secret(
        &self,
        sign_in: &str,
        project_id: &str,
        client_id: &str,
        grace_seconds: Option<u32>,
        step_up_proof: &str,
    ) -> Result<ProjectClientSecret> {
        let body = match grace_seconds {
            Some(seconds) if seconds > 86_400 => {
                return Err(Error::new(
                    ErrorKind::Validation,
                    "invalid_grace_seconds",
                    "grace_seconds must be 0 to 86400 seconds.",
                ));
            }
            Some(seconds) => serde_json::json!({ "grace_seconds": seconds }),
            None => serde_json::json!({}),
        };
        self.send_proved(
            "auth_projects.rotate_client_secret",
            Method::Post,
            &format!(
                "/{}/clients/{}/secret/rotate",
                self.project(project_id)?,
                path_id(client_id, "client_id")?
            ),
            Some(&body),
            step_up_proof,
            sign_in,
        )
        .await
    }

    /// Delete an OAuth client (`DELETE /v1/{project}/clients/{client_id}`).
    pub async fn delete_client(
        &self,
        sign_in: &str,
        project_id: &str,
        client_id: &str,
    ) -> Result<()> {
        self.send_no_content(
            "auth_projects.delete_client",
            Method::Delete,
            &format!(
                "/{}/clients/{}",
                self.project(project_id)?,
                path_id(client_id, "client_id")?
            ),
            sign_in,
        )
        .await
    }

    fn project(&self, project_id: &str) -> Result<String> {
        path_id(project_id, "project_id")
    }

    fn user_path(&self, project_id: &str, user_id: &str) -> Result<String> {
        Ok(format!(
            "/{}/users/{}",
            self.project(project_id)?,
            path_id(user_id, "user_id")?
        ))
    }

    fn page_query(
        &self,
        search: Option<&str>,
        limit: Option<u32>,
        after: Option<&str>,
    ) -> Result<String> {
        let mut query = Vec::new();
        if let Some(search) = search {
            validate_non_empty(search, "search")?;
            query.push(format!("q={}", percent_encode(search)));
        }
        if let Some(limit) = limit {
            validate_page_limit(limit, 100, "user")?;
            query.push(format!("limit={limit}"));
        }
        if let Some(cursor) = after {
            validate_cursor(cursor)?;
            query.push(format!("after={}", percent_encode(cursor.trim())));
        }
        Ok(if query.is_empty() {
            String::new()
        } else {
            format!("?{}", query.join("&"))
        })
    }

    async fn user_state(
        &self,
        sign_in: &str,
        project_id: &str,
        user_id: &str,
        action: &str,
        route: &str,
    ) -> Result<ProjectUser> {
        self.send_typed(
            route,
            Method::Post,
            &format!("{}/{}", self.user_path(project_id, user_id)?, action),
            None,
            sign_in,
        )
        .await
    }

    fn authorize(
        &self,
        route: &str,
        method: Method,
        path: &str,
        body: Option<&Value>,
        proof: Option<&str>,
        sign_in: &str,
    ) -> Result<Request> {
        validate_non_empty(sign_in, "sign_in")?;
        let url = format!("{}{path}", self.prefix);
        let mut request = match body {
            Some(body) => Request::json(route, method, url, body)?,
            None => Request {
                route_name: route.to_owned(),
                method,
                url,
                headers: Vec::new(),
                body: None,
            },
        };
        request
            .headers
            .push(("authorization".to_owned(), format!("Bearer {sign_in}")));
        if let Some(proof) = proof {
            validate_non_empty(proof, "step_up_proof")?;
            request
                .headers
                .push(("x-terrascale-step-up-proof".to_owned(), proof.to_owned()));
        }
        request
            .headers
            .push((SKIP_AUTH_MARKER.to_owned(), String::new()));
        request
            .headers
            .push((SKIP_SESSION_MARKER.to_owned(), String::new()));
        Ok(request)
    }

    async fn send_typed<T: DeserializeOwned>(
        &self,
        route: &str,
        method: Method,
        path: &str,
        body: Option<&Value>,
        sign_in: &str,
    ) -> Result<T> {
        let request = self.authorize(route, method, path, body, None, sign_in)?;
        self.shared.send_typed(request).await
    }

    async fn send_proved<T: DeserializeOwned>(
        &self,
        route: &str,
        method: Method,
        path: &str,
        body: Option<&Value>,
        proof: &str,
        sign_in: &str,
    ) -> Result<T> {
        let request = self.authorize(route, method, path, body, Some(proof), sign_in)?;
        self.shared.send_typed(request).await
    }

    async fn send_no_content(
        &self,
        route: &str,
        method: Method,
        path: &str,
        sign_in: &str,
    ) -> Result<()> {
        let request = self.authorize(route, method, path, None, None, sign_in)?;
        let response = self.shared.send_response(request).await?;
        if response.status != 204 {
            let mut error =
                response.invalid_response(format!("expected HTTP 204, got {}", response.status));
            error.code = "expected_http_204".into();
            return Err(error);
        }
        Ok(())
    }
}

#[derive(Clone, Deserialize)]
pub struct UserApiKey {
    pub id: String,
    pub user_id: String,
    pub name: String,
    pub prefix: String,
    pub scopes: Vec<String>,
    pub created_at: String,
    pub expires_at: Option<String>,
    pub key: Option<String>,
}

impl std::fmt::Debug for UserApiKey {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str("UserApiKey{secret:[REDACTED]}")
    }
}

#[derive(Debug, Clone, Deserialize)]
pub struct UserApiKeyPage {
    pub items: Vec<UserApiKey>,
}

#[derive(Debug, Clone, Serialize)]
pub struct CreateUserApiKey {
    pub name: String,
    pub scopes: Vec<String>,
    pub expires_at: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct UserApiKeyPolicy {
    pub allowed_scopes: Vec<String>,
}

#[derive(Debug, Clone, Deserialize)]
pub struct UserApiKeyVerification {
    pub valid: bool,
    pub user_id: Option<String>,
    pub scopes: Option<Vec<String>>,
    pub key_id: Option<String>,
    pub expires_at: Option<String>,
}

impl ProjectsClient {
    pub async fn create_user_api_key(
        &self,
        credential: &str,
        project: &str,
        user: &str,
        request: &CreateUserApiKey,
    ) -> Result<UserApiKey> {
        self.send_typed(
            "auth_projects.create_user_api_key",
            Method::Post,
            &format!(
                "/{}/users/{}/api-keys",
                self.project(project)?,
                path_id(user, "user")?
            ),
            Some(&serde_json::to_value(request).map_err(invalid_body)?),
            credential,
        )
        .await
    }

    pub async fn list_user_api_keys(
        &self,
        credential: &str,
        project: &str,
        user: &str,
    ) -> Result<UserApiKeyPage> {
        self.send_typed(
            "auth_projects.list_user_api_keys",
            Method::Get,
            &format!(
                "/{}/users/{}/api-keys",
                self.project(project)?,
                path_id(user, "user")?
            ),
            None,
            credential,
        )
        .await
    }

    pub async fn revoke_user_api_key(
        &self,
        credential: &str,
        project: &str,
        user: &str,
        key: &str,
    ) -> Result<()> {
        self.send_no_content(
            "auth_projects.revoke_user_api_key",
            Method::Delete,
            &format!(
                "/{}/users/{}/api-keys/{}",
                self.project(project)?,
                path_id(user, "user")?,
                path_id(key, "key")?
            ),
            credential,
        )
        .await
    }

    pub async fn verify_user_api_key(
        &self,
        credential: &str,
        project: &str,
        key: &str,
    ) -> Result<UserApiKeyVerification> {
        self.send_typed(
            "auth_projects.verify_user_api_key",
            Method::Post,
            &format!("/{}/api-keys/verify", self.project(project)?),
            Some(&serde_json::json!({"key": key})),
            credential,
        )
        .await
    }

    pub async fn set_user_api_key_policy(
        &self,
        credential: &str,
        project: &str,
        request: &UserApiKeyPolicy,
    ) -> Result<UserApiKeyPolicy> {
        self.send_typed(
            "auth_projects.set_user_api_key_policy",
            Method::Put,
            &format!("/{}/user-api-key-policy", self.project(project)?),
            Some(&serde_json::to_value(request).map_err(invalid_body)?),
            credential,
        )
        .await
    }
}

/// Custom OAuth2 endpoints, scopes and dot-separated claim paths.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProjectOAuth2 {
    /// Authorization URL.
    pub authorize_url: String,
    /// Token URL.
    pub token_url: String,
    /// Userinfo URL.
    pub userinfo_url: String,
    /// Space-separated scopes.
    pub scopes: String,
    /// Subject claim path.
    pub id_claim: String,
    /// Email claim path.
    pub email_claim: String,
    /// Boolean email verification claim path.
    pub email_verified_claim: String,
}
