//! `TerraBase` document backed identity persistence. Every write targets the auth authority.
#![allow(clippy::missing_errors_doc)] // All fallible APIs use the documented StoreError boundary.
#![allow(clippy::wildcard_imports)] // Model and store modules intentionally share one record vocabulary.
mod audit_export;
mod methods;
mod query;
mod quota;
pub use audit_export::{AuditVerificationResult, verify_audit_events, verify_audit_export_json};
pub use query::{Predicate, QueryPage, QueryRow, QueryTransport};
pub use quota::QuotaCapTransition;

use auth_contracts::models::*;
use hmac::{Hmac, Mac};
use serde::{Serialize, de::DeserializeOwned};
use serde_json::Value;
use sha2::{Digest, Sha256};
use std::{
    collections::HashSet,
    fmt::Write,
    time::{SystemTime, UNIX_EPOCH},
};
use subtle::ConstantTimeEq;
use terrascale::{
    DatabaseScope, DocumentOperationOptions, ErrorKind, ListOptions, TerraScaleClient,
};
use thiserror::Error;

pub type StoreResult<T> = Result<T, StoreError>;

#[derive(Debug, Error)]
pub enum StoreError {
    #[error(transparent)]
    Sdk(Box<terrascale::Error>),
    #[error(transparent)]
    HttpClient(#[from] reqwest::Error),
    #[error(transparent)]
    Json(#[from] serde_json::Error),
    #[error("TerraBase HTTP {status}: {code}")]
    Http { status: u16, code: String },
    #[error("invalid storage configuration: {0}")]
    InvalidConfiguration(String),
    #[error("storage precondition failed")]
    Precondition,
    #[error("user quota exceeded")]
    QuotaExceeded,
    #[error("storage compare-and-set contention exceeded retry bound")]
    Contention,
    #[error("secure random generation failed: {0}")]
    Entropy(String),
}

impl From<terrascale::Error> for StoreError {
    fn from(error: terrascale::Error) -> Self {
        Self::Sdk(Box::new(error))
    }
}

impl StoreError {
    #[must_use]
    pub fn status(&self) -> Option<u16> {
        match self {
            Self::Sdk(error) => error.status,
            Self::Http { status, .. } => Some(*status),
            Self::Precondition => Some(412),
            _ => None,
        }
    }
    #[must_use]
    pub fn is_not_found(&self) -> bool {
        self.status() == Some(404)
    }
    #[must_use]
    pub fn is_precondition(&self) -> bool {
        self.status() == Some(412)
    }
    #[must_use]
    pub fn is_conflict(&self) -> bool {
        self.status() == Some(409) || self.is_precondition()
    }
}

#[derive(Clone)]
pub struct AuthStore {
    client: TerraScaleClient,
    query: QueryTransport,
    database: String,
    audit_signing_key: Option<Vec<u8>>,
    audit_verification_keys: Vec<Vec<u8>>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct QuotaSeatReconciliation {
    pub claims_examined: usize,
    pub claims_removed: usize,
    pub claims_retained: usize,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct UserAuthenticationCleanupResult {
    pub sessions: usize,
    pub authorization_codes: usize,
    pub refresh_tokens: usize,
    pub mfa_factors: usize,
    pub verification_tokens: usize,
}

#[derive(Debug, Clone)]
pub enum CodeConsumeOutcome {
    Consumed(AuthorizationCode),
    Used(AuthorizationCode),
    Expired,
    Missing,
}

#[derive(Debug, Clone)]
pub enum RefreshRotateOutcome {
    Rotated(RefreshToken),
    Inactive(RefreshToken),
    Expired,
    Missing,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ScimActivationOutcome {
    Activated,
    AlreadyActiveOwned,
    NoLongerEligible,
}

impl AuthStore {
    #[must_use]
    pub fn new(
        client: TerraScaleClient,
        query: QueryTransport,
        database: String,
        audit_signing_key: Option<Vec<u8>>,
        audit_verification_keys: Vec<Vec<u8>>,
    ) -> Self {
        Self {
            client,
            query,
            database,
            audit_signing_key,
            audit_verification_keys,
        }
    }
    #[must_use]
    pub fn database(&self) -> &str {
        &self.database
    }
    fn scope(&self) -> DatabaseScope {
        DatabaseScope::new(&self.database)
    }

    pub async fn get<T: DeserializeOwned>(
        &self,
        collection: &str,
        id: &str,
    ) -> StoreResult<Option<T>> {
        match self
            .client
            .documents()
            .get(&self.scope(), collection, id)
            .await
        {
            Ok(document) => Ok(Some(serde_json::from_value(document.body)?)),
            Err(error) if error.kind == ErrorKind::NotFound => Ok(None),
            Err(error) => Err(error.into()),
        }
    }
    pub async fn get_versioned<T: DeserializeOwned>(
        &self,
        collection: &str,
        id: &str,
    ) -> StoreResult<Option<(T, String)>> {
        match self
            .client
            .documents()
            .get(&self.scope(), collection, id)
            .await
        {
            Ok(document) => {
                let version = document.etag.ok_or_else(|| {
                    StoreError::InvalidConfiguration("TerraBase point read omitted ETag".into())
                })?;
                Ok(Some((serde_json::from_value(document.body)?, version)))
            }
            Err(error) if error.kind == ErrorKind::NotFound => Ok(None),
            Err(error) => Err(error.into()),
        }
    }
    pub async fn create<T: Serialize>(
        &self,
        collection: &str,
        id: &str,
        record: &T,
    ) -> StoreResult<()> {
        self.client
            .documents()
            .put_with_options(
                &self.scope(),
                collection,
                id,
                serde_json::to_value(record)?,
                DocumentOperationOptions::new().with_create_only(),
            )
            .await?;
        Ok(())
    }
    pub async fn upsert<T: Serialize>(
        &self,
        collection: &str,
        id: &str,
        record: &T,
    ) -> StoreResult<()> {
        if matches!(
            collection,
            "organizations" | "scim_directories" | "memberships" | "invitations" | "org_domains"
        ) {
            return Err(StoreError::InvalidConfiguration(
                "lifecycle record writes require validated create or versioned compare-and-put"
                    .into(),
            ));
        }
        self.client
            .documents()
            .put(&self.scope(), collection, id, serde_json::to_value(record)?)
            .await?;
        Ok(())
    }
    pub async fn compare_and_put<T: Serialize>(
        &self,
        collection: &str,
        id: &str,
        record: &T,
        expected_version: &str,
    ) -> StoreResult<bool> {
        if collection == "organizations" {
            let Some((current, current_version)) =
                self.get_versioned::<Organization>(collection, id).await?
            else {
                return Ok(false);
            };
            if current_version.trim_matches('"') != expected_version.trim_matches('"') {
                return Ok(false);
            }
            let next: Organization = serde_json::from_value(serde_json::to_value(record)?)?;
            let allowed = matches!(
                (current.status, next.status),
                (
                    OrganizationStatus::Active,
                    OrganizationStatus::Active | OrganizationStatus::Deleting
                ) | (OrganizationStatus::Deleting, OrganizationStatus::Deleted)
            );
            if !allowed || next.id != current.id || next.instance_id != current.instance_id {
                return Ok(false);
            }
            if next.status != current.status {
                let mut unchanged = next.clone();
                unchanged.status = current.status;
                if serde_json::to_value(&unchanged)? != serde_json::to_value(&current)? {
                    return Ok(false);
                }
            }
        }
        if collection == "scim_directories" {
            let Some((current, current_version)) =
                self.get_versioned::<ScimDirectory>(collection, id).await?
            else {
                return Ok(false);
            };
            if current_version.trim_matches('"') != expected_version.trim_matches('"') {
                return Ok(false);
            }
            let next: ScimDirectory = serde_json::from_value(serde_json::to_value(record)?)?;
            let allowed = matches!(
                (current.status, next.status),
                (
                    ScimDirectoryStatus::Active,
                    ScimDirectoryStatus::Active | ScimDirectoryStatus::Deleting
                ) | (ScimDirectoryStatus::Deleting, ScimDirectoryStatus::Deleted)
            );
            if !allowed
                || next.id != current.id
                || next.instance_id != current.instance_id
                || next.org_id != current.org_id
                || next.instance_wide != current.instance_wide
            {
                return Ok(false);
            }
            if next.status != current.status {
                let mut unchanged = next.clone();
                unchanged.status = current.status;
                if serde_json::to_value(&unchanged)? != serde_json::to_value(&current)? {
                    return Ok(false);
                }
            }
        }
        let etag = if expected_version.starts_with('"') {
            expected_version.to_owned()
        } else {
            format!("\"{expected_version}\"")
        };
        match self
            .client
            .documents()
            .put_with_options(
                &self.scope(),
                collection,
                id,
                serde_json::to_value(record)?,
                DocumentOperationOptions::new().with_if_match(etag),
            )
            .await
        {
            Ok(_) => Ok(true),
            Err(error) if error.status == Some(412) || error.status == Some(404) => Ok(false),
            Err(error) => Err(error.into()),
        }
    }
    pub async fn compare_and_delete(
        &self,
        collection: &str,
        id: &str,
        expected_version: &str,
    ) -> StoreResult<bool> {
        let etag = if expected_version.starts_with('"') {
            expected_version.to_owned()
        } else {
            format!("\"{expected_version}\"")
        };
        match self
            .client
            .documents()
            .delete_with_options(
                &self.scope(),
                collection,
                id,
                DocumentOperationOptions::new().with_if_match(etag),
            )
            .await
        {
            Ok(()) => Ok(true),
            Err(error) if matches!(error.status, Some(404 | 412)) => Ok(false),
            Err(error) => Err(error.into()),
        }
    }
    pub async fn delete(&self, collection: &str, id: &str) -> StoreResult<()> {
        if matches!(
            collection,
            "organizations" | "scim_directories" | "memberships" | "org_domains"
        ) {
            return Err(StoreError::InvalidConfiguration(
                "lifecycle record deletion requires its typed cleanup".into(),
            ));
        }
        self.delete_unchecked(collection, id).await
    }
    async fn delete_unchecked(&self, collection: &str, id: &str) -> StoreResult<()> {
        self.client
            .documents()
            .delete(&self.scope(), collection, id)
            .await?;
        Ok(())
    }
    pub async fn delete_ignoring_missing(&self, collection: &str, id: &str) -> StoreResult<()> {
        match self.delete(collection, id).await {
            Err(error) if error.is_not_found() => Ok(()),
            result => result,
        }
    }
    async fn delete_ignoring_missing_unchecked(
        &self,
        collection: &str,
        id: &str,
    ) -> StoreResult<()> {
        match self.delete_unchecked(collection, id).await {
            Err(error) if error.is_not_found() => Ok(()),
            result => result,
        }
    }
    pub async fn query_page<T: DeserializeOwned>(
        &self,
        collection: &str,
        predicates: &[Predicate],
        limit: u32,
        after: Option<&str>,
    ) -> StoreResult<(Vec<(T, QueryRow)>, Option<String>)> {
        let page = self
            .query
            .query(&self.database, collection, predicates, limit, after)
            .await?;
        let rows = page
            .documents
            .into_iter()
            .map(|row| T::deserialize(&row.value).map(|record| (record, row)))
            .collect::<Result<Vec<_>, _>>()?;
        if page.has_more && page.next_after.is_none() {
            return Err(StoreError::InvalidConfiguration(
                "TerraBase query omitted continuation".into(),
            ));
        }
        Ok((rows, if page.has_more { page.next_after } else { None }))
    }
    pub async fn query_all<T: DeserializeOwned>(
        &self,
        collection: &str,
        predicates: &[Predicate],
        page_size: u32,
    ) -> StoreResult<Vec<T>> {
        let mut all = Vec::new();
        let mut after = None;
        loop {
            let (rows, next): (Vec<(T, QueryRow)>, _) = self
                .query_page(collection, predicates, page_size, after.as_deref())
                .await?;
            all.extend(rows.into_iter().map(|(record, _)| record));
            match next {
                Some(cursor) if after.as_deref() != Some(cursor.as_str()) => after = Some(cursor),
                Some(_) => {
                    return Err(StoreError::InvalidConfiguration(
                        "TerraBase query cursor did not advance".into(),
                    ));
                }
                None => return Ok(all),
            }
        }
    }
    pub async fn query_one<T: DeserializeOwned>(
        &self,
        collection: &str,
        field: &str,
        value: &str,
    ) -> StoreResult<Option<T>> {
        let (rows, _) = self
            .query_page(collection, &[Predicate::equal(field, value)?], 2, None)
            .await?;
        Ok(rows.into_iter().next().map(|(record, _)| record))
    }
    pub async fn list_raw(
        &self,
        collection: &str,
        limit: u32,
        after: Option<&str>,
    ) -> StoreResult<Vec<(String, Value)>> {
        let mut options = ListOptions::new().with_limit(limit);
        if let Some(after) = after {
            options = options.with_after(after);
        }
        let page = self
            .client
            .documents()
            .list(&self.scope(), collection, options)
            .await?;
        Ok(page
            .documents
            .into_iter()
            .map(|item| (item.id, item.value))
            .collect())
    }
    pub async fn claim_unique(&self, kind: &str, key: &str, owner_id: &str) -> StoreResult<()> {
        if kind == "quota-seat" {
            return Err(StoreError::InvalidConfiguration(
                "quota seats must use the quota admission ledger".into(),
            ));
        }
        let id = unique_claim_id(kind, key);
        self.create(
            "uniques",
            &id,
            &serde_json::json!({"ownerType": kind, "ownerId": owner_id}),
        )
        .await
    }
    pub async fn release_unique(&self, kind: &str, key: &str, owner_id: &str) -> StoreResult<()> {
        if kind == "quota-seat" {
            return Err(StoreError::InvalidConfiguration(
                "quota seats must use the quota admission ledger".into(),
            ));
        }
        if self.release_unique_if_owned(kind, key, owner_id).await? {
            return Ok(());
        }
        let id = unique_claim_id(kind, key);
        let claim: Option<Value> = self.get("uniques", &id).await?;
        match claim {
            None => Ok(()),
            Some(claim)
                if claim.get("ownerType").and_then(Value::as_str) == Some(kind)
                    && claim.get("ownerId").and_then(Value::as_str) == Some(owner_id) =>
            {
                Err(StoreError::Contention)
            }
            Some(_) => Err(StoreError::Precondition),
        }
    }
    pub async fn release_unique_if_owned(
        &self,
        kind: &str,
        key: &str,
        owner_id: &str,
    ) -> StoreResult<bool> {
        if kind == "quota-seat" {
            return Err(StoreError::InvalidConfiguration(
                "quota seats must use the quota admission ledger".into(),
            ));
        }
        let id = unique_claim_id(kind, key);
        let Some((claim, version)) = self.get_versioned::<Value>("uniques", &id).await? else {
            return Ok(false);
        };
        if claim.get("ownerType").and_then(Value::as_str) != Some(kind)
            || claim.get("ownerId").and_then(Value::as_str) != Some(owner_id)
        {
            return Ok(false);
        }
        self.compare_and_delete("uniques", &id, &version).await
    }
    pub async fn health(&self) -> StoreResult<terrascale::management::HealthResponse> {
        Ok(self
            .client
            .management()
            .regions()
            .health(terrascale::RegionCode::Gru)
            .await?)
    }
    pub async fn list_users(
        &self,
        instance_id: &str,
        limit: u32,
        after: Option<&str>,
    ) -> StoreResult<(Vec<User>, Option<String>)> {
        let (rows, next) = self
            .query_page::<User>(
                "users",
                &[Predicate::equal("instanceId", instance_id)?],
                limit,
                after,
            )
            .await?;
        let users = rows
            .into_iter()
            .map(|(user, _)| user)
            .filter(|user| user.scim_provisioning.is_none())
            .collect();
        Ok((users, next))
    }
    pub async fn claim_quota_seat(
        &self,
        instance_id: &str,
        quota: u32,
        owner_id: &str,
    ) -> StoreResult<u32> {
        if instance_id.is_empty() || owner_id.is_empty() || quota == 0 {
            return Err(StoreError::InvalidConfiguration(
                "invalid quota claim".into(),
            ));
        }
        let prior_claims = self.quota_claims_for_owner(instance_id, owner_id).await?;
        if prior_claims.len() > 1 {
            return Err(StoreError::Precondition);
        }
        if let Some(prior) = prior_claims.first() {
            // Legacy claims are counted while the gate is first created. A
            // retry for the same owner does not reserve another admission.
            self.read_or_create_quota_ledger(instance_id, quota).await?;
            return prior
                .get("seat")
                .and_then(Value::as_u64)
                .and_then(|seat| u32::try_from(seat).ok())
                .ok_or(StoreError::Precondition);
        }
        let limit = self
            .reserve_quota_admission(instance_id, quota, owner_id)
            .await?;
        let existing = self.quota_claims_for_owner(instance_id, owner_id).await?;
        if existing.len() > 1 {
            return Err(StoreError::Precondition);
        }
        if let Some(claim) = existing.first() {
            return claim
                .get("seat")
                .and_then(Value::as_u64)
                .and_then(|seat| u32::try_from(seat).ok())
                .filter(|seat| *seat < limit)
                .ok_or(StoreError::Precondition);
        }
        for seat in 0..limit {
            let id = unique_claim_id("quota-seat", &format!("{instance_id}:{seat}"));
            let claim = serde_json::json!({"ownerType":"quota-seat","ownerId":owner_id,"instanceId":instance_id,"seat":seat,"state":"pending","createdAt":unix_seconds()});
            match self.create("uniques", &id, &claim).await {
                Ok(()) => return Ok(seat),
                Err(error) => {
                    let observed: Option<Value> = self.get("uniques", &id).await?;
                    if observed.as_ref().is_some_and(|observed| {
                        observed.get("ownerType").and_then(Value::as_str) == Some("quota-seat")
                            && observed.get("ownerId").and_then(Value::as_str) == Some(owner_id)
                            && observed.get("instanceId").and_then(Value::as_str)
                                == Some(instance_id)
                            && observed.get("seat").and_then(Value::as_u64) == Some(u64::from(seat))
                    }) {
                        return Ok(seat);
                    }
                    if error.is_conflict() {
                        continue;
                    }
                    return Err(error);
                }
            }
        }
        self.cancel_quota_admission(instance_id, owner_id).await?;
        Err(StoreError::QuotaExceeded)
    }
    pub async fn release_quota_seat_if_owned(
        &self,
        instance_id: &str,
        seat: u32,
        owner_id: &str,
    ) -> StoreResult<bool> {
        self.release_quota_admission(instance_id, seat, owner_id)
            .await
    }
    pub async fn release_quota_seat_for_user(&self, user: &User) -> StoreResult<()> {
        if let Some(seat) = user.quota_seat {
            let _ = self
                .release_quota_seat_if_owned(&user.instance_id, seat, &user.id)
                .await?;
        }
        Ok(())
    }
    /// Finish uniqueness and quota release after a durable User tombstone.
    /// Retained profile values recover the exact normalized keys even when
    /// rekeying already cleared the active key fields before an error.
    pub async fn release_deleted_user_claims(
        &self,
        instance_id: &str,
        user_id: &str,
    ) -> StoreResult<bool> {
        let Some(user) = self.get::<User>("users", user_id).await? else {
            return Ok(false);
        };
        if user.instance_id != instance_id || !user.deleted || user.scim_provisioning.is_some() {
            return Ok(false);
        }
        let email_key = user.email_key.clone().or_else(|| {
            user.email
                .as_deref()
                .map(|email| format!("{instance_id}:{}", email.trim().to_lowercase()))
        });
        let username_key = user.username_key.clone().or_else(|| {
            user.username
                .as_deref()
                .map(|name| format!("{instance_id}:{}", name.trim().to_lowercase()))
        });
        for (kind, key) in [
            ("user-email", email_key.as_deref()),
            ("user-username", username_key.as_deref()),
        ] {
            if let Some(key) = key {
                self.release_unique_if_owned(kind, key, user_id).await?;
                let claim: Option<Value> = self.get("uniques", &unique_claim_id(kind, key)).await?;
                if claim.is_some_and(|claim| {
                    claim.get("ownerType").and_then(Value::as_str) == Some(kind)
                        && claim.get("ownerId").and_then(Value::as_str) == Some(user_id)
                }) {
                    return Err(StoreError::Contention);
                }
            }
        }
        self.release_quota_seat_for_user(&user).await?;
        if let Some(seat) = user.quota_seat {
            let claim_id = unique_claim_id("quota-seat", &format!("{instance_id}:{seat}"));
            let claim: Option<Value> = self.get("uniques", &claim_id).await?;
            if claim.is_some_and(|claim| {
                claim.get("ownerId").and_then(Value::as_str) == Some(user_id)
                    && claim.get("instanceId").and_then(Value::as_str) == Some(instance_id)
                    && claim.get("seat").and_then(Value::as_u64) == Some(u64::from(seat))
            }) {
                return Err(StoreError::Contention);
            }
        }
        Ok(true)
    }
    async fn activate_quota_seat_for_user(&self, user: &User) -> StoreResult<()> {
        let seat = user.quota_seat.ok_or(StoreError::Precondition)?;
        let id = unique_claim_id("quota-seat", &format!("{}:{seat}", user.instance_id));
        let Some((mut claim, version)) = self.get_versioned::<Value>("uniques", &id).await? else {
            return Err(StoreError::Precondition);
        };
        if claim.get("ownerId").and_then(Value::as_str) != Some(user.id.as_str())
            || claim.get("instanceId").and_then(Value::as_str) != Some(user.instance_id.as_str())
            || claim.get("seat").and_then(Value::as_u64) != Some(u64::from(seat))
        {
            return Err(StoreError::Precondition);
        }
        if claim.get("state").and_then(Value::as_str) == Some("active") {
            return Ok(());
        }
        if claim.get("state").and_then(Value::as_str) != Some("pending") {
            return Err(StoreError::Precondition);
        }
        claim["state"] = Value::String("active".into());
        if self
            .compare_and_put("uniques", &id, &claim, &version)
            .await?
        {
            Ok(())
        } else {
            Err(StoreError::Precondition)
        }
    }
    pub async fn reconcile_quota_seats(
        &self,
        instance_id: &str,
    ) -> StoreResult<QuotaSeatReconciliation> {
        if instance_id.trim().is_empty() {
            return Err(StoreError::InvalidConfiguration(
                "instance ID is required".into(),
            ));
        }
        let predicates = [
            Predicate::equal("ownerType", "quota-seat")?,
            Predicate::equal("instanceId", instance_id)?,
        ];
        let mut claims = Vec::new();
        let mut after = None;
        loop {
            let (rows, next): (Vec<(Value, QueryRow)>, _) = self
                .query_page("uniques", &predicates, 1_000, after.as_deref())
                .await?;
            claims.extend(rows);
            match next {
                Some(cursor) if after.as_deref() != Some(cursor.as_str()) => after = Some(cursor),
                Some(_) => {
                    return Err(StoreError::InvalidConfiguration(
                        "Quota cursor did not advance".into(),
                    ));
                }
                None => break,
            }
        }
        let examined = claims.len();
        let mut removed = 0;
        for (claim, row) in claims {
            let Some((owner, seat)) = claim.get("ownerId").and_then(Value::as_str).zip(
                claim
                    .get("seat")
                    .and_then(Value::as_u64)
                    .and_then(|seat| u32::try_from(seat).ok()),
            ) else {
                continue;
            };
            // Only expired pending reservations can be recovered. A creator must
            // activate its reservation by CAS before writing the user; the CAS
            // races safely with this exact-version delete. Active or legacy
            // claims are retained even when their user is absent.
            let old_pending = claim.get("state").and_then(Value::as_str) == Some("pending")
                && claim
                    .get("createdAt")
                    .and_then(Value::as_i64)
                    .is_some_and(|created| created <= unix_seconds().saturating_sub(900));
            if !old_pending || self.get::<User>("users", owner).await?.is_some() {
                continue;
            }
            let id = unique_claim_id("quota-seat", &format!("{instance_id}:{seat}"));
            if row.id != id {
                continue;
            }
            let result = self
                .client
                .documents()
                .delete_with_options(
                    &self.scope(),
                    "uniques",
                    &id,
                    DocumentOperationOptions::new().with_if_match(format!("\"{}\"", row.version)),
                )
                .await;
            match result {
                Ok(()) => removed += 1,
                Err(error) if matches!(error.status, Some(404 | 412)) => {}
                Err(error) => return Err(error.into()),
            }
        }
        if self
            .get::<Value>("quota_admissions", instance_id)
            .await?
            .is_some()
        {
            self.reconcile_quota_admission(instance_id).await?;
        }
        Ok(QuotaSeatReconciliation {
            claims_examined: examined,
            claims_removed: removed,
            claims_retained: examined - removed,
        })
    }
    pub async fn delete_user_authentication_artifacts(
        &self,
        instance_id: &str,
        user_id: &str,
    ) -> StoreResult<UserAuthenticationCleanupResult> {
        if instance_id.trim().is_empty() || user_id.trim().is_empty() {
            return Err(StoreError::InvalidConfiguration(
                "instance and user IDs are required".into(),
            ));
        }
        let predicates = [
            Predicate::equal("instanceId", instance_id)?,
            Predicate::equal("userId", user_id)?,
        ];
        let sessions: Vec<Session> = self.query_all("sessions", &predicates, 100).await?;
        let codes: Vec<AuthorizationCode> = self.query_all("oauth_codes", &predicates, 100).await?;
        let refresh: Vec<RefreshToken> = self.query_all("refresh_tokens", &predicates, 100).await?;
        let factors: Vec<MfaFactor> = self.query_all("mfa_factors", &predicates, 100).await?;
        let verification: Vec<VerificationToken> = self
            .query_all("verification_tokens", &predicates, 100)
            .await?;
        let counts = UserAuthenticationCleanupResult {
            sessions: sessions.len(),
            authorization_codes: codes.len(),
            refresh_tokens: refresh.len(),
            mfa_factors: factors.len(),
            verification_tokens: verification.len(),
        };
        for record in sessions {
            self.delete_ignoring_missing("sessions", &record.id).await?;
        }
        for record in codes {
            self.delete_ignoring_missing("oauth_codes", &record.id)
                .await?;
        }
        for record in refresh {
            self.delete_ignoring_missing("refresh_tokens", &record.id)
                .await?;
        }
        for record in factors {
            self.delete_ignoring_missing("mfa_factors", &record.id)
                .await?;
        }
        for record in verification {
            self.delete_ignoring_missing("verification_tokens", &record.id)
                .await?;
        }
        Ok(counts)
    }
    pub async fn try_touch_session(
        &self,
        id: &str,
        instance_id: &str,
        last_active_at: i64,
    ) -> StoreResult<bool> {
        let Some((mut session, version)) = self.get_versioned::<Session>("sessions", id).await?
        else {
            return Ok(false);
        };
        if session.instance_id != instance_id || session.status != "active" {
            return Ok(false);
        }
        session.last_active_at = Some(last_active_at);
        self.compare_and_put("sessions", id, &session, &version)
            .await
    }
    pub async fn try_revoke_session(
        &self,
        id: &str,
        instance_id: &str,
        user_id: Option<&str>,
    ) -> StoreResult<bool> {
        for _ in 0..8 {
            let Some((mut session, version)) =
                self.get_versioned::<Session>("sessions", id).await?
            else {
                return Ok(false);
            };
            if session.instance_id != instance_id
                || user_id.is_some_and(|user| session.user_id != user)
                || session.status != "active"
            {
                return Ok(false);
            }
            session.status = "revoked".into();
            if self
                .compare_and_put("sessions", id, &session, &version)
                .await?
            {
                return Ok(true);
            }
        }
        Err(StoreError::Contention)
    }
    /// Preserve only the current browser session across an authenticated
    /// password change after the User credential epoch has advanced.
    pub async fn try_update_session_auth_epoch(
        &self,
        session_id: &str,
        instance_id: &str,
        user_id: &str,
        expected_epoch: u64,
        new_epoch: u64,
    ) -> StoreResult<bool> {
        if new_epoch <= expected_epoch {
            return Err(StoreError::InvalidConfiguration(
                "session authentication epoch must advance".into(),
            ));
        }
        for _ in 0..8 {
            let Some((mut session, version)) = self
                .get_versioned::<Session>("sessions", session_id)
                .await?
            else {
                return Ok(false);
            };
            if session.instance_id != instance_id
                || session.user_id != user_id
                || session.status != "active"
                || session.auth_epoch != expected_epoch
            {
                return Ok(false);
            }
            session.auth_epoch = new_epoch;
            if self
                .compare_and_put("sessions", session_id, &session, &version)
                .await?
            {
                return Ok(true);
            }
        }
        Err(StoreError::Contention)
    }
    pub async fn try_update_user(
        &self,
        id: &str,
        instance_id: &str,
        update: impl Fn(&mut User) -> bool,
    ) -> StoreResult<bool> {
        for _ in 0..8 {
            let Some((mut user, version)) = self.get_versioned::<User>("users", id).await? else {
                return Ok(false);
            };
            if user.instance_id != instance_id || user.deleted {
                return Ok(false);
            }
            let old_email = user.email_key.clone();
            let old_username = user.username_key.clone();
            let quota_seat = user.quota_seat;
            let auth_epoch = user.auth_epoch;
            let established = user.identity_established || user.email_verified;
            let scim_provisioning = user.scim_provisioning.clone();
            let scim_directory_ids = user.scim_instance_wide_directory_ids.clone();
            if !update(&mut user) {
                return Ok(false);
            }
            user.identity_established |= established || user.email_verified;
            if user.email_key != old_email
                || user.username_key != old_username
                || user.instance_id != instance_id
                || user.id != id
                || user.quota_seat != quota_seat
                || user.scim_provisioning != scim_provisioning
                || user.scim_instance_wide_directory_ids != scim_directory_ids
                || user.auth_epoch < auth_epoch
                || user.deleted
            {
                return Err(StoreError::InvalidConfiguration(
                    "user identity or quota seat cannot change through this update".into(),
                ));
            }
            if self.compare_and_put("users", id, &user, &version).await? {
                return Ok(true);
            }
        }
        Err(StoreError::Contention)
    }
    /// Advance the credential/session epoch with a User CAS. This is used
    /// after a separately verified recovery factor is consumed.
    pub async fn advance_user_auth_epoch(
        &self,
        instance_id: &str,
        user_id: &str,
    ) -> StoreResult<Option<u64>> {
        for _ in 0..8 {
            let Some((mut user, version)) = self.get_versioned::<User>("users", user_id).await?
            else {
                return Ok(None);
            };
            if user.instance_id != instance_id || user.deleted || user.scim_provisioning.is_some() {
                return Ok(None);
            }
            user.auth_epoch = user.auth_epoch.checked_add(1).ok_or_else(|| {
                StoreError::InvalidConfiguration("user authentication epoch exhausted".into())
            })?;
            if self
                .compare_and_put("users", user_id, &user, &version)
                .await?
            {
                return Ok(Some(user.auth_epoch));
            }
        }
        Err(StoreError::Contention)
    }
    /// Advance exactly the epoch proved by the caller. A stale recovery proof
    /// cannot revoke a newer authentication state.
    pub async fn advance_user_auth_epoch_if_current(
        &self,
        instance_id: &str,
        user_id: &str,
        expected_epoch: u64,
    ) -> StoreResult<Option<u64>> {
        for _ in 0..8 {
            let Some((mut user, version)) = self.get_versioned::<User>("users", user_id).await?
            else {
                return Ok(None);
            };
            if user.instance_id != instance_id
                || user.deleted
                || user.scim_provisioning.is_some()
                || user.auth_epoch != expected_epoch
            {
                return Ok(None);
            }
            user.auth_epoch = expected_epoch.checked_add(1).ok_or_else(|| {
                StoreError::InvalidConfiguration("user authentication epoch exhausted".into())
            })?;
            if self
                .compare_and_put("users", user_id, &user, &version)
                .await?
            {
                return Ok(Some(user.auth_epoch));
            }
        }
        Err(StoreError::Contention)
    }
    pub async fn rekey_user(
        &self,
        id: &str,
        instance_id: &str,
        new_email_key: Option<String>,
        new_username_key: Option<String>,
        update: impl Fn(&mut User),
    ) -> StoreResult<bool> {
        for _ in 0..8 {
            let Some((mut user, version)) = self.get_versioned::<User>("users", id).await? else {
                return Ok(false);
            };
            if user.instance_id != instance_id || user.deleted {
                return Ok(false);
            }
            let old_email = user.email_key.clone();
            let old_username = user.username_key.clone();
            let quota_seat = user.quota_seat;
            let auth_epoch = user.auth_epoch;
            let established = user.identity_established || user.email_verified;
            let scim_provisioning = user.scim_provisioning.clone();
            let scim_directory_ids = user.scim_instance_wide_directory_ids.clone();
            update(&mut user);
            user.identity_established |= established || user.email_verified;
            let deleting = user.deleted && new_email_key.is_none() && new_username_key.is_none();
            if user.id != id
                || user.instance_id != instance_id
                || user.quota_seat != quota_seat
                || user.scim_provisioning != scim_provisioning
                || user.scim_instance_wide_directory_ids != scim_directory_ids
                || user.auth_epoch < auth_epoch
                || (user.deleted && !deleting)
            {
                return Err(StoreError::InvalidConfiguration(
                    "user identity or quota seat cannot change".into(),
                ));
            }
            user.email_key = new_email_key.clone();
            user.username_key = new_username_key.clone();
            let claim_email = new_email_key
                .as_ref()
                .filter(|key| old_email.as_ref() != Some(key));
            let claim_username = new_username_key
                .as_ref()
                .filter(|key| old_username.as_ref() != Some(key));
            if let Some(key) = claim_email {
                self.claim_unique("user-email", key, id).await?;
            }
            if let Some(key) = claim_username
                && let Err(error) = self.claim_unique("user-username", key, id).await
            {
                if let Some(email) = claim_email {
                    self.release_unique_if_owned("user-email", email, id)
                        .await?;
                }
                return Err(error);
            }
            let write = self.compare_and_put("users", id, &user, &version).await;
            if matches!(write, Ok(false)) {
                if let Some(email) = claim_email {
                    self.release_unique_if_owned("user-email", email, id)
                        .await?;
                }
                if let Some(username) = claim_username {
                    self.release_unique_if_owned("user-username", username, id)
                        .await?;
                }
            }
            match write {
                Ok(true) => {
                    if let Some(old) = old_email.filter(|old| new_email_key.as_ref() != Some(old)) {
                        self.release_unique_if_owned("user-email", &old, id).await?;
                    }
                    if let Some(old) =
                        old_username.filter(|old| new_username_key.as_ref() != Some(old))
                    {
                        self.release_unique_if_owned("user-username", &old, id)
                            .await?;
                    }
                    return Ok(true);
                }
                Ok(false) => {}
                Err(error) => return Err(error),
            }
        }
        Err(StoreError::Contention)
    }

    /// Convert a guest only at its proven credential epoch. The User CAS makes
    /// two competing upgrades mutually exclusive. Retain an email reservation
    /// on an uncertain/losing write: releasing an owner-shared claim here could
    /// release a concurrent successful conversion's uniqueness protection.
    pub async fn upgrade_anonymous_user(
        &self,
        id: &str,
        instance_id: &str,
        expected_epoch: u64,
        email: &str,
        password_hash: Option<String>,
        now: i64,
    ) -> StoreResult<Option<User>> {
        let Some((mut user, version)) = self.get_versioned::<User>("users", id).await? else {
            return Ok(None);
        };
        if user.instance_id != instance_id
            || user.deleted
            || user.banned
            || !user.is_anonymous
            || user.auth_epoch != expected_epoch
            || user.email.is_some()
            || user.email_key.is_some()
            || user.username.is_some()
            || user.username_key.is_some()
            || user.scim_provisioning.is_some()
            || !user.scim_instance_wide_directory_ids.is_empty()
        {
            return Ok(None);
        }
        let next_epoch = expected_epoch
            .checked_add(1)
            .ok_or(StoreError::Contention)?;
        let email_key = format!("{instance_id}:{email}");
        self.claim_unique("user-email", &email_key, id).await?;
        user.email = Some(email.to_owned());
        user.email_key = Some(email_key);
        user.email_verified = true;
        user.identity_established = true;
        user.is_anonymous = false;
        user.password_hash = password_hash;
        user.auth_epoch = next_epoch;
        user.updated_at = now;
        if self.compare_and_put("users", id, &user, &version).await? {
            Ok(Some(user))
        } else {
            Ok(None)
        }
    }
    /// Tombstone only the unverified owner of a mailbox being claimed by a
    /// verified identity. The User CAS prevents a stale read from deleting an
    /// account that was verified or rekeyed in the meantime.
    pub async fn deprovision_unverified_user_for_verified_claim(
        &self,
        instance_id: &str,
        user_id: &str,
        email_snapshot: &str,
    ) -> StoreResult<bool> {
        self.deprovision_unverified_user_for_verified_claim_if_epoch(
            instance_id,
            user_id,
            email_snapshot,
            None,
        )
        .await
    }
    /// A proof tied to an authentication epoch cannot replace a user after
    /// credential recovery has invalidated that proof.
    pub async fn deprovision_unverified_user_for_verified_claim_if_epoch(
        &self,
        instance_id: &str,
        user_id: &str,
        email_snapshot: &str,
        expected_auth_epoch: Option<u64>,
    ) -> StoreResult<bool> {
        let email = email_snapshot.trim().to_lowercase();
        let expected_key = format!("{instance_id}:{email}");
        for _ in 0..8 {
            let Some((mut user, version)) = self.get_versioned::<User>("users", user_id).await?
            else {
                return Ok(false);
            };
            if user.instance_id != instance_id
                || user.banned
                || user.email_verified
                || user.identity_established
                || user.scim_provisioning.is_some()
                || expected_auth_epoch.is_some_and(|expected| user.auth_epoch != expected)
                || !user
                    .email
                    .as_deref()
                    .is_some_and(|value| value.trim().to_lowercase() == email)
                || (!user.deleted && user.email_key.as_deref() != Some(expected_key.as_str()))
            {
                return Ok(false);
            }
            if user.deleted {
                return self.release_deleted_user_claims(instance_id, user_id).await;
            }
            user.deleted = true;
            user.email_key = None;
            user.username_key = None;
            user.updated_at = unix_seconds();
            match self
                .compare_and_put("users", user_id, &user, &version)
                .await
            {
                Ok(true) => return self.release_deleted_user_claims(instance_id, user_id).await,
                Ok(false) => {}
                Err(error) => return Err(error),
            }
        }
        Err(StoreError::Contention)
    }
    pub async fn try_transition_invitation(
        &self,
        id: &str,
        from_status: &str,
        to_status: &str,
    ) -> StoreResult<Option<Invitation>> {
        let Some((mut invitation, version)) =
            self.get_versioned::<Invitation>("invitations", id).await?
        else {
            return Ok(None);
        };
        if invitation.status != from_status {
            return Ok(None);
        }
        invitation.status = to_status.to_owned();
        if self
            .compare_and_put("invitations", id, &invitation, &version)
            .await?
        {
            Ok(Some(invitation))
        } else {
            Ok(None)
        }
    }
    pub async fn consume_verification_token(
        &self,
        token_key: &str,
        eligible: impl Fn(&VerificationToken) -> bool,
    ) -> StoreResult<Option<VerificationToken>> {
        let predicate = Predicate::equal("tokenKey", token_key)?;
        let mut after = None;
        loop {
            let (rows, next): (Vec<(VerificationToken, QueryRow)>, _) = self
                .query_page(
                    "verification_tokens",
                    std::slice::from_ref(&predicate),
                    100,
                    after.as_deref(),
                )
                .await?;
            for (mut record, row) in rows {
                if !eligible(&record) {
                    continue;
                }
                let prior = record.clone();
                record.consumed = true;
                return if self
                    .compare_and_put(
                        "verification_tokens",
                        &record.id,
                        &record,
                        &row.version.to_string(),
                    )
                    .await?
                {
                    Ok(Some(prior))
                } else {
                    Ok(None)
                };
            }
            match next {
                Some(cursor) if after.as_deref() != Some(cursor.as_str()) => {
                    after = Some(cursor);
                }
                Some(_) => {
                    return Err(StoreError::InvalidConfiguration(
                        "Token cursor did not advance".into(),
                    ));
                }
                None => return Ok(None),
            }
        }
    }
    /// Find a token within its caller-defined scope before selecting a row.
    /// The caller may include instance, purpose, user, expiry, and consumption
    /// rules; a colliding token key outside that scope cannot shadow it.
    pub async fn find_verification_token_matching(
        &self,
        token_key: &str,
        eligible: impl Fn(&VerificationToken) -> bool,
    ) -> StoreResult<Option<VerificationToken>> {
        let predicate = Predicate::equal("tokenKey", token_key)?;
        let mut after = None;
        loop {
            let (rows, next): (Vec<(VerificationToken, QueryRow)>, _) = self
                .query_page(
                    "verification_tokens",
                    std::slice::from_ref(&predicate),
                    100,
                    after.as_deref(),
                )
                .await?;
            if let Some((record, _)) = rows.into_iter().find(|(record, _)| eligible(record)) {
                return Ok(Some(record));
            }
            match next {
                Some(cursor) if after.as_deref() != Some(cursor.as_str()) => {
                    after = Some(cursor);
                }
                Some(_) => {
                    return Err(StoreError::InvalidConfiguration(
                        "Token cursor did not advance".into(),
                    ));
                }
                None => return Ok(None),
            }
        }
    }
    pub async fn record_verification_failure(
        &self,
        instance_id: &str,
        user_id: &str,
        purpose: &str,
    ) -> StoreResult<i32> {
        if instance_id.trim().is_empty() || user_id.trim().is_empty() || purpose.trim().is_empty() {
            return Err(StoreError::InvalidConfiguration(
                "token scope is required".into(),
            ));
        }
        let predicates = [
            Predicate::equal("instanceId", instance_id)?,
            Predicate::equal("userId", user_id)?,
            Predicate::equal("purpose", purpose)?,
        ];
        for _ in 0..8 {
            let mut latest: Option<(VerificationToken, QueryRow)> = None;
            let mut after = None;
            let now = unix_seconds();
            loop {
                let (rows, next): (Vec<(VerificationToken, QueryRow)>, _) = self
                    .query_page("verification_tokens", &predicates, 100, after.as_deref())
                    .await?;
                for (token, row) in rows {
                    if token.expires_at > now
                        && !token.consumed
                        && latest
                            .as_ref()
                            .is_none_or(|(prior, _)| token.created_at > prior.created_at)
                    {
                        latest = Some((token, row));
                    }
                }
                match next {
                    Some(cursor) if after.as_deref() != Some(cursor.as_str()) => {
                        after = Some(cursor);
                    }
                    Some(_) => {
                        return Err(StoreError::InvalidConfiguration(
                            "Token cursor did not advance".into(),
                        ));
                    }
                    None => break,
                }
            }
            let Some((mut token, row)) = latest else {
                return Ok(0);
            };
            token.failed_attempts = token
                .failed_attempts
                .checked_add(1)
                .ok_or(StoreError::Contention)?;
            if self
                .compare_and_put(
                    "verification_tokens",
                    &token.id,
                    &token,
                    &row.version.to_string(),
                )
                .await?
            {
                return Ok(token.failed_attempts);
            }
        }
        Err(StoreError::Contention)
    }
    pub async fn record_verification_token_failure(
        &self,
        token_key: &str,
        max_failures: i32,
    ) -> StoreResult<bool> {
        if max_failures <= 0 {
            return Err(StoreError::InvalidConfiguration(
                "positive failure cap required".into(),
            ));
        }
        for _ in 0..8 {
            let (rows, _) = self
                .query_page::<VerificationToken>(
                    "verification_tokens",
                    &[Predicate::equal("tokenKey", token_key)?],
                    2,
                    None,
                )
                .await?;
            let Some((mut token, row)) = rows.into_iter().next() else {
                return Ok(false);
            };
            if token.consumed
                || token.expires_at <= unix_seconds()
                || token.failed_attempts >= max_failures
            {
                return Ok(false);
            }
            token.failed_attempts += 1;
            if token.failed_attempts >= max_failures {
                token.consumed = true;
            }
            if self
                .compare_and_put(
                    "verification_tokens",
                    &token.id,
                    &token,
                    &row.version.to_string(),
                )
                .await?
            {
                return Ok(true);
            }
        }
        Err(StoreError::Contention)
    }
    pub async fn invalidate_verification_tokens(
        &self,
        user_id: &str,
        purpose: &str,
    ) -> StoreResult<()> {
        let predicates = [
            Predicate::equal("userId", user_id)?,
            Predicate::equal("purpose", purpose)?,
        ];
        let tokens: Vec<VerificationToken> = self
            .query_all("verification_tokens", &predicates, 100)
            .await?;
        for token in tokens {
            if !token.consumed {
                let _ = self
                    .consume_verification_token(&token.token_key, |current| {
                        current.id == token.id && !current.consumed
                    })
                    .await?;
            }
        }
        Ok(())
    }
    pub async fn consume_recovery_code(
        &self,
        factor_id: &str,
        candidate_hash: &str,
    ) -> StoreResult<bool> {
        let Some((mut factor, version)) = self
            .get_versioned::<MfaFactor>("mfa_factors", factor_id)
            .await?
        else {
            return Ok(false);
        };
        let Some(codes) = &mut factor.recovery_code_hashes else {
            return Ok(false);
        };
        let prior_len = codes.len();
        codes.retain(|code| code != candidate_hash);
        if codes.len() == prior_len {
            return Ok(false);
        }
        self.compare_and_put("mfa_factors", factor_id, &factor, &version)
            .await
    }
    pub async fn try_update_mfa_factor(
        &self,
        id: &str,
        instance_id: &str,
        user_id: &str,
        update: impl Fn(&mut MfaFactor) -> bool,
    ) -> StoreResult<bool> {
        for _ in 0..8 {
            let Some((mut factor, version)) =
                self.get_versioned::<MfaFactor>("mfa_factors", id).await?
            else {
                return Ok(false);
            };
            if factor.instance_id != instance_id
                || factor.user_id != user_id
                || !update(&mut factor)
            {
                return Ok(false);
            }
            if self
                .compare_and_put("mfa_factors", id, &factor, &version)
                .await?
            {
                return Ok(true);
            }
        }
        Err(StoreError::Contention)
    }
    pub async fn try_accept_totp_step(
        &self,
        id: &str,
        instance_id: &str,
        user_id: &str,
        step: i64,
    ) -> StoreResult<bool> {
        self.try_update_mfa_factor(id, instance_id, user_id, |factor| {
            if !factor.confirmed
                || factor.r#type != "totp"
                || factor.last_used_totp_step.is_some_and(|last| step <= last)
            {
                return false;
            }
            factor.last_used_totp_step = Some(step);
            true
        })
        .await
    }
    pub async fn try_delete_mfa_factor(
        &self,
        id: &str,
        instance_id: &str,
        user_id: &str,
    ) -> StoreResult<bool> {
        let Some((factor, version)) = self.get_versioned::<MfaFactor>("mfa_factors", id).await?
        else {
            return Ok(false);
        };
        if factor.instance_id != instance_id || factor.user_id != user_id {
            return Ok(false);
        }
        match self
            .client
            .documents()
            .delete_with_options(
                &self.scope(),
                "mfa_factors",
                id,
                DocumentOperationOptions::new().with_if_match(version),
            )
            .await
        {
            Ok(()) => Ok(true),
            Err(error) if matches!(error.status, Some(404 | 412)) => Ok(false),
            Err(error) => Err(error.into()),
        }
    }
    pub async fn consume_authorization_code(
        &self,
        code_key: &str,
        instance_id: &str,
        client_id: &str,
        now: i64,
    ) -> StoreResult<CodeConsumeOutcome> {
        let (rows, _) = self
            .query_page::<AuthorizationCode>(
                "oauth_codes",
                &[Predicate::equal("codeKey", code_key)?],
                2,
                None,
            )
            .await?;
        let Some((mut code, row)) = rows.into_iter().next() else {
            return Ok(CodeConsumeOutcome::Missing);
        };
        if code.instance_id != instance_id || code.client_id != client_id {
            return Ok(CodeConsumeOutcome::Missing);
        }
        if code.consumed {
            return Ok(CodeConsumeOutcome::Used(code));
        }
        if code.expires_at <= now {
            return Ok(CodeConsumeOutcome::Expired);
        }
        let prior = code.clone();
        code.consumed = true;
        if self
            .compare_and_put("oauth_codes", &code.id, &code, &row.version.to_string())
            .await?
        {
            Ok(CodeConsumeOutcome::Consumed(prior))
        } else {
            Ok(CodeConsumeOutcome::Used(prior))
        }
    }
    pub async fn rotate_refresh_token(
        &self,
        token_key: &str,
        instance_id: &str,
        client_id: &str,
        now: i64,
        rotated_to: &str,
    ) -> StoreResult<RefreshRotateOutcome> {
        let (rows, _) = self
            .query_page::<RefreshToken>(
                "refresh_tokens",
                &[Predicate::equal("tokenKey", token_key)?],
                2,
                None,
            )
            .await?;
        let Some((mut token, row)) = rows.into_iter().next() else {
            return Ok(RefreshRotateOutcome::Missing);
        };
        if token.instance_id != instance_id || token.client_id != client_id {
            return Ok(RefreshRotateOutcome::Missing);
        }
        if token.family_id.is_empty()
            || self
                .is_refresh_family_revoked(instance_id, client_id, &token.family_id)
                .await?
        {
            return Ok(RefreshRotateOutcome::Inactive(token));
        }
        if token.status != "active" {
            return Ok(RefreshRotateOutcome::Inactive(token));
        }
        if token.expires_at <= now {
            return Ok(RefreshRotateOutcome::Expired);
        }
        let prior = token.clone();
        token.status = "rotated".into();
        token.rotated_to = Some(rotated_to.to_owned());
        if self
            .compare_and_put(
                "refresh_tokens",
                &token.id,
                &token,
                &row.version.to_string(),
            )
            .await?
        {
            if self
                .is_refresh_family_revoked(instance_id, client_id, &token.family_id)
                .await?
            {
                Ok(RefreshRotateOutcome::Inactive(prior))
            } else {
                Ok(RefreshRotateOutcome::Rotated(prior))
            }
        } else {
            Ok(RefreshRotateOutcome::Inactive(prior))
        }
    }
    pub async fn revoke_refresh_family(
        &self,
        instance_id: &str,
        client_id: &str,
        family_id: &str,
    ) -> StoreResult<()> {
        if instance_id.is_empty() || client_id.is_empty() || family_id.is_empty() {
            return Err(StoreError::InvalidConfiguration(
                "refresh family scope is required".into(),
            ));
        }
        let id = refresh_family_marker_id(instance_id, client_id, family_id);
        let marker = serde_json::json!({"instanceId":instance_id,"clientId":client_id,"familyId":family_id,"revokedAt":unix_seconds()});
        match self
            .create("refresh_family_revocations", &id, &marker)
            .await
        {
            Ok(()) => Ok(()),
            Err(error) if error.is_conflict() => Ok(()),
            Err(error) => Err(error),
        }
    }
    pub async fn is_refresh_family_revoked(
        &self,
        instance_id: &str,
        client_id: &str,
        family_id: &str,
    ) -> StoreResult<bool> {
        if instance_id.is_empty() || client_id.is_empty() || family_id.is_empty() {
            return Err(StoreError::InvalidConfiguration(
                "refresh family scope is required".into(),
            ));
        }
        Ok(self
            .get::<Value>(
                "refresh_family_revocations",
                &refresh_family_marker_id(instance_id, client_id, family_id),
            )
            .await?
            .is_some())
    }
    pub async fn advance_passkey_sign_count(
        &self,
        passkey_id: &str,
        expected: u32,
        new: u32,
        last_used_at: i64,
    ) -> StoreResult<bool> {
        let Some((mut passkey, version)) = self
            .get_versioned::<Passkey>("passkeys", passkey_id)
            .await?
        else {
            return Ok(false);
        };
        if passkey.sign_count != expected || (expected != 0 && new <= expected) {
            return Ok(false);
        }
        passkey.sign_count = new;
        passkey.last_used_at = Some(last_used_at);
        self.compare_and_put("passkeys", passkey_id, &passkey, &version)
            .await
    }
    pub async fn list_webhook_instance_ids(&self) -> StoreResult<Vec<String>> {
        let mut ids = HashSet::new();
        let mut after = None;
        loop {
            let rows = self.list_raw("webhooks", 100, after.as_deref()).await?;
            if rows.is_empty() {
                break;
            }
            for (_, value) in &rows {
                ids.insert(serde_json::from_value::<WebhookEndpoint>(value.clone())?.instance_id);
            }
            let cursor = rows.last().map(|(id, _)| id.clone());
            if cursor == after {
                return Err(StoreError::InvalidConfiguration(
                    "Webhook cursor did not advance".into(),
                ));
            }
            after = cursor;
        }
        Ok(ids.into_iter().collect())
    }
    pub async fn try_link_stripe_customer(
        &self,
        instance_id: &str,
        customer_id: &str,
    ) -> StoreResult<bool> {
        if customer_id.trim().is_empty() {
            return Err(StoreError::InvalidConfiguration(
                "Stripe customer ID is required".into(),
            ));
        }
        self.try_update_instance(instance_id, |instance| {
            if instance
                .stripe_customer_id
                .as_deref()
                .is_some_and(|current| current != customer_id)
            {
                return false;
            }
            instance.stripe_customer_id = Some(customer_id.to_owned());
            true
        })
        .await
    }
    pub async fn try_update_instance(
        &self,
        id: &str,
        update: impl Fn(&mut Instance) -> bool,
    ) -> StoreResult<bool> {
        for _ in 0..8 {
            let Some((mut instance, version)) =
                self.get_versioned::<Instance>("instances", id).await?
            else {
                return Ok(false);
            };
            if !update(&mut instance) {
                return Ok(false);
            }
            if self
                .compare_and_put("instances", id, &instance, &version)
                .await?
            {
                return Ok(true);
            }
        }
        Err(StoreError::Contention)
    }
    pub async fn try_claim_webhook_delivery(
        &self,
        id: &str,
        now: i64,
        lease_seconds: i64,
    ) -> StoreResult<Option<WebhookDelivery>> {
        if lease_seconds <= 0 {
            return Err(StoreError::InvalidConfiguration(
                "Webhook lease must be positive".into(),
            ));
        }
        for _ in 0..8 {
            let Some((mut delivery, version)) = self
                .get_versioned::<WebhookDelivery>("webhook_deliveries", id)
                .await?
            else {
                return Ok(None);
            };
            if delivery.status != "pending"
                || delivery.next_attempt_at.is_some_and(|due| due > now)
                || delivery.lease_until.is_some_and(|until| until > now)
            {
                return Ok(None);
            }
            let mut bytes = [0_u8; 24];
            getrandom::fill(&mut bytes).map_err(|error| StoreError::Entropy(error.to_string()))?;
            delivery.lease_token = Some(hex_lower(&bytes));
            delivery.lease_until = Some(now.saturating_add(lease_seconds));
            delivery.attempts = delivery
                .attempts
                .checked_add(1)
                .ok_or(StoreError::Contention)?;
            delivery.updated_at = now;
            if self
                .compare_and_put("webhook_deliveries", id, &delivery, &version)
                .await?
            {
                return Ok(Some(delivery));
            }
        }
        Err(StoreError::Contention)
    }
    pub async fn try_update_webhook_delivery(
        &self,
        id: &str,
        update: impl Fn(&mut WebhookDelivery) -> bool,
    ) -> StoreResult<bool> {
        for _ in 0..8 {
            let Some((mut delivery, version)) = self
                .get_versioned::<WebhookDelivery>("webhook_deliveries", id)
                .await?
            else {
                return Ok(false);
            };
            if !update(&mut delivery) {
                return Ok(false);
            }
            if self
                .compare_and_put("webhook_deliveries", id, &delivery, &version)
                .await?
            {
                return Ok(true);
            }
        }
        Err(StoreError::Contention)
    }
    #[must_use]
    pub fn has_valid_audit_integrity(&self, event: &AuditEvent) -> bool {
        event.integrity_version == Some(2)
            && event.integrity_hash.as_ref().is_some_and(|hash| {
                audit_hash(event).is_ok_and(|expected| constant_time_equal(hash, &expected))
            })
    }
    #[must_use]
    pub fn has_authentic_audit_integrity(&self, event: &AuditEvent) -> bool {
        self.audit_signing_key
            .as_ref()
            .is_some_and(|key| Self::audit_integrity_for_key(event, key))
            || self
                .audit_verification_keys
                .iter()
                .any(|key| Self::audit_integrity_for_key(event, key))
    }
    fn audit_integrity_for_key(event: &AuditEvent, key: &[u8]) -> bool {
        if event.integrity_version != Some(2) {
            return false;
        }
        let Some(hash) = &event.integrity_hash else {
            return false;
        };
        let Some(signature) = &event.integrity_signature else {
            return false;
        };
        let Ok(expected_hash) = audit_hash(event) else {
            return false;
        };
        if !constant_time_equal(hash, &expected_hash) {
            return false;
        }
        if event
            .integrity_key_fingerprint
            .as_deref()
            .is_some_and(|fingerprint| {
                !constant_time_equal(fingerprint, &format!("{:x}", Sha256::digest(key)))
            })
        {
            return false;
        }
        let expected_signature = audit_signature(key, hash);
        constant_time_equal(signature, &expected_signature)
    }
}

#[must_use]
pub fn unique_claim_id(kind: &str, key: &str) -> String {
    format!("{kind}:{:x}", Sha256::digest(key.as_bytes()))
}

fn refresh_family_marker_id(instance_id: &str, client_id: &str, family_id: &str) -> String {
    unique_claim_id(
        "refresh-family",
        &format!("{instance_id}:{client_id}:{family_id}"),
    )
}

fn unix_seconds() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |duration| {
            i64::try_from(duration.as_secs()).unwrap_or(i64::MAX)
        })
}

fn audit_hash(event: &AuditEvent) -> StoreResult<String> {
    fn canonicalize(value: &Value) -> StoreResult<Value> {
        match value {
            Value::Object(object) => {
                let ordered: std::collections::BTreeMap<String, Value> = object
                    .iter()
                    .map(|(key, value)| Ok((key.clone(), canonicalize(value)?)))
                    .collect::<StoreResult<_>>()?;
                Ok(serde_json::to_value(ordered)?)
            }
            Value::Array(values) => values
                .iter()
                .map(canonicalize)
                .collect::<StoreResult<Vec<_>>>()
                .map(Value::Array),
            value => Ok(value.clone()),
        }
    }

    fn append_frame(output: &mut Vec<u8>, value: &[u8]) -> StoreResult<()> {
        let length = u64::try_from(value.len()).map_err(|_| {
            StoreError::InvalidConfiguration("audit field exceeds supported length".into())
        })?;
        output.extend_from_slice(&length.to_be_bytes());
        output.extend_from_slice(value);
        Ok(())
    }

    let canonical_data = serde_json::to_vec(&canonicalize(&Value::Object(event.data.clone()))?)?;
    let mut canonical = b"terrascale-auth-audit-event-v2\0".to_vec();
    append_frame(&mut canonical, event.id.as_bytes())?;
    append_frame(&mut canonical, event.instance_id.as_bytes())?;
    append_frame(&mut canonical, event.r#type.as_bytes())?;
    match event.actor_id.as_deref() {
        Some(actor_id) => {
            canonical.push(1);
            append_frame(&mut canonical, actor_id.as_bytes())?;
        }
        None => canonical.push(0),
    }
    canonical.extend_from_slice(&event.created_at.to_be_bytes());
    append_frame(&mut canonical, &canonical_data)?;
    Ok(format!("{:x}", Sha256::digest(&canonical)))
}

fn audit_signature(key: &[u8], hash: &str) -> String {
    let mut mac = Hmac::<Sha256>::new_from_slice(key).expect("HMAC accepts arbitrary key lengths");
    mac.update(format!("v2:{hash}").as_bytes());
    let result = mac.finalize().into_bytes();
    hex_lower(&result)
}

fn hex_lower(bytes: &[u8]) -> String {
    let mut output = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        write!(&mut output, "{byte:02x}").expect("string writes cannot fail");
    }
    output
}

fn constant_time_equal(left: &str, right: &str) -> bool {
    left.len() == right.len() && bool::from(left.as_bytes().ct_eq(right.as_bytes()))
}
