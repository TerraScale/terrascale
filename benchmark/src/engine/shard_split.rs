//! Engine-owned durable shard route catalog and cutover CAS.

use super::{CommitError, CommitOutcome, Engine, EngineError};
use crate::{
    shard_split::{DurableSplitState, RouteTable, ShardRoute},
    types::{DocumentValue, LocalDurability, OperationId, TenantScope},
};

const VERSION: u8 = 1;
const MAX_ROUTES: usize = 1024;
const MAX_BYTES: usize = 1024 * 1024;

/// A validated route catalog with its durable generation.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct RouteCatalog {
    /// The validated key-range table.
    pub(crate) table: RouteTable,
    /// Monotonic route generation (zero is the initial catalog).
    pub(crate) generation: u64,
}

/// Failures reading or installing a route catalog.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) enum RouteCatalogError {
    /// Bytes were malformed or violated route invariants.
    Malformed,
    /// The caller's expected catalog is stale.
    Conflict,
    /// The underlying engine failed.
    Engine(EngineError),
}

impl From<EngineError> for RouteCatalogError {
    fn from(error: EngineError) -> Self {
        Self::Engine(error)
    }
}

fn key(incarnation: u128) -> String {
    format!("control:shard-route/v1/{incarnation:032x}")
}
fn split_key(incarnation: u128) -> String {
    format!("control:shard-split/v1/{incarnation:032x}")
}

fn push_bound(out: &mut Vec<u8>, bound: Option<&[u8]>) -> Result<(), RouteCatalogError> {
    let bytes = bound.unwrap_or_default();
    let len = u32::try_from(bytes.len()).map_err(|_| RouteCatalogError::Malformed)?;
    out.extend_from_slice(&len.to_be_bytes());
    out.extend_from_slice(bytes);
    Ok(())
}

/// Encodes a route catalog in a bounded, versioned binary form.
pub(crate) fn encode_catalog(catalog: &RouteCatalog) -> Result<Vec<u8>, RouteCatalogError> {
    let routes = catalog.table.routes();
    if routes.is_empty() || routes.len() > MAX_ROUTES {
        return Err(RouteCatalogError::Malformed);
    }
    let mut out = Vec::with_capacity(32);
    out.push(VERSION);
    out.extend_from_slice(&catalog.generation.to_be_bytes());
    out.extend_from_slice(
        &(u32::try_from(routes.len()).map_err(|_| RouteCatalogError::Malformed)?).to_be_bytes(),
    );
    for route in routes {
        if route.shard_id() == 0 {
            return Err(RouteCatalogError::Malformed);
        }
        out.extend_from_slice(&route.shard_id().to_be_bytes());
        push_bound(&mut out, route.lower())?;
        push_bound(&mut out, route.upper())?;
    }
    (out.len() <= MAX_BYTES).then_some(out).ok_or(RouteCatalogError::Malformed)
}

struct Reader<'a> {
    bytes: &'a [u8],
    at: usize,
}
impl<'a> Reader<'a> {
    fn take(&mut self, n: usize) -> Result<&'a [u8], RouteCatalogError> {
        let end = self.at.checked_add(n).ok_or(RouteCatalogError::Malformed)?;
        let value = self.bytes.get(self.at..end).ok_or(RouteCatalogError::Malformed)?;
        self.at = end;
        Ok(value)
    }
    fn u32(&mut self) -> Result<u32, RouteCatalogError> {
        Ok(u32::from_be_bytes(self.take(4)?.try_into().unwrap()))
    }
    fn u64(&mut self) -> Result<u64, RouteCatalogError> {
        Ok(u64::from_be_bytes(self.take(8)?.try_into().unwrap()))
    }
    fn bound(&mut self) -> Result<Option<Vec<u8>>, RouteCatalogError> {
        let n = usize::try_from(self.u32()?).map_err(|_| RouteCatalogError::Malformed)?;
        if n == 0 {
            return Ok(None);
        }
        Ok(Some(self.take(n)?.to_vec()))
    }
}

/// Decodes and validates a complete route catalog.
pub(crate) fn decode_catalog(bytes: &[u8]) -> Result<RouteCatalog, RouteCatalogError> {
    if bytes.len() > MAX_BYTES {
        return Err(RouteCatalogError::Malformed);
    }
    let mut r = Reader { bytes, at: 0 };
    if r.take(1)?[0] != VERSION {
        return Err(RouteCatalogError::Malformed);
    }
    let generation = r.u64()?;
    let count = usize::try_from(r.u32()?).map_err(|_| RouteCatalogError::Malformed)?;
    if count == 0 || count > MAX_ROUTES {
        return Err(RouteCatalogError::Malformed);
    }
    let mut routes = Vec::with_capacity(count);
    let mut ids = std::collections::BTreeSet::new();
    for _ in 0..count {
        let id = r.u64()?;
        if id == 0 || !ids.insert(id) {
            return Err(RouteCatalogError::Malformed);
        }
        let route = ShardRoute::new(id, r.bound()?, r.bound()?)
            .map_err(|_| RouteCatalogError::Malformed)?;
        routes.push(route);
    }
    if r.at != bytes.len() {
        return Err(RouteCatalogError::Malformed);
    }
    let table = RouteTable::new(routes).map_err(|_| RouteCatalogError::Malformed)?;
    Ok(RouteCatalog { table, generation })
}

fn value(bytes: &[u8]) -> DocumentValue {
    DocumentValue::String(hex::encode(bytes))
}
fn bytes(value: Option<DocumentValue>) -> Result<Option<Vec<u8>>, RouteCatalogError> {
    let Some(value) = value else { return Ok(None); };
    let DocumentValue::String(value) = value else { return Err(RouteCatalogError::Malformed); };
    hex::decode(value).map(Some).map_err(|_| RouteCatalogError::Malformed)
}

impl Engine {
    /// Loads the route catalog for a database incarnation from a snapshot.
    pub(crate) fn load_shard_route_catalog(
        &self,
        scope: &TenantScope,
        incarnation: u128,
    ) -> Result<Option<RouteCatalog>, RouteCatalogError> {
        let tx = self.begin(
            scope,
            self.read_snapshot(scope)?,
            OperationId::try_new("route-load").map_err(|_| RouteCatalogError::Malformed)?,
        )?;
        bytes(tx.read_private_metadata(&key(incarnation))?)
            .and_then(|value| value.map_or(Ok(None), |raw| decode_catalog(&raw).map(Some)))
    }

    /// Atomically installs route bytes and the durable cutover split record.
    pub(crate) fn install_shard_route_catalog(
        &self,
        scope: &TenantScope,
        incarnation: u128,
        expected: Option<&[u8]>,
        replacement: &[u8],
        split: &DurableSplitState,
        operation: OperationId,
    ) -> Result<u64, RouteCatalogError> {
        let catalog = decode_catalog(replacement)?;
        let mut tx = self.begin(scope, self.read_snapshot(scope)?, operation)?;
        let current = bytes(tx.read_private_metadata(&key(incarnation))?)?;
        if current.as_deref() == Some(replacement) {
            return Ok(catalog.generation);
        }
        if current.as_deref() != expected {
            return Err(RouteCatalogError::Conflict);
        }
        tx.put_private_metadata(&key(incarnation), value(replacement))?;
        tx.put_private_metadata(&split_key(incarnation), value(&split.encode()))?;
        match tx.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(_) => Ok(catalog.generation),
            CommitOutcome::Rejected(CommitError::Conflict) => Err(RouteCatalogError::Conflict),
            CommitOutcome::Rejected(error) => Err(RouteCatalogError::Engine(match error {
                CommitError::Validation => EngineError::InvalidDocument,
                CommitError::OperationIdReuse { .. } => EngineError::Conflict,
                CommitError::EngineClosed => EngineError::Closed(super::EngineClosed),
                CommitError::UsageRequiresSynced => EngineError::InvalidDocument,
                CommitError::StorageFailure => EngineError::StorageFailure,
                CommitError::Conflict => EngineError::Conflict,
            })),
            CommitOutcome::Unknown(_) => {
                Err(RouteCatalogError::Engine(EngineError::StorageFailure))
            }
        }
    }
}
