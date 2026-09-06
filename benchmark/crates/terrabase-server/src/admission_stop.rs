//! Exact member-bound deletion stop commands on the authenticated native socket.
use super::*;

/// A durable authority stop command for one admitted member.
#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
pub struct StopRequest {
    /// Deployment identity.
    pub environment: String,
    /// Organization owning the admitted database.
    pub organization_id: String,
    /// Owning project.
    pub project_id: String,
    /// Exact admitted database identity.
    pub database_id: String,
    /// Parent identity for a branch; never substitutes its own identity.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub parent_database_id: Option<String>,
    /// Durable deletion operation identity.
    pub operation_id: String,
    /// Region whose member is being stopped.
    pub region: String,
    /// Exact member acknowledging the stop.
    pub member_id: String,
    /// RFC3339 UTC cutoff with millisecond precision.
    pub cutoff: String,
    /// Authority-selected retention boundary.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub retain_until: Option<String>,
}

impl StopRequest {
    /// Parses the exact millisecond boundary; submillisecond truncation is refused.
    pub fn cutoff_millis(&self) -> Result<u64, String> {
        let timestamp = chrono::DateTime::parse_from_rfc3339(&self.cutoff)
            .map_err(|_| "invalid deletion cutoff")?;
        if timestamp.timestamp_subsec_nanos() % 1_000_000 != 0 {
            return Err("deletion cutoff requires millisecond precision".into());
        }
        u64::try_from(timestamp.timestamp_millis()).map_err(|_| "invalid deletion cutoff".into())
    }
}

/// A member acknowledgment; the authority must collect every replica separately.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct StopReceipt {
    /// Exact command acknowledged.
    #[serde(flatten)]
    pub request: StopRequest,
    /// Public access is persistently revoked.
    pub access_revoked: bool,
    /// Customer usage minting is persistently stopped at the cutoff.
    pub usage_stopped: bool,
}

/// Callback must durably fence mutations and access before returning success.
pub type StorageStop =
    Arc<dyn Fn(&AdmissionRecord, &StopRequest) -> Result<(), String> + Send + Sync>;

impl AdmissionHost {
    /// Installs the native durable stop implementation; absence refuses every stop.
    pub fn with_storage_stop(mut self, stop: StorageStop) -> Self {
        self.stop_storage = Some(stop);
        self
    }

    /// Validates immutable membership before executing a durable stop.
    pub fn stop(&self, request: StopRequest) -> Result<StopReceipt, String> {
        request.cutoff_millis()?;
        if request.operation_id.is_empty() || request.operation_id.len() > 255 {
            return Err("invalid deletion operation".into());
        }
        let record = self
            .records
            .lock()
            .map_err(|_| "admission lock poisoned")?
            .get(&request.database_id)
            .cloned()
            .ok_or("database not admitted")?;
        if request.environment != record.environment
            || request.organization_id != record.tenant_id
            || request.project_id != record.project_id
            || request.region != record.region
            || request.member_id != record.member_id
            || request.parent_database_id != record.parent_database_id
        {
            return Err("deletion stop identity mismatch".into());
        }
        self.stop_storage.as_ref().ok_or("native deletion stop unavailable")?(&record, &request)?;
        Ok(StopReceipt { request, access_revoked: true, usage_stopped: true })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn request() -> StopRequest {
        StopRequest {
            environment: "production".into(),
            organization_id: "org".into(),
            project_id: "project".into(),
            database_id: "db".into(),
            parent_database_id: None,
            operation_id: "delete-1".into(),
            region: "GRU".into(),
            member_id: "member".into(),
            cutoff: "2026-09-05T00:00:00.123Z".into(),
            retain_until: Some("2026-09-12T00:00:00.123Z".into()),
        }
    }
    #[test]
    fn cutoff_roundtrips_exact_milliseconds_and_refuses_lossy_boundaries() {
        let mut value = request();
        assert_eq!(value.cutoff_millis().unwrap() % 1000, 123);
        value.cutoff = "2026-09-05T00:00:00.123001Z".into();
        assert!(value.cutoff_millis().is_err());
        value.cutoff = "1969-12-31T23:59:59Z".into();
        assert!(value.cutoff_millis().is_err());
    }
}

/// First phase freezes the exact native member before the authority chooses a common cutoff.
#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
pub struct PrepareStopRequest {
    /// Deployment identity.
    pub environment: String,
    /// Owning organization identity.
    pub organization_id: String,
    /// Owning project identity.
    pub project_id: String,
    /// Exact admitted database identity.
    pub database_id: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    /// Parent identity for branches.
    pub parent_database_id: Option<String>,
    /// Durable deletion operation identity.
    pub operation_id: String,
    /// Serving region.
    pub region: String,
    /// Exact serving member identity.
    pub member_id: String,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
/// Acknowledgment of the persisted native preparation.
pub struct PrepareStopReceipt {
    #[serde(flatten)]
    /// Exact acknowledged command.
    pub request: PrepareStopRequest,
    /// Whether native admission is durably frozen.
    pub prepared: bool,
    /// Minimum common billing cutoff admitted by this member.
    pub last_accounted_at: String,
}

/// Callback that must persist the native preparation before returning its boundary.
pub type StoragePrepareStop =
    Arc<dyn Fn(&AdmissionRecord, &PrepareStopRequest) -> Result<u64, String> + Send + Sync>;

impl AdmissionHost {
    /// Installs the first-phase durable fence implementation.
    pub fn with_storage_prepare_stop(mut self, prepare: StoragePrepareStop) -> Self {
        self.prepare_stop_storage = Some(prepare);
        self
    }
    /// Prepares a member-bound stop without inventing a final usage cutoff.
    pub fn prepare_stop(&self, request: PrepareStopRequest) -> Result<PrepareStopReceipt, String> {
        let record = self
            .records
            .lock()
            .map_err(|_| "admission lock poisoned")?
            .get(&request.database_id)
            .cloned()
            .ok_or("database not admitted")?;
        if request.environment != record.environment
            || request.organization_id != record.tenant_id
            || request.project_id != record.project_id
            || request.region != record.region
            || request.member_id != record.member_id
            || request.parent_database_id != record.parent_database_id
            || request.operation_id.is_empty()
            || request.operation_id.len() > 255
        {
            return Err("deletion prepare identity mismatch".into());
        }
        let millis = self
            .prepare_stop_storage
            .as_ref()
            .ok_or("native deletion prepare unavailable")?(&record, &request)?;
        let timestamp = chrono::DateTime::<chrono::Utc>::from_timestamp_millis(
            i64::try_from(millis).map_err(|_| "invalid prepared cutoff")?,
        )
        .ok_or("invalid prepared cutoff")?;
        Ok(PrepareStopReceipt {
            request,
            prepared: true,
            last_accounted_at: timestamp.to_rfc3339_opts(chrono::SecondsFormat::Millis, true),
        })
    }
}

/// Native preflight observations; retention absence grants no cleanup permission.
#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct StopPreflightReceipt {
    #[serde(flatten)]
    /// Exact observed native command.
    pub request: PrepareStopRequest,
    /// Durable protection state.
    pub deletion_protection: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    /// Authenticated retained artifact horizon; absent grants no cleanup.
    pub retain_until: Option<String>,
}
/// Reads durable policy facts for an authenticated member command.
pub type StorageStopPreflight = Arc<
    dyn Fn(&AdmissionRecord, &PrepareStopRequest) -> Result<(bool, Option<u64>), String>
        + Send
        + Sync,
>;
impl AdmissionHost {
    /// Installs the durable policy observation callback; no callback means unavailable.
    pub fn with_storage_stop_preflight(mut self, preflight: StorageStopPreflight) -> Self {
        self.preflight_stop_storage = Some(preflight);
        self
    }
    /// Validates exact native identity before reading protection and retention facts.
    pub fn preflight_stop(
        &self,
        request: PrepareStopRequest,
    ) -> Result<StopPreflightReceipt, String> {
        let record = self
            .records
            .lock()
            .map_err(|_| "admission lock poisoned")?
            .get(&request.database_id)
            .cloned()
            .ok_or("database not admitted")?;
        if request.environment != record.environment
            || request.organization_id != record.tenant_id
            || request.project_id != record.project_id
            || request.region != record.region
            || request.member_id != record.member_id
            || request.parent_database_id != record.parent_database_id
            || request.operation_id.is_empty()
            || request.operation_id.len() > 255
        {
            return Err("deletion preflight identity mismatch".into());
        }
        let (deletion_protection, retention) =
            self.preflight_stop_storage.as_ref().ok_or("native deletion preflight unavailable")?(
                &record, &request,
            )?;
        let retain_until = retention
            .map(|millis| {
                chrono::DateTime::<chrono::Utc>::from_timestamp_millis(
                    i64::try_from(millis).map_err(|_| "invalid retention timestamp")?,
                )
                .map(|value| value.to_rfc3339_opts(chrono::SecondsFormat::Millis, true))
                .ok_or("invalid retention timestamp")
            })
            .transpose()?;
        Ok(StopPreflightReceipt { request, deletion_protection, retain_until })
    }
}
