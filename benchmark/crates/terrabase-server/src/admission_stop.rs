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
    pub retain_until: String,
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
