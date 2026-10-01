//! Narrow adapter for the provisional document query wire contract missing from ts-rust.
//! The pinned SDK `OpenAPI` has no query route; production compatibility must be
//! checked once the `TerraBase` producer publishes that contract.
use reqwest::StatusCode;
use serde::{Deserialize, Serialize};
use serde_json::Value;

use crate::{StoreError, StoreResult};

/// Nearest-region public TerraScale API router.
pub const TERRASCALE_API_HOST: &str = "api.terrascale.tech";

/// Regional storage authority shared by every hosted auth replica.
pub const GRU_AUTHORITY_HOST: &str = "gru.api.terrascale.tech";

#[derive(Clone)]
pub struct QueryTransport {
    client: reqwest::Client,
    base_url: String,
    api_key: String,
    project_id: Option<String>,
    tenant_id: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
pub struct Predicate {
    pub path: String,
    pub operator: &'static str,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub value: Option<Value>,
}

impl Predicate {
    pub fn equal(path: impl Into<String>, value: impl Serialize) -> StoreResult<Self> {
        Ok(Self {
            path: path.into(),
            operator: "equal",
            value: Some(serde_json::to_value(value)?),
        })
    }
    pub fn greater_than_or_equal(
        path: impl Into<String>,
        value: impl Serialize,
    ) -> StoreResult<Self> {
        Ok(Self {
            path: path.into(),
            operator: "greaterThanOrEqual",
            value: Some(serde_json::to_value(value)?),
        })
    }
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct QueryRow {
    pub id: String,
    pub version: u64,
    pub value: Value,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct QueryPage {
    pub documents: Vec<QueryRow>,
    pub has_more: bool,
    pub next_after: Option<String>,
}

impl QueryTransport {
    pub fn new(
        base_url: impl AsRef<str>,
        api_key: impl Into<String>,
        project_id: Option<String>,
        tenant_id: Option<String>,
    ) -> StoreResult<Self> {
        let base_url = base_url.as_ref().trim_end_matches('/');
        let url = reqwest::Url::parse(base_url)
            .map_err(|error| StoreError::InvalidConfiguration(error.to_string()))?;
        let host = url.host_str().ok_or_else(|| {
            StoreError::InvalidConfiguration("TerraBase API URL needs a host".into())
        })?;
        let loopback = matches!(host, "localhost" | "127.0.0.1" | "::1");
        if !url.username().is_empty()
            || url.password().is_some()
            || url.query().is_some()
            || url.fragment().is_some()
        {
            return Err(StoreError::InvalidConfiguration(
                "TerraBase API URL must be a bare origin".into(),
            ));
        }
        if !loopback
            && (url.scheme() != "https"
                || !matches!(host, TERRASCALE_API_HOST | GRU_AUTHORITY_HOST)
                || url.port().is_some()
                || url.path() != "/")
        {
            return Err(StoreError::InvalidConfiguration(format!(
                "Auth query transport requires the bare HTTPS origin https://{TERRASCALE_API_HOST} or https://{GRU_AUTHORITY_HOST}, with no non-default port or path"
            )));
        }
        let effective = if loopback {
            base_url.to_owned()
        } else {
            format!("https://{GRU_AUTHORITY_HOST}")
        };
        let api_key_input = api_key.into();
        let api_key = normalize_api_key(&api_key_input)?;
        Ok(Self {
            client: reqwest::Client::builder()
                .redirect(reqwest::redirect::Policy::none())
                .timeout(std::time::Duration::from_secs(30))
                .build()?,
            base_url: effective,
            api_key,
            project_id,
            tenant_id,
        })
    }

    pub async fn query(
        &self,
        database: &str,
        collection: &str,
        predicates: &[Predicate],
        limit: u32,
        after: Option<&str>,
    ) -> StoreResult<QueryPage> {
        if !(1..=1000).contains(&limit)
            || predicates.len() > 32
            || after.is_some_and(|cursor| cursor.len() > 255)
        {
            return Err(StoreError::InvalidConfiguration(
                "Invalid document query page".into(),
            ));
        }
        let url = format!(
            "{}/api/v1/databases/{}/collections/{}/documents/query",
            self.base_url,
            encode(database),
            encode(collection)
        );
        let mut request = self
            .client
            .post(url)
            .bearer_auth(&self.api_key)
            .header("accept", "application/json")
            .json(&serde_json::json!({"predicates": predicates, "limit": limit, "after": after}));
        if let Some(project) = &self.project_id {
            request = request.header("x-terrascale-project-id", project);
        }
        if let Some(tenant) = &self.tenant_id {
            request = request.header("x-terrascale-tenant-id", tenant);
        }
        let response = request.send().await?;
        if response.status() == StatusCode::MISDIRECTED_REQUEST {
            return Err(StoreError::InvalidConfiguration(
                "GRU auth authority redirected query to another region".into(),
            ));
        }
        if !response.status().is_success() {
            let status = response.status();
            let body: Value = response.json().await.unwrap_or(Value::Null);
            return Err(StoreError::Http {
                status: status.as_u16(),
                code: body
                    .pointer("/error/code")
                    .and_then(Value::as_str)
                    .unwrap_or("storage_error")
                    .to_owned(),
            });
        }
        Ok(response.json().await?)
    }
}

fn normalize_api_key(input: &str) -> StoreResult<String> {
    if input.chars().any(char::is_control) {
        return Err(StoreError::InvalidConfiguration(
            "TerraBase API key contains control characters".into(),
        ));
    }
    let value = input.trim();
    let value = if value.eq_ignore_ascii_case("bearer") {
        ""
    } else if value
        .as_bytes()
        .get(..6)
        .is_some_and(|prefix| prefix.eq_ignore_ascii_case(b"bearer"))
        && value
            .get(6..)
            .is_some_and(|rest| rest.chars().next().is_some_and(char::is_whitespace))
    {
        value.get(6..).unwrap_or_default().trim()
    } else {
        value
    };
    if value.is_empty() || value.chars().any(char::is_whitespace) {
        return Err(StoreError::InvalidConfiguration(
            "TerraBase API key is empty or contains whitespace".into(),
        ));
    }
    Ok(value.to_owned())
}

fn encode(value: &str) -> String {
    url::form_urlencoded::byte_serialize(value.as_bytes()).collect()
}
