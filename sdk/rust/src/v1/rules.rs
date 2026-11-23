use super::client::Req;
use super::{Database, Error, ManagementPage, Result};
use reqwest::{Method, StatusCode};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::collections::BTreeMap;

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct RulesSet {
    pub tables: BTreeMap<String, RulesTable>,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct RulesTable {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub read: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub create: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub update: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub delete: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub columns: Option<Vec<String>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub writable: Option<Vec<String>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub validate: Option<Vec<String>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub set: Option<RulesValues>,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct RulesValues {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub create: Option<BTreeMap<String, String>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub update: Option<BTreeMap<String, String>>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RulesProblem {
    pub table: String,
    pub field: String,
    pub code: String,
    pub message: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Rules {
    pub version: String,
    pub tables: BTreeMap<String, RulesTable>,
    #[serde(default)]
    pub problems: Option<Vec<RulesProblem>>,
    #[serde(default)]
    pub warnings: Option<Vec<RulesProblem>>,
    pub updated_at: String,
    pub updated_by: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RulesVersion {
    pub version: String,
    pub updated_at: String,
    pub updated_by: String,
    pub digest: String,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RulesTestAuth {
    pub uid: String,
    pub claims: Option<BTreeMap<String, Value>>,
    pub org_id: Option<String>,
    pub org_role: Option<String>,
    pub is_anonymous: bool,
}

#[derive(Debug, Clone, Default, Serialize)]
pub struct RulesTestRequest {
    pub operation: String,
    pub table: String,
    pub auth: RulesTestAuth,
    pub row: Option<BTreeMap<String, Value>>,
    pub new: Option<BTreeMap<String, Value>>,
    pub time: Option<i64>,
    #[serde(flatten)]
    pub rules: Option<RulesSet>,
}

#[derive(Debug, Clone, Deserialize)]
pub struct RulesTestValue {
    pub rule: String,
    pub value: bool,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RulesTestResult {
    pub allowed: bool,
    #[serde(default)]
    pub decided_by: String,
    #[serde(default, alias = "condition")]
    pub clause: Option<String>,
    #[serde(default)]
    pub results: Vec<RulesTestValue>,
    #[serde(default)]
    pub columns: Vec<String>,
    pub problems: Vec<RulesProblem>,
}

fn precondition(version: Option<&str>) -> Result<(&'static str, String)> {
    match version {
        None => Ok(("If-None-Match", "*".into())),
        Some(v) if super::is_valid_id(v) => Ok(("If-Match", format!("\"{v}\""))),
        Some(_) => Err(Error::InvalidInput("rules version must be a ULID".into())),
    }
}

impl Database {
    pub async fn get_rules(&self) -> Result<Rules> {
        self.get_json(&["rules"], &[]).await
    }

    pub async fn deploy_rules(
        &self,
        rules: &RulesSet,
        current_version: Option<&str>,
    ) -> Result<Rules> {
        super::ids::check_database_selector(self.name())?;
        let mut req =
            Req::new(Method::PUT, vec!["v1", "databases", self.name(), "rules"]).json(rules)?;
        req.headers.push(precondition(current_version)?);
        self.client()
            .send_project(req)
            .await?
            .decode_status(&[StatusCode::OK])
    }

    pub async fn list_rules_versions(
        &self,
        after: Option<&str>,
        limit: Option<u32>,
    ) -> Result<ManagementPage<RulesVersion>> {
        let mut query = Vec::new();
        if let Some(after) = after {
            query.push(("after", after.to_owned()));
        }
        if let Some(limit) = limit {
            query.push(("limit", limit.to_string()));
        }
        self.get_json(&["rules", "versions"], &query).await
    }

    pub async fn get_rules_version(&self, version: &str) -> Result<Rules> {
        if !super::is_valid_id(version) {
            return Err(Error::InvalidInput("rules version must be a ULID".into()));
        }
        self.get_json(&["rules", "versions", version], &[]).await
    }

    pub async fn restore_rules(
        &self,
        version: &str,
        current_version: Option<&str>,
    ) -> Result<Rules> {
        super::ids::check_database_selector(self.name())?;
        if !super::is_valid_id(version) {
            return Err(Error::InvalidInput("rules version must be a ULID".into()));
        }
        let mut req = Req::new(
            Method::POST,
            vec![
                "v1",
                "databases",
                self.name(),
                "rules",
                "versions",
                version,
                "restore",
            ],
        );
        req.headers.push(precondition(current_version)?);
        self.client()
            .send_project(req)
            .await?
            .decode_status(&[StatusCode::OK])
    }

    pub async fn test_rules(&self, request: &RulesTestRequest) -> Result<RulesTestResult> {
        self.management(
            Method::POST,
            &["rules:test"],
            &[],
            Some(request),
            StatusCode::OK,
        )
        .await
    }
}
