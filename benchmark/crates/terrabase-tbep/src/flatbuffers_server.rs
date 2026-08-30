//! Production generated-FlatBuffers dispatch for the authenticated QUIC
//! listener. The legacy positional UDS server does not call this module.

use std::collections::{HashSet, VecDeque};
use std::io::{Read, Write};
use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};

use tbep_fbs::production as wire;

use crate::codec::TbepValue;
use crate::generated::{
    Acknowledgement, BackupOutcome, ChannelBindRequest, DocumentDeleteRequest, DocumentPut,
    DocumentReadRequest, DocumentWriteRequest, ErrorCode, ExpectedVersion, ReadLevel, WriteOutcome,
};
use crate::host::{HostingError, TbepHost};
use crate::server::TenantGates;

const CORRELATION_HORIZON: usize = 1_024;
const SUPPORTED_CAPABILITIES: [&str; 3] = ["document.read", "document.write", "document.delete"];

pub(crate) fn serve_flatbuffers_io<R, W>(
    mut reader: R,
    mut writer: W,
    host: &Arc<dyn TbepHost>,
    inflight: &Arc<AtomicUsize>,
    inflight_bound: usize,
    tenant_gates: &Arc<TenantGates>,
) where
    R: Read,
    W: Write,
{
    let mut state = ConnectionState::default();
    loop {
        let bytes = match read_frame(&mut reader) {
            Ok(Some(bytes)) => bytes,
            Ok(None) | Err(ReadFrameError::Io) => break,
            Err(ReadFrameError::Oversized) => {
                let response = wire::Response::Error {
                    code: ErrorCode::FrameTooLarge.code(),
                    detail: "frame exceeds the largest supported production operation".into(),
                };
                let _ = write_response(&mut writer, &mut state, [0; 16], &response);
                break;
            }
        };

        let request = match wire::decode_request(&bytes) {
            Ok(request) => request,
            Err(error) => {
                let Some(correlation) = error.correlation else { break };
                let code = if matches!(
                    error.semantic,
                    Some(wire::SemanticError::WrongDirectionOrUnsupported)
                ) {
                    ErrorCode::UnknownOperation
                } else {
                    ErrorCode::MalformedFrame
                };
                let response =
                    wire::Response::Error { code: code.code(), detail: error.to_string() };
                if write_response(&mut writer, &mut state, correlation, &response).is_err() {
                    break;
                }
                continue;
            }
        };

        let correlation = request.correlation();
        if !state.record_correlation(correlation) {
            let response = wire::Response::Error {
                code: ErrorCode::MalformedFrame.code(),
                detail: "request correlation was already used on this channel".into(),
            };
            let _ = write_response(&mut writer, &mut state, correlation, &response);
            break;
        }

        let (response, close) =
            dispatch(&mut state, request, host, inflight, inflight_bound, tenant_gates);
        if write_response(&mut writer, &mut state, correlation, &response).is_err() || close {
            break;
        }
    }
}

#[derive(Default)]
struct ConnectionState {
    negotiated: bool,
    tenant: Option<String>,
    correlations: HashSet<wire::FrameId>,
    correlation_order: VecDeque<wire::FrameId>,
    response_sequence: u64,
}

impl ConnectionState {
    fn record_correlation(&mut self, correlation: wire::FrameId) -> bool {
        if !self.correlations.insert(correlation) {
            return false;
        }
        self.correlation_order.push_back(correlation);
        if self.correlation_order.len() > CORRELATION_HORIZON
            && let Some(expired) = self.correlation_order.pop_front()
        {
            self.correlations.remove(&expired);
        }
        true
    }

    fn next_response_correlation(&mut self) -> wire::FrameId {
        self.response_sequence = self.response_sequence.checked_add(1).unwrap_or(1);
        let elapsed = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap_or_default()
            .as_millis();
        let timestamp = elapsed.to_be_bytes();
        let mut result = [0_u8; 16];
        result[..6].copy_from_slice(&timestamp[10..]);
        let process_id = std::process::id().to_be_bytes();
        result[6..8].copy_from_slice(&process_id[2..]);
        result[8..].copy_from_slice(&self.response_sequence.to_be_bytes());
        result
    }
}

// The single match keeps protocol sequencing and close-policy decisions auditable together.
#[allow(clippy::too_many_lines)]
fn dispatch(
    state: &mut ConnectionState,
    request: wire::Request,
    host: &Arc<dyn TbepHost>,
    inflight: &Arc<AtomicUsize>,
    inflight_bound: usize,
    tenant_gates: &Arc<TenantGates>,
) -> (wire::Response, bool) {
    match request {
        wire::Request::Negotiation { supported_versions, capabilities, .. } => {
            if state.negotiated || state.tenant.is_some() {
                return (
                    protocol_error(ErrorCode::AlreadyBound, "channel already negotiated"),
                    true,
                );
            }
            if !supported_versions.contains(&1) {
                return (
                    protocol_error(
                        ErrorCode::ProtocolVersionMismatch,
                        "server supports TBEP major 1 only",
                    ),
                    true,
                );
            }
            state.negotiated = true;
            let accepted_capabilities = capabilities
                .into_iter()
                .filter(|capability| SUPPORTED_CAPABILITIES.contains(&capability.as_str()))
                .collect();
            (wire::Response::Negotiation { selected_version: 1, accepted_capabilities }, false)
        }
        wire::Request::ChannelBind { principal, tenant, .. } => {
            if !state.negotiated {
                return (negotiation_required(), true);
            }
            if state.tenant.is_some() {
                return (protocol_error(ErrorCode::AlreadyBound, "channel already bound"), false);
            }
            let request = ChannelBindRequest { principal, tenant: tenant.clone() };
            match host.bind_channel(&request) {
                Ok(response) if response.tenant == tenant => {
                    state.tenant = Some(tenant);
                    (
                        wire::Response::ChannelBind {
                            tenant: response.tenant,
                            serving_instance: response.serving_instance,
                        },
                        false,
                    )
                }
                Ok(_) => (
                    protocol_error(ErrorCode::EngineFailure, "engine changed the bound database"),
                    true,
                ),
                Err(error) => (host_error(&error), false),
            }
        }
        wire::Request::DocumentRead { collection, document, level, .. } => {
            let Some(tenant) = bound_tenant(state) else {
                return (unbound(), false);
            };
            let Some(_admission) = Admission::enter(inflight, inflight_bound, tenant_gates, tenant)
            else {
                return (overloaded(), false);
            };
            let request =
                DocumentReadRequest { collection, document, level: map_read_level(level) };
            match host.document_read(tenant, &request) {
                Ok(response) if response.found == response.document.is_some() => {
                    let document = response.document.as_ref().map(map_document);
                    (
                        wire::Response::DocumentRead {
                            document,
                            position: response.position.unwrap_or_default(),
                        },
                        false,
                    )
                }
                Ok(_) => (
                    protocol_error(
                        ErrorCode::EngineFailure,
                        "engine returned an inconsistent read",
                    ),
                    true,
                ),
                Err(error) => (host_error(&error), false),
            }
        }
        wire::Request::DocumentWrite {
            document, expected, operation_id, acknowledgement, ..
        } => {
            let Some(tenant) = bound_tenant(state) else {
                return (unbound(), false);
            };
            let Some(_admission) = Admission::enter(inflight, inflight_bound, tenant_gates, tenant)
            else {
                return (overloaded(), false);
            };
            let request = DocumentWriteRequest {
                document: DocumentPut {
                    id: document.id,
                    collection: document.collection,
                    body: map_value_to_host(document.body),
                },
                expected: map_expected(expected),
                operation_id: operation_id.clone(),
                ack: map_acknowledgement(acknowledgement),
            };
            match host.document_write(tenant, &request, &operation_id) {
                Ok(response) => {
                    (wire::Response::DocumentWrite(map_outcome(response.outcome)), false)
                }
                Err(error) => (host_error(&error), false),
            }
        }
        wire::Request::DocumentDelete {
            collection,
            document,
            expected,
            operation_id,
            acknowledgement,
            ..
        } => {
            let Some(tenant) = bound_tenant(state) else {
                return (unbound(), false);
            };
            let Some(_admission) = Admission::enter(inflight, inflight_bound, tenant_gates, tenant)
            else {
                return (overloaded(), false);
            };
            let request = DocumentDeleteRequest {
                collection,
                document,
                expected: map_expected(expected),
                operation_id: operation_id.clone(),
                ack: map_acknowledgement(acknowledgement),
            };
            match host.document_delete(tenant, &request, &operation_id) {
                Ok(response) => {
                    (wire::Response::DocumentDelete(map_outcome(response.outcome)), false)
                }
                Err(error) => (host_error(&error), false),
            }
        }
    }
}

fn bound_tenant(state: &ConnectionState) -> Option<&str> {
    if state.negotiated { state.tenant.as_deref() } else { None }
}

fn protocol_error(code: ErrorCode, detail: &str) -> wire::Response {
    wire::Response::Error { code: code.code(), detail: detail.into() }
}

fn negotiation_required() -> wire::Response {
    protocol_error(ErrorCode::ProtocolVersionMismatch, "negotiate TBEP before channel binding")
}

fn unbound() -> wire::Response {
    protocol_error(ErrorCode::UnboundChannel, "bind the database channel before document work")
}

fn overloaded() -> wire::Response {
    protocol_error(ErrorCode::Overloaded, "in-flight bound reached; retry_after_ms=50")
}

fn host_error(error: &HostingError) -> wire::Response {
    wire::Response::Error { code: error.error_code().code(), detail: error.detail() }
}

fn map_read_level(level: wire::ReadLevel) -> Option<ReadLevel> {
    match level {
        wire::ReadLevel::Default => None,
        wire::ReadLevel::Local => Some(ReadLevel::Local),
        wire::ReadLevel::RegionalQuorum => Some(ReadLevel::RegionalQuorumRead),
        wire::ReadLevel::GlobalQuorum => Some(ReadLevel::GlobalQuorumRead),
    }
}

fn map_acknowledgement(value: wire::Acknowledgement) -> Option<Acknowledgement> {
    match value {
        wire::Acknowledgement::Default => None,
        wire::Acknowledgement::RegionalQuorum => Some(Acknowledgement::RegionalQuorum),
        wire::Acknowledgement::GlobalQuorum => Some(Acknowledgement::GlobalQuorum),
        wire::Acknowledgement::BackupAck => Some(Acknowledgement::BackupAck),
    }
}

fn map_expected(value: wire::ExpectedVersion) -> ExpectedVersion {
    match value {
        wire::ExpectedVersion::Absent => ExpectedVersion::Absent,
        wire::ExpectedVersion::Any => ExpectedVersion::Any,
        wire::ExpectedVersion::Exact(version) => ExpectedVersion::Exact(version),
    }
}

fn map_value_to_host(value: wire::Value) -> TbepValue {
    match value {
        wire::Value::Null => TbepValue::Null,
        wire::Value::Boolean(value) => TbepValue::Boolean(value),
        wire::Value::Integer(value) => TbepValue::Integer(value),
        wire::Value::Number(value) => TbepValue::Number(value),
        wire::Value::String(value) => TbepValue::String(value),
        wire::Value::Array(values) => {
            TbepValue::Array(values.into_iter().map(map_value_to_host).collect())
        }
        wire::Value::Object(values) => TbepValue::Object(
            values.into_iter().map(|(key, value)| (key, map_value_to_host(value))).collect(),
        ),
    }
}

fn map_value_from_host(value: &TbepValue) -> wire::Value {
    match value {
        TbepValue::Null => wire::Value::Null,
        TbepValue::Boolean(value) => wire::Value::Boolean(*value),
        TbepValue::Integer(value) => wire::Value::Integer(*value),
        TbepValue::Number(value) => wire::Value::Number(*value),
        TbepValue::String(value) => wire::Value::String(value.clone()),
        TbepValue::Array(values) => {
            wire::Value::Array(values.iter().map(map_value_from_host).collect())
        }
        TbepValue::Object(values) => wire::Value::Object(
            values.iter().map(|(key, value)| (key.clone(), map_value_from_host(value))).collect(),
        ),
    }
}

fn map_document(document: &crate::generated::DocumentBody) -> wire::Document {
    wire::Document {
        id: document.id.clone(),
        collection: document.collection.clone(),
        version: document.version,
        body: map_value_from_host(&document.body),
    }
}

fn map_outcome(outcome: WriteOutcome) -> wire::WriteOutcome {
    match outcome {
        WriteOutcome::Committed(receipt) => wire::WriteOutcome::Committed(wire::CommitReceipt {
            sequence: receipt.sequence,
            version: receipt.version,
            operation_id: receipt.operation_id,
            acknowledgement: receipt.acknowledgement.map(|value| match value {
                Acknowledgement::RegionalQuorum => wire::Acknowledgement::RegionalQuorum,
                Acknowledgement::GlobalQuorum => wire::Acknowledgement::GlobalQuorum,
                Acknowledgement::BackupAck => wire::Acknowledgement::BackupAck,
            }),
            backup: receipt.backup.map(|value| match value {
                BackupOutcome::None => 1,
                BackupOutcome::Full => 2,
                BackupOutcome::Partial => 3,
            }),
        }),
        WriteOutcome::Conflict => wire::WriteOutcome::Conflict,
        WriteOutcome::Rejected(reason) => wire::WriteOutcome::Rejected(reason.code()),
    }
}

struct Admission<'a> {
    inflight: &'a AtomicUsize,
    tenant_gates: &'a TenantGates,
    tenant: &'a str,
}

impl<'a> Admission<'a> {
    fn enter(
        inflight: &'a AtomicUsize,
        bound: usize,
        tenant_gates: &'a TenantGates,
        tenant: &'a str,
    ) -> Option<Self> {
        let occupied = inflight.fetch_add(1, Ordering::SeqCst);
        if occupied >= bound {
            inflight.fetch_sub(1, Ordering::SeqCst);
            return None;
        }
        if !tenant_gates.enter(tenant) {
            inflight.fetch_sub(1, Ordering::SeqCst);
            return None;
        }
        Some(Self { inflight, tenant_gates, tenant })
    }
}

impl Drop for Admission<'_> {
    fn drop(&mut self) {
        self.tenant_gates.release(self.tenant);
        self.inflight.fetch_sub(1, Ordering::SeqCst);
    }
}

fn write_response<W: Write>(
    writer: &mut W,
    state: &mut ConnectionState,
    request_correlation: wire::FrameId,
    response: &wire::Response,
) -> Result<(), ()> {
    let correlation = state.next_response_correlation();
    let bytes =
        wire::encode_response(correlation, request_correlation, response).map_err(|_| ())?;
    writer.write_all(&bytes).map_err(|_| ())?;
    writer.flush().map_err(|_| ())
}

enum ReadFrameError {
    Io,
    Oversized,
}

fn read_frame<R: Read>(reader: &mut R) -> Result<Option<Vec<u8>>, ReadFrameError> {
    let mut prefix = [0_u8; tbep_fbs::SIZE_PREFIX_BYTES];
    let mut read = 0;
    while read < prefix.len() {
        match reader.read(&mut prefix[read..]) {
            Ok(0) if read == 0 => return Ok(None),
            Ok(0) | Err(_) => return Err(ReadFrameError::Io),
            Ok(count) => read += count,
        }
    }
    let declared = u32::from_le_bytes(prefix) as usize;
    if declared > wire::LARGE_OPERATION_FRAME_BYTES {
        return Err(ReadFrameError::Oversized);
    }
    let mut bytes = Vec::with_capacity(prefix.len() + declared);
    bytes.extend_from_slice(&prefix);
    bytes.resize(prefix.len() + declared, 0);
    reader.read_exact(&mut bytes[prefix.len()..]).map_err(|_| ReadFrameError::Io)?;
    Ok(Some(bytes))
}
