//! Authenticated host-local credential rotation with durable replay receipts.
use crate::reactive_api::{CapabilityKeyRing, OriginProofKeyRing};
use axum::{
    Json, Router,
    extract::{DefaultBodyLimit, State},
    http::{HeaderMap, StatusCode},
    routing::post,
};
use base64::{Engine as _, engine::general_purpose::STANDARD};
use serde::{Deserialize, Serialize};
use std::{
    collections::BTreeMap,
    io::{self, Write},
    os::unix::fs::{FileTypeExt, MetadataExt, OpenOptionsExt, PermissionsExt},
    path::{Path, PathBuf},
    sync::{Arc, Mutex, RwLock},
    time::{SystemTime, UNIX_EPOCH},
};

fn now() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map_or(u64::MAX, |v| v.as_secs())
}

#[derive(Clone, Default, Serialize, Deserialize)]
pub(crate) struct KeyMaterial {
    pub(crate) current: Vec<u8>,
    previous: Option<Vec<u8>>,
    expires_at: u64,
    previous_expires_at: u64,
}
impl std::fmt::Debug for KeyMaterial {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("KeyMaterial([REDACTED])")
    }
}
impl Drop for KeyMaterial {
    fn drop(&mut self) {
        use zeroize::Zeroize;
        self.current.zeroize();
        if let Some(p) = &mut self.previous {
            p.zeroize();
        }
    }
}
impl KeyMaterial {
    pub(crate) fn new(current: &[u8], previous: Option<&[u8]>) -> Option<Self> {
        if !(16..=4096).contains(&current.len())
            || previous.is_some_and(|p| !(16..=4096).contains(&p.len()) || p == current)
        {
            return None;
        }
        Some(Self {
            current: current.to_vec(),
            previous: previous.map(<[u8]>::to_vec),
            expires_at: u64::MAX,
            previous_expires_at: u64::MAX,
        })
    }
    pub(crate) fn usable(&self) -> Option<&Self> {
        (!self.current.is_empty() && self.expires_at > now()).then_some(self)
    }
    pub(crate) fn previous_key(&self) -> Option<&[u8]> {
        if self.previous_expires_at > now() { self.previous.as_deref() } else { None }
    }
}
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub(crate) struct LiveKeys {
    #[serde(default)]
    tls: Option<TlsMaterial>,
    #[serde(default)]
    previous_generation: u64,
    #[serde(default)]
    origin_previous_generation: u64,
    pub(crate) origin: KeyMaterial,
    pub(crate) capability: KeyMaterial,
}
impl LiveKeys {
    pub(crate) fn single(keys: KeyMaterial) -> Self {
        Self {
            origin: keys.clone(),
            capability: keys,
            tls: None,
            previous_generation: 0,
            origin_previous_generation: 0,
        }
    }
}

fn materials_disjoint(keys: &LiveKeys) -> bool {
    let capability =
        [keys.capability.current.as_slice(), keys.capability.previous.as_deref().unwrap_or(&[])];
    let origin = [keys.origin.current.as_slice(), keys.origin.previous.as_deref().unwrap_or(&[])];
    capability.iter().all(|candidate| {
        candidate.is_empty() || origin.iter().all(|other| other.is_empty() || *candidate != *other)
    })
}

#[derive(Clone, Serialize, Deserialize)]
struct TlsMaterial {
    workload: Vec<u8>,
    trust: Vec<u8>,
    expires_at: u64,
}

/// Initial TLS listener and certificate material used to restore rotation state.
pub struct RotationTlsConfig {
    /// Live reload target used to install or fence rotated listener credentials.
    pub controller: terrabase_tbep::grpc::reloadable_tls::ReloadableTls,
    /// Workload certificate and private key in PEM form.
    pub workload: Vec<u8>,
    /// Trusted workload certificate authority in PEM form.
    pub trust: Vec<u8>,
}
impl std::fmt::Debug for TlsMaterial {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("TlsMaterial([REDACTED])")
    }
}
impl Drop for TlsMaterial {
    fn drop(&mut self) {
        use zeroize::Zeroize;
        self.workload.zeroize();
        self.trust.zeroize();
    }
}
impl TlsMaterial {
    fn prepare(&self) -> io::Result<terrabase_tbep::grpc::reloadable_tls::PreparedTls> {
        let workload = std::str::from_utf8(&self.workload).map_err(io::Error::other)?;
        let trust = std::str::from_utf8(&self.trust).map_err(io::Error::other)?;
        terrabase_tbep::grpc::reloadable_tls::ReloadableTls::prepare(
            workload,
            workload,
            trust,
            self.expires_at,
        )
    }
}

#[derive(Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct Request {
    operation_id: String,
    action: String,
    generation: u64,
    #[serde(default)]
    expires_at: u64,
    #[serde(default)]
    current_key_id: String,
    #[serde(default)]
    current_key: String,
    #[serde(default)]
    origin_current_key_id: String,
    #[serde(default)]
    origin_current_key: String,
    #[serde(default)]
    origin_expires_at: u64,
    #[serde(default)]
    previous_key_id: String,
    #[serde(default)]
    previous_key: String,
    #[serde(default)]
    previous_expires_at: u64,
    #[serde(default)]
    origin_previous_key_id: String,
    #[serde(default)]
    origin_previous_key: String,
    #[serde(default)]
    origin_previous_generation: u64,
    #[serde(default)]
    origin_previous_expires_at: u64,
    #[serde(default)]
    workload_tls: String,
    #[serde(default)]
    previous_generation: u64,
    #[serde(default)]
    trust_bundle: String,
}
impl Drop for Request {
    fn drop(&mut self) {
        use zeroize::Zeroize;
        self.workload_tls.zeroize();
        self.trust_bundle.zeroize();
        self.current_key.zeroize();
        self.previous_key.zeroize();
        self.origin_current_key.zeroize();
        self.origin_previous_key.zeroize();
    }
}
#[derive(Clone, Serialize, Deserialize)]
struct Receipt {
    generation: u64,
    valid_until: u64,
    state: String,
    operation_id: String,
}
#[derive(Clone, Serialize, Deserialize)]
struct SavedReceipt {
    digest: String,
    receipt: Receipt,
}
#[derive(Clone, Serialize, Deserialize)]
struct Durable {
    generation: u64,
    state: String,
    active: LiveKeys,
    staged: Option<LiveKeys>,
    rollback: Option<LiveKeys>,
    verified: bool,
    receipts: BTreeMap<String, SavedReceipt>,
}
struct Inner {
    durable: Durable,
    file: PathBuf,
    _lock: std::fs::File,
}
struct Service {
    tls: Option<terrabase_tbep::grpc::reloadable_tls::ReloadableTls>,
    inner: Mutex<Inner>,
    live: Arc<RwLock<LiveKeys>>,
    token: zeroize::Zeroizing<Vec<u8>>,
}

/// Bound private listener and the two rings that must be installed in API state.
pub struct BoundRotation {
    /// Live origin-proof verifier ring.
    pub origin: OriginProofKeyRing,
    /// Live capability issuer/verifier ring.
    pub capability: CapabilityKeyRing,
    listener: tokio::net::UnixListener,
    socket: PathBuf,
    service: Arc<Service>,
}

fn private_dir(path: &Path) -> io::Result<()> {
    let metadata = std::fs::symlink_metadata(path)?;
    if !metadata.is_dir()
        || metadata.uid() != rustix::process::geteuid().as_raw()
        || metadata.permissions().mode() & 0o077 != 0
    {
        return Err(io::Error::other("rotation directory must be private (0700)"));
    }
    Ok(())
}
fn persist(path: &Path, state: &Durable) -> io::Result<()> {
    let bytes = zeroize::Zeroizing::new(serde_json::to_vec(state)?);
    let tmp = path.with_extension("rotation-tmp");
    // A leftover temporary file is never trusted after a crash.
    if tmp.exists() {
        std::fs::remove_file(&tmp)?;
    }
    let mut f = std::fs::OpenOptions::new().write(true).create_new(true).mode(0o600).open(&tmp)?;
    f.write_all(&bytes)?;
    f.sync_all()?;
    std::fs::rename(&tmp, path)?;
    std::fs::File::open(path.parent().ok_or_else(|| io::Error::other("missing state directory"))?)?
        .sync_all()
}

/// Binds the private control socket and restores durable active credentials.
///
/// # Errors
/// Rejects weak tokens, public directories, malformed state, or socket conflicts.
pub fn bind(
    socket: &Path,
    state_file: &Path,
    token: Vec<u8>,
    origin: &OriginProofKeyRing,
    capability: &CapabilityKeyRing,
) -> io::Result<BoundRotation> {
    bind_inner(socket, state_file, token, origin, capability, None)
}
/// Binds rotation to the actual member TLS listener and its initial durable material.
///
/// # Errors
/// Returns invalid TLS material or private listener/state validation failures.
pub fn bind_with_tls(
    socket: &Path,
    state_file: &Path,
    token: Vec<u8>,
    origin: &OriginProofKeyRing,
    capability: &CapabilityKeyRing,
    tls: RotationTlsConfig,
) -> io::Result<BoundRotation> {
    let material = TlsMaterial { workload: tls.workload, trust: tls.trust, expires_at: u64::MAX };
    material.prepare()?;
    bind_inner(socket, state_file, token, origin, capability, Some((tls.controller, material)))
}
fn bind_inner(
    socket: &Path,
    state_file: &Path,
    token: Vec<u8>,
    origin: &OriginProofKeyRing,
    capability: &CapabilityKeyRing,
    tls: Option<(terrabase_tbep::grpc::reloadable_tls::ReloadableTls, TlsMaterial)>,
) -> io::Result<BoundRotation> {
    if token.len() < 32 || token.len() > 4096 {
        return Err(io::Error::other("rotation token must contain 32..4096 bytes"));
    }
    private_dir(socket.parent().ok_or_else(|| io::Error::other("missing socket directory"))?)?;
    private_dir(state_file.parent().ok_or_else(|| io::Error::other("missing state directory"))?)?;
    let lock_path = state_file.with_extension("rotation-lock");
    let lock = std::fs::OpenOptions::new()
        .read(true)
        .write(true)
        .create(true)
        .truncate(false)
        .mode(0o600)
        .custom_flags(rustix::fs::OFlags::NOFOLLOW.bits().cast_signed())
        .open(lock_path)?;
    lock.try_lock().map_err(io::Error::other)?;
    if let Ok(metadata) = std::fs::symlink_metadata(socket) {
        if !metadata.file_type().is_socket() || metadata.permissions().mode() & 0o077 != 0 {
            return Err(io::Error::other("unsafe existing rotation socket"));
        }
        match std::os::unix::net::UnixStream::connect(socket) {
            Ok(_) => return Err(io::Error::other("rotation socket already active")),
            Err(e) if e.kind() == io::ErrorKind::ConnectionRefused => std::fs::remove_file(socket)?,
            Err(e) => return Err(e),
        }
    }
    let initial = LiveKeys {
        previous_generation: 0,
        origin_previous_generation: 0,
        tls: tls.as_ref().map(|(_, material)| material.clone()),
        origin: origin
            .live
            .read()
            .map_err(|_| io::Error::other("poisoned keyring"))?
            .origin
            .clone(),
        capability: capability
            .live
            .read()
            .map_err(|_| io::Error::other("poisoned keyring"))?
            .capability
            .clone(),
    };
    let durable = if state_file.exists() {
        let metadata = std::fs::symlink_metadata(state_file)?;
        if !metadata.is_file() || metadata.permissions().mode() & 0o077 != 0 {
            return Err(io::Error::other("rotation state must be a private regular file"));
        }
        let bytes = zeroize::Zeroizing::new(std::fs::read(state_file)?);
        serde_json::from_slice::<Durable>(&bytes)?
    } else {
        let value = Durable {
            generation: 0,
            state: "active".into(),
            active: initial,
            staged: None,
            rollback: None,
            verified: false,
            receipts: BTreeMap::new(),
        };
        persist(state_file, &value)?;
        value
    };
    if !materials_disjoint(&durable.active)
        || durable.staged.as_ref().is_some_and(|keys| !materials_disjoint(keys))
        || durable.rollback.as_ref().is_some_and(|keys| !materials_disjoint(keys))
    {
        return Err(io::Error::other("rotation rings must contain disjoint material"));
    }
    if let Some((controller, _)) = &tls {
        if durable.state == "fenced" {
            controller.fence();
        } else {
            controller.install(
                durable
                    .active
                    .tls
                    .as_ref()
                    .ok_or_else(|| io::Error::other("durable TLS material missing"))?
                    .prepare()?,
            )?;
        }
    }
    let live = Arc::new(RwLock::new(durable.active.clone()));
    let listener = tokio::net::UnixListener::bind(socket)?;
    std::fs::set_permissions(socket, std::fs::Permissions::from_mode(0o600))?;
    let service = Arc::new(Service {
        tls: tls.map(|(controller, _)| controller),
        inner: Mutex::new(Inner { durable, file: state_file.to_owned(), _lock: lock }),
        live: live.clone(),
        token: zeroize::Zeroizing::new(token),
    });
    Ok(BoundRotation {
        origin: OriginProofKeyRing { live: live.clone() },
        capability: CapabilityKeyRing { live },
        listener,
        socket: socket.to_owned(),
        service,
    })
}
impl BoundRotation {
    /// Serves only the authenticated local rotation control route.
    ///
    /// # Errors
    /// Returns listener failures.
    pub async fn serve(self) -> io::Result<()> {
        self.serve_until(std::future::pending::<()>()).await
    }
    /// Serves until the owner supplies a shutdown signal, then removes the socket.
    ///
    /// # Errors
    /// Returns listener or socket cleanup failures.
    pub async fn serve_until(
        self,
        shutdown: impl std::future::Future<Output = ()> + Send + 'static,
    ) -> io::Result<()> {
        let router = Router::new()
            .route("/v1/credential-rotation", post(handle))
            .layer(DefaultBodyLimit::max(256 * 1024))
            .with_state(self.service);
        let result = axum::serve(self.listener, router).with_graceful_shutdown(shutdown).await;
        std::fs::remove_file(self.socket)?;
        result
    }
}
async fn handle(
    State(service): State<Arc<Service>>,
    headers: HeaderMap,
    Json(request): Json<Request>,
) -> Result<Json<Receipt>, (StatusCode, &'static str)> {
    let supplied = headers
        .get("authorization")
        .and_then(|h| h.to_str().ok())
        .and_then(|h| h.strip_prefix("Bearer "))
        .unwrap_or("");
    if !constant_time_equal(supplied.as_bytes(), &service.token) {
        return Err((StatusCode::UNAUTHORIZED, "unauthorized"));
    }
    apply(&service, &request)
        .map(Json)
        .map_err(|_| (StatusCode::CONFLICT, "rotation transition rejected"))
}
fn constant_time_equal(a: &[u8], b: &[u8]) -> bool {
    // Hashing first gives the constant-time primitive fixed-size inputs.
    aws_lc_rs::constant_time::verify_slices_are_equal(
        blake3::hash(a).as_bytes(),
        blake3::hash(b).as_bytes(),
    )
    .is_ok()
}
fn stage_rotation(service: &Service, request: &Request, next: &mut Durable) -> io::Result<()> {
    let invalid = || io::Error::other("invalid rotation transition");
    if request.generation <= next.generation
        || next.staged.is_some()
        || request.expires_at <= now()
        || request.current_key_id.is_empty()
        || request.current_key_id.len() > 128
        || request.origin_expires_at <= now()
        || request.origin_current_key_id.is_empty()
        || request.origin_current_key_id.len() > 128
    {
        return Err(invalid());
    }
    let current =
        zeroize::Zeroizing::new(STANDARD.decode(&request.current_key).map_err(|_| invalid())?);
    let previous =
        zeroize::Zeroizing::new(STANDARD.decode(&request.previous_key).map_err(|_| invalid())?);
    let origin_current = zeroize::Zeroizing::new(
        STANDARD.decode(&request.origin_current_key).map_err(|_| invalid())?,
    );
    let origin_previous = zeroize::Zeroizing::new(
        STANDARD.decode(&request.origin_previous_key).map_err(|_| invalid())?,
    );
    if current == origin_current
        || (!previous.is_empty() && previous == origin_current)
        || (!origin_previous.is_empty() && current == origin_previous)
        || (!previous.is_empty() && !origin_previous.is_empty() && previous == origin_previous)
    {
        return Err(invalid());
    }
    let overlap = !previous.is_empty();
    if overlap
        && (request.previous_key_id.is_empty()
            || request.previous_key_id == request.current_key_id
            || request.previous_expires_at <= now()
            || request.previous_expires_at > request.expires_at)
    {
        return Err(invalid());
    }
    let mut keys =
        KeyMaterial::new(&current, overlap.then_some(previous.as_slice())).ok_or_else(invalid)?;
    keys.expires_at = request.expires_at;
    keys.previous_expires_at = request.previous_expires_at;
    let origin_overlap = !origin_previous.is_empty();
    if origin_overlap
        && (request.origin_previous_key_id.is_empty()
            || request.origin_previous_key_id == request.origin_current_key_id
            || request.origin_previous_expires_at <= now()
            || request.origin_previous_expires_at > request.origin_expires_at
            || request.origin_previous_generation == 0
            || request.origin_previous_generation >= request.generation)
    {
        return Err(invalid());
    }
    let mut origin_keys =
        KeyMaterial::new(&origin_current, origin_overlap.then_some(origin_previous.as_slice()))
            .ok_or_else(invalid)?;
    origin_keys.expires_at = request.origin_expires_at;
    origin_keys.previous_expires_at = request.origin_previous_expires_at;
    if overlap
        && (request.previous_generation == 0 || request.previous_generation >= request.generation)
    {
        return Err(invalid());
    }
    let mut snapshot = LiveKeys { origin: origin_keys, capability: keys, ..LiveKeys::default() };
    snapshot.previous_generation = request.previous_generation;
    snapshot.origin_previous_generation = request.origin_previous_generation;
    if service.tls.is_some() {
        let mut material = TlsMaterial {
            workload: STANDARD.decode(&request.workload_tls).map_err(|_| invalid())?,
            trust: STANDARD.decode(&request.trust_bundle).map_err(|_| invalid())?,
            expires_at: request.expires_at,
        };
        if material.workload.len() > 128 * 1024 || material.trust.len() > 32 * 1024 {
            return Err(invalid());
        }
        let prepared = material.prepare()?;
        service.tls.as_ref().ok_or_else(invalid)?.validate(&prepared)?;
        material.expires_at = prepared.valid_until();
        snapshot.origin.expires_at = snapshot.origin.expires_at.min(material.expires_at);
        snapshot.capability.expires_at = snapshot.capability.expires_at.min(material.expires_at);
        snapshot.tls = Some(material);
    } else if !request.workload_tls.is_empty() || !request.trust_bundle.is_empty() {
        return Err(invalid());
    }
    next.staged = Some(snapshot);
    next.generation = request.generation;
    next.state = "staged".into();
    next.verified = false;
    Ok(())
}

fn publish_rotation(
    service: &Service,
    inner: &mut Inner,
    request: &Request,
    mut next: Durable,
    digest: String,
) -> io::Result<Receipt> {
    let invalid = || io::Error::other("invalid rotation transition");
    let active = next.staged.as_ref().unwrap_or(&next.active);
    let valid_until = active.origin.expires_at.min(active.capability.expires_at);
    let receipt = Receipt {
        generation: if request.action == "retire" { request.generation } else { next.generation },
        valid_until,
        state: next.state.clone(),
        operation_id: request.operation_id.clone(),
    };
    // A bounded receipt journal fails closed instead of evicting replay protection.
    if next.receipts.len() >= 100_000 {
        return Err(invalid());
    }
    next.receipts
        .insert(request.operation_id.clone(), SavedReceipt { digest, receipt: receipt.clone() });
    let prepared_tls = if service.tls.is_some()
        && next.state != "fenced"
        && !matches!(request.action.as_str(), "stage" | "verify")
    {
        Some(next.active.tls.as_ref().ok_or_else(invalid)?.prepare()?)
    } else {
        None
    };
    if let (Some(controller), Some(prepared)) = (&service.tls, &prepared_tls) {
        controller.validate(prepared)?;
    }
    let mut live = service.live.write().map_err(|_| invalid())?;
    if let Err(error) = persist(&inner.file, &next) {
        // A failed directory fsync can leave an uncertain on-disk commit.
        // Fence credentials until authenticated retry or restart reconciles it.
        *live = LiveKeys::default();
        if let Some(tls) = &service.tls {
            tls.fence();
        }
        return Err(error);
    }
    if let Some(tls) = &service.tls
        && !matches!(request.action.as_str(), "stage" | "verify")
    {
        if let Some(prepared) = prepared_tls {
            if let Err(error) = tls.install(prepared) {
                tls.fence();
                *live = LiveKeys::default();
                return Err(error);
            }
        } else {
            tls.fence();
        }
    }
    *live = next.active.clone();
    inner.durable = next;
    Ok(receipt)
}

fn apply(service: &Service, request: &Request) -> io::Result<Receipt> {
    let invalid = || io::Error::other("invalid rotation transition");
    if request.operation_id.is_empty()
        || request.operation_id.len() > 128
        || (request.generation == 0 && request.action != "fence")
    {
        return Err(invalid());
    }
    let encoded = zeroize::Zeroizing::new(serde_json::to_vec(request)?);
    let digest = blake3::hash(&encoded).to_hex().to_string();
    let mut inner = service.inner.lock().map_err(|_| invalid())?;
    if let Some(saved) = inner.durable.receipts.get(&request.operation_id) {
        return if saved.digest == digest { Ok(saved.receipt.clone()) } else { Err(invalid()) };
    }
    let mut next = inner.durable.clone();
    match request.action.as_str() {
        "stage" => {
            stage_rotation(service, request, &mut next)?;
        }
        "verify" => {
            if request.generation != next.generation
                || next
                    .staged
                    .as_ref()
                    .is_none_or(|s| s.origin.usable().is_none() || s.capability.usable().is_none())
            {
                return Err(invalid());
            }
            if let Some(controller) = &service.tls {
                controller.validate(
                    &next
                        .staged
                        .as_ref()
                        .and_then(|s| s.tls.as_ref())
                        .ok_or_else(invalid)?
                        .prepare()?,
                )?;
            }
            next.verified = true;
        }
        "activate" => {
            if request.generation != next.generation || !next.verified {
                return Err(invalid());
            }
            let staged = next.staged.take().ok_or_else(invalid)?;
            if staged.origin.usable().is_none() || staged.capability.usable().is_none() {
                return Err(invalid());
            }
            next.rollback = Some(next.active.clone());
            next.active = staged;
            next.state = "active".into();
        }
        "retire" => {
            if next.state != "active" {
                return Err(invalid());
            }
            let retire_capability = request.generation == next.active.previous_generation;
            let retire_origin = request.generation == next.active.origin_previous_generation;
            if !retire_capability && !retire_origin {
                return Err(invalid());
            }
            if retire_capability {
                next.active.previous_generation = 0;
                next.active.capability.previous = None;
            }
            if retire_origin {
                next.active.origin_previous_generation = 0;
                next.active.origin.previous = None;
            }
            next.rollback = None;
        }
        "rollback" => {
            if request.generation != next.generation {
                return Err(invalid());
            }
            if next.staged.take().is_none() {
                next.active = next.rollback.take().ok_or_else(invalid)?;
            }
            next.state = "active".into();
        }
        "fence" => {
            if request.generation != 0 && request.generation < next.generation {
                return Err(invalid());
            }
            next.generation = next.generation.max(request.generation);
            next.active = LiveKeys::default();
            next.staged = None;
            next.rollback = None;
            next.state = "fenced".into();
        }
        _ => return Err(invalid()),
    }
    publish_rotation(service, &mut inner, request, next, digest)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::reactive_api::{ORIGIN_PROOF_HEADER, origin_proof_for, verify_origin_proof};
    use axum::http::{HeaderValue, Method, Uri};
    use rcgen::{BasicConstraints, CertificateParams, ExtendedKeyUsagePurpose, IsCa, KeyPair};
    fn request(action: &str, operation: &str) -> Request {
        Request {
            operation_id: operation.into(),
            action: action.into(),
            generation: 3,
            expires_at: now() + 3600,
            current_key_id: "new".into(),
            current_key: STANDARD.encode(b"new-key-with-at-least-32-bytes-000"),
            origin_current_key_id: "origin-new".into(),
            origin_current_key: STANDARD.encode(b"origin-new-key-at-least-32-bytes-"),
            origin_expires_at: now() + 3600,
            previous_key_id: "old".into(),
            previous_key: STANDARD.encode(b"old-key-with-at-least-32-bytes-000"),
            previous_expires_at: now() + 600,
            previous_generation: 2,
            origin_previous_key_id: "origin-old".into(),
            origin_previous_key: STANDARD.encode(b"origin-old-key-at-least-32-bytes-"),
            origin_previous_generation: 1,
            origin_previous_expires_at: now() + 600,
            workload_tls: String::new(),
            trust_bundle: String::new(),
        }
    }
    fn accepts(ring: &OriginProofKeyRing, key: &[u8]) -> bool {
        let time = now();
        let uri = Uri::from_static("/api/v1/views/example/log");
        let mut headers = HeaderMap::new();
        headers.insert(
            ORIGIN_PROOF_HEADER,
            HeaderValue::from_str(&origin_proof_for(
                &Method::GET,
                uri.path(),
                time / 600 * 600,
                key,
                terrabase::regions::RegionCode::GRU,
            ))
            .unwrap(),
        );
        verify_origin_proof(
            &headers,
            &Method::GET,
            &uri,
            ring,
            time,
            terrabase::regions::RegionCode::GRU,
        )
    }
    #[tokio::test]
    async fn durable_rotation_changes_actual_verification_and_rejects_replays() {
        use std::os::unix::fs::PermissionsExt;
        let dir = std::env::temp_dir().join(format!(
            "rotation-{}-{}",
            std::process::id(),
            SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos()
        ));
        std::fs::create_dir(&dir).unwrap();
        std::fs::set_permissions(&dir, std::fs::Permissions::from_mode(0o700)).unwrap();
        let old = b"old-key-with-at-least-32-bytes-000";
        let origin_old = b"origin-old-key-at-least-32-bytes-";
        let new = b"new-key-with-at-least-32-bytes-000";
        let origin_new = b"origin-new-key-at-least-32-bytes-";
        let make = || {
            bind(
                &dir.join("control.sock"),
                &dir.join("state.json"),
                vec![42; 32],
                &OriginProofKeyRing::new(origin_old, None::<&[u8]>).unwrap(),
                &CapabilityKeyRing::new(old, None::<&[u8]>).unwrap(),
            )
            .unwrap()
        };
        let bound = make();
        let unauthenticated = handle(
            State(bound.service.clone()),
            HeaderMap::new(),
            Json(request("stage", "unauthorized")),
        )
        .await;
        assert!(matches!(unauthenticated, Err((StatusCode::UNAUTHORIZED, _))));
        let stage = request("stage", "stage");
        let mut cross_role_collision = request("stage", "cross-role-collision");
        cross_role_collision.origin_current_key = cross_role_collision.previous_key.clone();
        cross_role_collision.origin_current_key_id = "origin-collision".into();
        cross_role_collision.origin_previous_key.clear();
        cross_role_collision.origin_previous_key_id.clear();
        cross_role_collision.origin_previous_generation = 0;
        cross_role_collision.origin_previous_expires_at = 0;
        assert!(apply(&bound.service, &cross_role_collision).is_err());
        assert_eq!(bound.service.inner.lock().unwrap().durable.generation, 0);
        apply(&bound.service, &stage).unwrap();
        assert!(accepts(&bound.origin, origin_old));
        assert!(!accepts(&bound.origin, origin_new));
        assert!(apply(&bound.service, &request("activate", "early-activate")).is_err());
        apply(&bound.service, &request("verify", "verify")).unwrap();
        let activate = request("activate", "activate");
        apply(&bound.service, &activate).unwrap();
        apply(&bound.service, &activate).unwrap();
        {
            let live = bound.service.live.read().unwrap();
            assert_eq!(live.capability.current, new);
            assert_eq!(live.origin.current, origin_new);
            assert_ne!(live.capability.current, live.origin.current);
        }
        assert!(accepts(&bound.origin, origin_old));
        assert!(accepts(&bound.origin, origin_new));
        let mut conflict = request("fence", "activate");
        conflict.generation = 2;
        assert!(apply(&bound.service, &conflict).is_err());
        let (stop, signal) = tokio::sync::oneshot::channel();
        let task = tokio::spawn(bound.serve_until(async {
            let _ = signal.await;
        }));
        let socket = dir.join("control.sock");
        let response = tokio::task::spawn_blocking(move || {
            use std::io::{Read, Write};
            let mut stream = std::os::unix::net::UnixStream::connect(socket).unwrap();
            stream.set_read_timeout(Some(std::time::Duration::from_secs(2))).unwrap();
            let body = serde_json::to_string(&request("fence", "http-unauthorized")).unwrap();
            write!(stream, "POST /v1/credential-rotation HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: {}\r\n\r\n{}", body.len(), body).unwrap();
            let mut response = String::new(); stream.read_to_string(&mut response).unwrap(); response
        }).await.unwrap();
        assert!(response.starts_with("HTTP/1.1 401"));
        stop.send(()).unwrap();
        task.await.unwrap().unwrap();
        let restored = make();
        assert!(accepts(&restored.origin, origin_new));
        let mut retire = request("retire", "retire");
        retire.generation = 1;
        apply(&restored.service, &retire).unwrap();
        assert!(!accepts(&restored.origin, origin_old));
        assert!(accepts(&restored.origin, origin_new));
        {
            let live = restored.service.live.read().unwrap();
            assert!(live.capability.previous.is_some());
            assert!(live.origin.previous.is_none());
        }
        let mut retire_capability = request("retire", "retire-capability");
        retire_capability.generation = 2;
        apply(&restored.service, &retire_capability).unwrap();
        assert!(restored.service.live.read().unwrap().capability.previous.is_none());
        apply(&restored.service, &request("fence", "fence")).unwrap();
        assert!(!accepts(&restored.origin, origin_new));
        drop(restored);
        std::fs::remove_dir_all(dir).unwrap();
    }
    #[test]
    fn expired_material_and_debug_output_fail_closed() {
        let mut keys = KeyMaterial::new(b"secret-key-at-least-16", None).unwrap();
        keys.expires_at = now();
        assert!(keys.usable().is_none());
        assert!(!format!("{keys:?}").contains("secret"));
    }

    #[tokio::test]
    async fn malformed_initial_tls_is_rejected_before_private_state_creation() {
        let dir = std::env::temp_dir().join(format!(
            "rotation-tls-{}-{}",
            std::process::id(),
            SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos()
        ));
        std::fs::create_dir_all(&dir).unwrap();
        std::fs::set_permissions(&dir, std::os::unix::fs::PermissionsExt::from_mode(0o700))
            .unwrap();
        let ca_key = KeyPair::generate().unwrap();
        let mut ca_params = CertificateParams::new(Vec::new()).unwrap();
        ca_params.is_ca = IsCa::Ca(BasicConstraints::Unconstrained);
        let ca = ca_params.self_signed(&ca_key).unwrap();
        let key = KeyPair::generate().unwrap();
        let mut params = CertificateParams::new(vec!["localhost".into()]).unwrap();
        params.extended_key_usages = vec![ExtendedKeyUsagePurpose::ServerAuth];
        let leaf = params.signed_by(&key, &ca, &ca_key).unwrap();
        let workload = format!("{}{}", leaf.pem(), key.serialize_pem());
        let trust = ca.pem();
        let controller = terrabase_tbep::grpc::reloadable_tls::ReloadableTls::new(
            &leaf.pem(),
            &key.serialize_pem(),
            &trust,
        )
        .unwrap();
        let origin = OriginProofKeyRing::disabled();
        let capability = CapabilityKeyRing::disabled();
        let valid_dir = dir.join("valid");
        std::fs::create_dir(&valid_dir).unwrap();
        std::fs::set_permissions(&valid_dir, std::fs::Permissions::from_mode(0o700)).unwrap();
        let valid = bind_with_tls(
            &valid_dir.join("control.sock"),
            &valid_dir.join("state.json"),
            vec![42; 32],
            &origin,
            &capability,
            RotationTlsConfig {
                controller: controller.clone(),
                workload: workload.clone().into_bytes(),
                trust: trust.clone().into_bytes(),
            },
        )
        .unwrap();
        drop(valid);
        std::fs::remove_dir_all(valid_dir).unwrap();
        let result = bind_with_tls(
            &dir.join("control.sock"),
            &dir.join("state.json"),
            vec![42; 32],
            &origin,
            &capability,
            RotationTlsConfig {
                controller: controller.clone(),
                workload: b"malformed".to_vec(),
                trust: trust.clone().into_bytes(),
            },
        );
        assert!(result.is_err());
        assert!(!dir.join("control.sock").exists());
        assert!(!dir.join("state.json").exists());
        assert!(!dir.join("state.rotation-lock").exists());
        let result = bind_with_tls(
            &dir.join("control.sock"),
            &dir.join("state.json"),
            vec![42; 32],
            &origin,
            &capability,
            RotationTlsConfig {
                controller,
                workload: workload.into_bytes(),
                trust: b"malformed".to_vec(),
            },
        );
        assert!(result.is_err());
        assert!(!dir.join("control.sock").exists());
        assert!(!dir.join("state.json").exists());
        assert!(!dir.join("state.rotation-lock").exists());
        std::fs::remove_dir_all(dir).unwrap();
    }
}
