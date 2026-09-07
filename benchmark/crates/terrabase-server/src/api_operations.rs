//! Read-only discovery from the durable provisioning authority journal.

use super::{ApiError, ApiState, Arc, Extension, Response, State, StatusCode};
use axum::{extract::Query, response::IntoResponse};
use serde::{Deserialize, Serialize};

fn member_id(state: &ApiState) -> &str {
    &state.member_id
}

fn export_state(status: &crate::export_api::JobStatus) -> &'static str {
    match status {
        crate::export_api::JobStatus::Accepted => "queued",
        crate::export_api::JobStatus::Running => "running",
        crate::export_api::JobStatus::Cancelled => "cancelled",
        crate::export_api::JobStatus::Complete => "succeeded",
        crate::export_api::JobStatus::Failed => "failed",
    }
}

fn export_phase(phase: crate::export_api::ExportPhase) -> &'static str {
    match phase {
        crate::export_api::ExportPhase::Documents => "documents",
        crate::export_api::ExportPhase::History => "history",
        crate::export_api::ExportPhase::Done => "done",
    }
}

fn recovery_state(phase: &str) -> &'static str {
    match phase {
        "accepted" => "queued",
        "capturing" | "measuring" => "running",
        "complete" => "succeeded",
        "failed" => "failed",
        _ => "running",
    }
}

fn rfc3339_millis(millis: u64) -> Option<String> {
    chrono::DateTime::from_timestamp_millis(i64::try_from(millis).ok()?)
        .map(|time| time.to_rfc3339_opts(chrono::SecondsFormat::Millis, true))
}

/// Versioned state for the operation scan. It is sealed before crossing the
/// API boundary; the plaintext contains only resume positions, never foreign
/// database identities that a caller is not already authorized to inspect.
#[derive(Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub(crate) struct DiscoveryCursor {
    pub version: u8,
    pub phase: String,
    pub member_id: String,
    pub db_after: String,
    pub current_db: Option<String>,
    pub owner_after: String,
}

impl DiscoveryCursor {
    pub(crate) fn seal(
        self,
        runtime: &crate::export_api::ExportRuntime,
        tenant: &str,
        project: &str,
        member: &str,
    ) -> Result<String, crate::export_api::ExportError> {
        if self.version != 1 || self.member_id != member {
            return Err(crate::export_api::ExportError::InvalidRequest("invalid operation cursor"));
        }
        let scope = format!("{tenant}/{project}/{member}");
        let bytes = serde_json::to_vec(&self)
            .map_err(|_| crate::export_api::ExportError::EncryptionUnavailable)?;
        runtime.seal_discovery_cursor(scope.as_bytes(), &bytes)
    }

    pub(crate) fn open(
        runtime: &crate::export_api::ExportRuntime,
        token: &str,
        tenant: &str,
        project: &str,
        member: &str,
    ) -> Result<Self, crate::export_api::ExportError> {
        let scope = format!("{tenant}/{project}/{member}");
        let bytes = runtime.open_discovery_cursor(scope.as_bytes(), token)?;
        let cursor: Self = serde_json::from_slice(&bytes).map_err(|_| {
            crate::export_api::ExportError::InvalidRequest("invalid operation cursor")
        })?;
        if cursor.version != 1 || cursor.member_id != member {
            return Err(crate::export_api::ExportError::InvalidRequest("invalid operation cursor"));
        }
        Ok(cursor)
    }
}

#[derive(Deserialize, utoipa::IntoParams)]
#[serde(deny_unknown_fields)]
#[into_params(parameter_in = Query)]
pub(super) struct OperationListQuery {
    /// Opaque cursor returned by the preceding page, bound to the verified project.
    after: Option<String>,
    /// Maximum records, from one through fifty; defaults to fifty.
    #[param(minimum = 1, maximum = 50)]
    limit: Option<usize>,
}

#[derive(Deserialize, Serialize, utoipa::ToSchema)]
pub(crate) struct OperationPage {
    pub operations: Vec<OperationSummary>,
    #[serde(default)]
    pub next_cursor: Option<String>,
    /// Journal kinds currently covered by this endpoint; additional owners remain explicit.
    pub covered_kinds: Vec<String>,
}

#[derive(Deserialize, Serialize, utoipa::ToSchema)]
pub(crate) struct OperationSummary {
    pub operation_id: String,
    pub kind: String,
    pub database_id: String,
    pub name: String,
    /// Queued, running, or succeeded, derived only from committed owner state.
    pub state: String,
    pub stage: String,
    /// Optional because some owner journals do not persist a creation time.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub created_at: Option<String>,
    /// Durable journal revision; viewing does not advance it.
    /// Optional because member-local journals may not expose a revision.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub revision: Option<u64>,
    pub cancellable: bool,
    pub cancellation_reason: String,
}

#[utoipa::path(get, path = "/api/v1/operations", params(OperationListQuery),
    responses((status = 200, description = "Project-scoped durable operation discovery with explicit journal coverage", body = OperationPage)))]
pub(super) async fn list_operations(
    State(state): State<Arc<ApiState>>,
    scope: Option<Extension<crate::delegated_identity::VerifiedProjectScope>>,
    Query(query): Query<OperationListQuery>,
) -> Response {
    let Some(Extension(scope)) = scope else {
        return ApiError::control_unavailable().into_response();
    };
    let Some(authority) = &state.provisioning else {
        return match list_member_operations(
            &state,
            &scope,
            query.after.as_deref(),
            query.limit.unwrap_or(50),
        ) {
            Ok(page) => crate::export_api::json_response(StatusCode::OK, &page),
            Err(_) => ApiError::control_unavailable().into_response(),
        };
    };
    match authority.list_operations(&scope, query.after.as_deref(), query.limit.unwrap_or(50)).await
    {
        Ok(page) => crate::export_api::json_response(StatusCode::OK, &page),
        Err(error) => super::provisioning_error(error).into_response(),
    }
}

/// Reads the local member journals through the admitted registry. This is an
/// explicitly local projection: fleet completeness belongs to the authority
/// owner and is never inferred from a member page.
fn list_member_operations(
    state: &ApiState,
    scope: &crate::delegated_identity::VerifiedProjectScope,
    after: Option<&str>,
    limit: usize,
) -> Result<OperationPage, ()> {
    if !(1..=50).contains(&limit) {
        return Err(());
    }
    let Some(registry) = &state.admitted_databases else {
        return Err(());
    };
    let Some(exports) = &state.exports else {
        return Ok(OperationPage {
            operations: Vec::new(),
            next_cursor: None,
            covered_kinds: Vec::new(),
        });
    };
    let cursor = match after {
        Some(token) => DiscoveryCursor::open(
            exports,
            token,
            &scope.tenant_id,
            &scope.project_id,
            member_id(&state),
        )
        .map_err(|_| ())?,
        None => DiscoveryCursor {
            version: 1,
            phase: "member".into(),
            member_id: member_id(&state).into(),
            db_after: String::new(),
            current_db: None,
            owner_after: String::new(),
        },
    };
    let (databases, db_next) = registry
        .discovery_page(&scope.tenant_id, &scope.project_id, &cursor.db_after)
        .map_err(|_| ())?;
    let mut operations = Vec::new();
    let mut current_db = cursor.current_db;
    let mut owner_after = cursor.owner_after;
    let mut phase = cursor.phase;
    let scanning_recovery = phase == "recovery";
    for database in databases {
        if current_db.as_deref().is_some_and(|id| id != database.record.database_id) {
            continue;
        }
        current_db = Some(database.record.database_id.clone());
        let rows = if phase == "recovery" {
            Vec::new()
        } else {
            exports
                .discover(
                    &database.record.database_id,
                    (!owner_after.is_empty()).then_some(owner_after.as_str()),
                    limit + 1,
                )
                .map_err(|_| ())?
        };
        for row in rows {
            if operations.len() == limit {
                break;
            }
            owner_after = row.job_id.clone();
            let state_name = export_state(&row.status);
            operations.push(OperationSummary {
                operation_id: row.job_id,
                kind: "export".into(),
                database_id: database.record.database_id.clone(),
                name: database.record.database_id.clone(),
                state: state_name.into(),
                stage: export_phase(row.phase).into(),
                created_at: rfc3339_millis(row.created_at_millis),
                revision: None,
                cancellable: matches!(
                    row.cancellation,
                    crate::export_api::ExportCancellation::Available
                        | crate::export_api::ExportCancellation::Requested
                ),
                cancellation_reason: String::new(),
            });
        }
        let mut recovery_added = false;
        if operations.len() < limit {
            if let Some(recovery) = &state.native_recovery {
                let recovery_rows = recovery
                    .discover(
                        &database.record.database_id,
                        if scanning_recovery { &owner_after } else { "" },
                        limit - operations.len(),
                        false,
                    )
                    .map_err(|_| ())?;
                for row in recovery_rows {
                    if operations.len() == limit {
                        break;
                    }
                    let facts = recovery
                        .status(&database.record.database_id, &row.operation_id)
                        .map_err(|_| ())?
                        .and_then(|operation| operation.facts);
                    owner_after = row.operation_id.clone();
                    recovery_added = true;
                    operations.push(OperationSummary {
                        operation_id: row.operation_id,
                        kind: "recovery".into(),
                        database_id: database.record.database_id.clone(),
                        name: database.record.database_id.clone(),
                        state: recovery_state(&row.phase),
                        stage: row.phase,
                        created_at: facts
                            .as_ref()
                            .map(|facts| facts.captured_at_millis.to_string()),
                        revision: None,
                        cancellable: false,
                        cancellation_reason: "Native recovery has no safe cancellation action"
                            .into(),
                    });
                }
            }
        }
        if operations.len() == limit {
            phase = if scanning_recovery || recovery_added { "recovery" } else { "export" }.into();
            let next = DiscoveryCursor {
                version: 1,
                phase,
                member_id: member_id(&state).into(),
                db_after: cursor.db_after.clone(),
                current_db,
                owner_after,
            };
            return Ok(OperationPage {
                operations,
                next_cursor: Some(
                    next.seal(exports, &scope.tenant_id, &scope.project_id, member_id(&state))
                        .map_err(|_| ())?,
                ),
                covered_kinds: vec!["export".into(), "recovery".into()],
            });
        }
        owner_after.clear();
        current_db = None;
    }
    let next_cursor = db_next
        .map(|db_after| {
            DiscoveryCursor {
                version: 1,
                phase: "member".into(),
                member_id: member_id(&state).into(),
                db_after,
                current_db: None,
                owner_after: String::new(),
            }
            .seal(exports, &scope.tenant_id, &scope.project_id, member_id(&state))
            .map_err(|_| ())
        })
        .transpose()?;
    Ok(OperationPage {
        operations,
        next_cursor,
        covered_kinds: vec!["export".into(), "recovery".into()],
    })
}

#[cfg(test)]
mod tests {

    #[test]
    fn discovery_is_project_administration_and_has_a_typed_paged_contract() {
        assert!(super::super::is_project_administration(
            &axum::http::Method::GET,
            "/api/v1/operations"
        ));
        assert_eq!(
            super::super::required_verb(&axum::http::Method::GET, "/api/v1/operations"),
            crate::api_keys::Verbs::ADMIN
        );
        let document = serde_json::from_str::<serde_json::Value>(&super::super::openapi_json())
            .expect("contract");
        let route = &document["paths"]["/api/v1/operations"]["get"];
        assert_eq!(route["operationId"], "list_operations");
        assert_eq!(
            route["responses"]["200"]["content"]["application/json"]["schema"]["$ref"],
            "#/components/schemas/OperationPage"
        );
        assert!(
            document["components"]["schemas"]["OperationSummary"]["properties"]
                .get("key_version")
                .is_none()
        );
    }
}
