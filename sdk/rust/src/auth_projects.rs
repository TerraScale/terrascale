//! Project tenant administration under `/v1/{project}`.
//!
//! Every call authenticates with the operator's own TerraScale account
//! sign-in token (compact JWT): the producer checks the project role per
//! request, viewer for reads and admin for changes. Obtain a client with
//! [`crate::TerraScaleClient::projects`].

use serde::de::DeserializeOwned;
use serde::{Deserialize, Serialize};

use crate::auth_idp::{path_id, percent_encode, validate_non_empty, validate_page_limit};
use crate::client::{SharedClient, SKIP_AUTH_MARKER, SKIP_SESSION_MARKER};
use crate::error::{Error, ErrorKind, Result};
use crate::transport::{Method, Request};

const MAX_EXPORT_CURSOR_BYTES: usize = 8192;

/// Sign-in methods; at least one of passcode and password stays on.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProjectMethods {
    /// Whether the email passcode method is on.
    pub passcode: bool,
    /// Whether the password method is on.
    pub password: bool,
    /// Whether sign-up is allowed.
    pub sign_up: bool,
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
}

/// A project's settings.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProjectSettings {
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
}

/// Settings changes; an omitted part stays unchanged.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct ProjectSettingsUpdate {
    /// Sign-in method changes.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub methods: Option<ProjectMethods>,
    /// Branding changes.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub branding: Option<ProjectBranding>,
    /// Session policy changes.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub configuration: Option<ProjectConfiguration>,
}

/// A project end user.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProjectUser {
    /// User id.
    pub id: String,
    /// Email address.
    pub email: String,
    /// Creation time (RFC 3339).
    pub created_at: String,
    /// Whether the user is disabled.
    pub disabled: bool,
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

/// Project tenant administration client. Obtain with
/// [`crate::TerraScaleClient::projects`].
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
            Some(request),
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
        let request = self.authorize(
            "auth_projects.delete_user",
            Method::Delete,
            &self.user_path(project_id, user_id)?,
            None,
            sign_in,
        )?;
        let response = self.shared.send_response(request).await?;
        if response.status != 204 {
            let mut error =
                response.invalid_response(format!("expected HTTP 204, got {}", response.status));
            error.code = "expected_http_204".into();
            return Err(error);
        }
        Ok(())
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
            validate_non_empty(cursor, "after")?;
            let trimmed = cursor.trim();
            if trimmed.len() > MAX_EXPORT_CURSOR_BYTES {
                return Err(Error::new(
                    ErrorKind::Validation,
                    "invalid_after",
                    "after must be a non-empty opaque cursor of at most 8192 bytes.",
                ));
            }
            query.push(format!("after={}", percent_encode(trimmed)));
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
        body: Option<&ProjectSettingsUpdate>,
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
        body: Option<&ProjectSettingsUpdate>,
        sign_in: &str,
    ) -> Result<T> {
        let request = self.authorize(route, method, path, body, sign_in)?;
        self.shared.send_typed(request).await
    }
}
