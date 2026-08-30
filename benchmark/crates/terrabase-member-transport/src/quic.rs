//! The engine-socket member lane: real QUIC sockets between members.
//!
//! PD-2026-08-26-13 D2 and ADR 0012 D-2 place networked member links on
//! engine-owned sockets — no broker between members. This lane binds one
//! QUIC endpoint per Member on loopback, mints one certificate per Member
//! from a lane certificate authority (no shared Member credentials), and
//! carries the same encoded member envelopes over mutual-TLS connections,
//! one uni stream per frame.
//!
//! The W4 fault vocabulary applies at the lane boundary of each direction —
//! delay, reorder, bounded drop, duplicate, and full partition — so the
//! fault classes fire on real frames crossing real sockets, and the same
//! per-direction counters observe them.
//!
//! Two fixture facts distinguish this lane from the in-process lane, both
//! recorded in every receipt it produces: the lane clock stays tick-based
//! (one tick per pump, all protocol vocabulary tick-valued), while each pump
//! additionally drains real socket traffic to a causal completion condition;
//! and delivery timing is the network's, so receipts record measured ticks
//! as lane facts of that run, never as reproducible constants. No
//! wall-clock value appears in any receipt and no performance claim is made
//! anywhere.
//!
//! Delivery causality, not elapsed time, ends a pump: a pump that flushed
//! frames to sockets returns once every frame it wrote has been read off
//! the wire by the addressed Member, signalled by the drain's explicit
//! progress accounting (frames taken, frames written, frames still awaited)
//! rather than by a clock. Socket futures are polled with short bounded
//! timeouts so an idle pass returns to the loop that decides on causality;
//! a generous hang guard bounds a pathological drain only, never a healthy
//! one, and a guard expiry is accounted as observable drops on the
//! directions still waiting — never a silent loss. The suite is therefore
//! deterministic in outcome on an idle host and under host load alike.

use std::collections::{BTreeMap, VecDeque};
use std::net::{IpAddr, Ipv4Addr, SocketAddr};
use std::sync::Arc;
use std::sync::Mutex;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Duration;

use crate::clock::LaneInstant;
use crate::envelope::{EncodedFrame, FrameKind};
use crate::identity::MemberId;
use crate::transport::{
    FrameClassBytes, FrameSelector, LinkControls, LinkStats, MAX_LINK_FRAMES, MemberTransport,
    TransportError,
};

/// The hang guard of one drain. A drain ends on its causal condition —
/// every frame written to a socket has been read off the wire — so this
/// bound never sizes a healthy delivery and no assertion depends on it; it
/// exists only so a genuinely stuck link cannot hang a pump. A guard expiry
/// is accounted as observable drops on the directions still waiting.
const DRAIN_GUARD: Duration = Duration::from_secs(30);

/// The per-poll timeout when watching for new connections or streams: one
/// non-blocking poll of a socket future, so an idle pass returns to the
/// loop that decides on causality rather than on elapsed time.
const POLL: Duration = Duration::from_millis(2);

/// One member's endpoint state.
struct EndpointState {
    endpoint: quinn::Endpoint,
    addr: SocketAddr,
}

/// One directed socket link: the fault controls and the outbound pipeline.
struct DirectedSocketLink {
    controls: LinkControls,
    held: VecDeque<(u64, EncodedFrame)>,
    outbound: VecDeque<EncodedFrame>,
    stats: LinkStats,
}

impl DirectedSocketLink {
    fn new() -> Self {
        Self {
            controls: LinkControls::default(),
            held: VecDeque::new(),
            outbound: VecDeque::new(),
            stats: LinkStats::default(),
        }
    }

    /// Enqueues one frame for the socket pipeline, applying the duplicate
    /// control and the outbound bound.
    #[allow(
        clippy::needless_pass_by_value,
        reason = "the queue owns its encoded copies; the caller hands ownership of the frame it minted"
    )]
    fn enqueue(&mut self, frame: &EncodedFrame) {
        let copies = if self.controls.duplicate { 2 } else { 1 };
        for _ in 0..copies {
            if self.outbound.len() >= MAX_LINK_FRAMES {
                self.stats.overflowed += 1;
                continue;
            }
            self.outbound.push_back(frame.clone());
            self.stats.delivered += 1;
            self.stats.bytes_delivered += frame.bytes.len() as u64;
            self.stats.payload_bytes_delivered += frame.payload_len as u64;
        }
    }
}

/// One drain's explicit progress accounting: the frames it took from the
/// outbound queues, the frames it wrote to a socket, and the frames it is
/// still awaiting on the wire. Shared between the flush and the inbound
/// driver so a drain can assert its causal completion condition — nothing
/// taken is still awaited — instead of watching a clock.
#[derive(Default)]
struct DrainProgress {
    taken: BTreeMap<(MemberId, MemberId), u64>,
    written: BTreeMap<(MemberId, MemberId), u64>,
    awaited: BTreeMap<(MemberId, MemberId), u64>,
}

impl DrainProgress {
    /// The drain's lock discipline: the progress lock is only ever held
    /// across synchronous steps, never across an await.
    fn locked(progress: &Mutex<Self>) -> std::sync::MutexGuard<'_, Self> {
        progress.lock().expect("the drain progress lock is held across synchronous steps only")
    }

    /// Records `count` frames taken from one direction's outbound queue.
    fn record_taken(progress: &Mutex<Self>, pair: &(MemberId, MemberId), count: u64) {
        Self::locked(progress).taken(pair, count);
    }

    /// Records one frame written to a socket and now awaited on the wire.
    fn record_written(progress: &Mutex<Self>, pair: &(MemberId, MemberId)) {
        Self::locked(progress).written(pair);
    }

    /// Retires one awaited frame its addressed Member read off the wire.
    fn record_arrival(progress: &Mutex<Self>, from: &MemberId, to: &MemberId) {
        Self::locked(progress).arrived(from, to);
    }

    /// The causal completion condition: nothing written is still awaited.
    fn is_settled(progress: &Mutex<Self>) -> bool {
        Self::locked(progress).settled()
    }

    /// The observable loss accounting of a drain that stopped before its
    /// causal condition: per direction, the frames it took but neither
    /// wrote to a socket nor had arrive.
    fn shortfall_of(progress: &Mutex<Self>) -> Vec<((MemberId, MemberId), u64)> {
        Self::locked(progress).shortfall()
    }

    /// Records `count` frames taken from one direction's outbound queue.
    fn taken(&mut self, pair: &(MemberId, MemberId), count: u64) {
        *self.taken.entry(pair.clone()).or_insert(0) += count;
    }

    /// Records one frame written to a socket and now awaited on the wire.
    fn written(&mut self, pair: &(MemberId, MemberId)) {
        *self.written.entry(pair.clone()).or_insert(0) += 1;
        *self.awaited.entry(pair.clone()).or_insert(0) += 1;
    }

    /// Retires one awaited frame its addressed Member read off the wire.
    fn arrived(&mut self, from: &MemberId, to: &MemberId) {
        let pair = (from.clone(), to.clone());
        if let Some(count) = self.awaited.get_mut(&pair) {
            *count = count.saturating_sub(1);
            if *count == 0 {
                self.awaited.remove(&pair);
            }
        }
    }

    /// The causal completion condition: nothing written is still awaited.
    fn settled(&self) -> bool {
        self.awaited.values().all(|count| *count == 0)
    }

    /// The observable loss accounting of a drain that stopped before its
    /// causal condition: per direction, the frames it took but neither
    /// wrote to a socket nor had arrive.
    fn shortfall(&self) -> Vec<((MemberId, MemberId), u64)> {
        self.taken
            .iter()
            .map(|(pair, taken)| {
                let written = self.written.get(pair).copied().unwrap_or(0);
                let awaited = self.awaited.get(pair).copied().unwrap_or(0);
                (pair.clone(), taken.saturating_sub(written) + awaited)
            })
            .filter(|(_, lost)| *lost > 0)
            .collect()
    }
}

/// The QUIC engine-socket lane.
pub struct QuicLane {
    runtime: tokio::runtime::Runtime,
    members: BTreeMap<MemberId, EndpointState>,
    links: BTreeMap<(MemberId, MemberId), DirectedSocketLink>,
    connections: BTreeMap<(MemberId, MemberId), quinn::Connection>,
    /// The inbound side's live connections per Member. A frame written on an
    /// existing connection is read only while that connection's streams are
    /// polled, so the lane keeps every accepted connection as readiness
    /// state across pumps instead of rediscovering it by timing.
    accepted: BTreeMap<MemberId, Vec<quinn::Connection>>,
    inbound: BTreeMap<MemberId, VecDeque<EncodedFrame>>,
    class_bytes: BTreeMap<FrameKind, FrameClassBytes>,
    clock: u64,
    inbound_overflowed: u64,
}

impl QuicLane {
    /// Creates the lane with one QUIC endpoint per member on loopback, each
    /// presenting its own lane-CA-signed certificate with client
    /// authentication required.
    ///
    /// # Errors
    ///
    /// Returns [`QuicLaneError`] when an endpoint cannot be bound or the
    /// lane certificates cannot be minted.
    pub fn new(member_names: &[&str]) -> Result<Self, QuicLaneError> {
        use rcgen::BasicConstraints;
        use rcgen::CertificateParams;
        use rcgen::ExtendedKeyUsagePurpose;
        use rcgen::IsCa;
        use rcgen::KeyPair;
        use rcgen::SanType;

        let runtime = tokio::runtime::Builder::new_multi_thread()
            .worker_threads(2)
            .enable_all()
            .build()
            .map_err(|_| QuicLaneError::Runtime)?;
        install_rustls_provider()?;
        // quinn's endpoints drive themselves on the ambient tokio runtime:
        // bind them inside this lane's runtime context.
        let _runtime_guard = runtime.enter();

        // The lane CA and the per-member leaves: one certificate per member,
        // no shared credentials.
        let issuer_key = KeyPair::generate().map_err(|_| QuicLaneError::Certificates)?;
        let mut issuer_params =
            CertificateParams::new(Vec::new()).map_err(|_| QuicLaneError::Certificates)?;
        issuer_params.is_ca = IsCa::Ca(BasicConstraints::Unconstrained);
        let issuer =
            issuer_params.self_signed(&issuer_key).map_err(|_| QuicLaneError::Certificates)?;
        let anchor = rustls::pki_types::CertificateDer::from(issuer.der().to_vec());
        let mut roots = rustls::RootCertStore::empty();
        roots.add(anchor.clone()).map_err(|_| QuicLaneError::Certificates)?;
        let verifier = rustls::server::WebPkiClientVerifier::builder(Arc::new(roots))
            .build()
            .map_err(|_| QuicLaneError::Certificates)?;

        let mut lane = Self {
            runtime,
            members: BTreeMap::new(),
            links: BTreeMap::new(),
            connections: BTreeMap::new(),
            accepted: BTreeMap::new(),
            inbound: BTreeMap::new(),
            class_bytes: BTreeMap::new(),
            clock: 0,
            inbound_overflowed: 0,
        };
        for name in member_names {
            let member = MemberId::try_new(name).map_err(|_| QuicLaneError::Certificates)?;
            let key = KeyPair::generate().map_err(|_| QuicLaneError::Certificates)?;
            let mut params = CertificateParams::new(vec![(*name).to_owned()])
                .map_err(|_| QuicLaneError::Certificates)?;
            params.subject_alt_names = vec![SanType::IpAddress(IpAddr::V4(Ipv4Addr::LOCALHOST))];
            params.extended_key_usages =
                vec![ExtendedKeyUsagePurpose::ServerAuth, ExtendedKeyUsagePurpose::ClientAuth];
            let certificate = params
                .signed_by(&key, &issuer, &issuer_key)
                .map_err(|_| QuicLaneError::Certificates)?;
            let cert_der = rustls::pki_types::CertificateDer::from(certificate.der().to_vec());
            let key_der = rustls::pki_types::PrivateKeyDer::Pkcs8(
                rustls::pki_types::PrivatePkcs8KeyDer::from(key.serialize_der()),
            );
            let rustls_server_config = rustls::ServerConfig::builder()
                .with_client_cert_verifier(verifier.clone())
                .with_single_cert(vec![cert_der.clone()], key_der.clone_key())
                .map_err(|_| QuicLaneError::Certificates)?;
            let server_config =
                quinn::crypto::rustls::QuicServerConfig::try_from(rustls_server_config)
                    .map_err(|_| QuicLaneError::Certificates)?;
            let client_config = rustls::ClientConfig::builder()
                .with_root_certificates({
                    let mut roots = rustls::RootCertStore::empty();
                    roots.add(anchor.clone()).map_err(|_| QuicLaneError::Certificates)?;
                    roots
                })
                .with_client_auth_cert(vec![cert_der], key_der)
                .map_err(|_| QuicLaneError::Certificates)?;
            let socket = std::net::UdpSocket::bind(SocketAddr::new(
                IpAddr::V4(Ipv4Addr::LOCALHOST),
                0,
            ))
            .map_err(|_| QuicLaneError::Bind)?;
            socket.set_nonblocking(true).map_err(|_| QuicLaneError::Bind)?;
            let mut endpoint = quinn::Endpoint::new(
                quinn::EndpointConfig::default(),
                Some(quinn::ServerConfig::with_crypto(Arc::new(server_config))),
                socket,
                Arc::new(quinn::TokioRuntime),
            )
            .map_err(|_| QuicLaneError::Bind)?;
            let quic_client_config =
                quinn::crypto::rustls::QuicClientConfig::try_from(client_config)
                    .map_err(|_| QuicLaneError::Certificates)?;
            endpoint
                .set_default_client_config(quinn::ClientConfig::new(Arc::new(quic_client_config)));
            let addr = endpoint.local_addr().map_err(|_| QuicLaneError::Bind)?;
            lane.members.insert(member.clone(), EndpointState { endpoint, addr });
            lane.accepted.insert(member.clone(), Vec::new());
            lane.inbound.insert(member, VecDeque::new());
        }
        Ok(lane)
    }

    /// Returns the socket address one member's endpoint is bound to.
    #[must_use]
    pub fn address_of(&self, member: &MemberId) -> Option<SocketAddr> {
        self.members.get(member).map(|state| state.addr)
    }

    /// Returns the number of received frames dropped because a member's
    /// inbound queue was at its bound.
    #[must_use]
    pub fn inbound_overflowed(&self) -> u64 {
        self.inbound_overflowed
    }

    /// Closes every member endpoint.
    pub fn close(&mut self) {
        for state in self.members.values() {
            state.endpoint.close(0_u32.into(), b"lane closed");
        }
    }

    /// Drains the socket pipeline to its causal completion condition:
    /// flushes every direction's outbound frames over its connection while
    /// concurrently driving the inbound side — the peer's `accept` must be
    /// polled for a connecting handshake to complete, so the two run joined
    /// — and returns once every frame this drain wrote to a socket has been
    /// read off the wire by the addressed Member. Delivery readiness is this
    /// explicit acknowledgement of arrival, never elapsed time: the inbound
    /// driver polls socket futures with short bounded timeouts so an idle
    /// pass returns to the loop, which decides on the progress accounting
    /// alone. Every flush failure is counted as an observable drop on the
    /// direction's counters, never a silent loss, and so is the pathological
    /// case of the hang guard expiring with frames still awaited.
    fn drain(&mut self) {
        let mut outbound: Vec<((MemberId, MemberId), Vec<EncodedFrame>)> = Vec::new();
        for (pair, link) in &mut self.links {
            if link.outbound.is_empty() {
                continue;
            }
            outbound.push((pair.clone(), link.outbound.drain(..).collect()));
        }
        if outbound.is_empty() {
            // A pure clock tick: nothing was accepted for socket delivery,
            // and an earlier drain left nothing awaited (a drain returns
            // only once the wire is settled), so this pump advances the
            // lane clock and touches no socket.
            return;
        }
        let progress = Arc::new(Mutex::new(DrainProgress::default()));
        for (pair, frames) in &outbound {
            DrainProgress::record_taken(&progress, pair, frames.len() as u64);
        }
        let flush_done = Arc::new(AtomicBool::new(false));
        // Arrival attribution: a connection accepted by one Member's endpoint
        // arrives from exactly one peer endpoint address, and the lane knows
        // every Member's address.
        let mut addresses: BTreeMap<SocketAddr, MemberId> = BTreeMap::new();
        for (member, state) in &self.members {
            addresses.insert(state.addr, member.clone());
        }

        let runtime = &self.runtime;
        let members = &self.members;
        let connections = &mut self.connections;
        let accepted = &mut self.accepted;
        let inbound = &mut self.inbound;
        let class_bytes = &mut self.class_bytes;
        let inbound_overflowed = &mut self.inbound_overflowed;
        let links = &mut self.links;
        let addresses = &addresses;
        let settled = runtime.block_on(async {
            // The outbound flush and the inbound driver run concurrently.
            let flush =
                flush_outbound(outbound, members, connections, links, &progress, &flush_done);
            let receive = async {
                loop {
                    if flush_done.load(Ordering::Acquire) && DrainProgress::is_settled(&progress) {
                        break;
                    }
                    drive_inbound(
                        members,
                        addresses,
                        accepted,
                        inbound,
                        class_bytes,
                        inbound_overflowed,
                        &progress,
                    )
                    .await;
                }
            };
            // The guard bounds a pathological drain only: the joined pair
            // above finishes on its own causal condition, so an expiry means
            // a link stopped delivering, and the shortfall is accounted
            // below rather than dropped silently.
            tokio::time::timeout(DRAIN_GUARD, async { tokio::join!(flush, receive) }).await.is_ok()
        });
        if settled {
            return;
        }
        // The hang guard expired with frames still awaited: account every
        // frame this drain took but neither wrote nor had arrive as an
        // observable drop on its direction, and forget the connections so
        // the next pump reconnects instead of waiting on a link that
        // stopped delivering.
        let shortfall = DrainProgress::shortfall_of(&progress);
        for (pair, lost) in shortfall {
            if let Some(link) = links.get_mut(&pair) {
                link.stats.dropped += lost;
            }
        }
        connections.clear();
    }
}

/// Flushes one drain's outbound batches over their connections, one uni
/// stream per frame. Every frame written to a socket is recorded in the
/// drain's progress accounting, so the drain can wait for its arrival;
/// every frame not written — a direction with no bound endpoint, a failed
/// connect, a failed stream open, a failed write, including the frames
/// behind the failing one — is counted as an observable drop on its
/// direction, never a silent loss. The flush records its own completion so
/// the inbound driver can distinguish "nothing awaited yet" from "nothing
/// awaited any more".
async fn flush_outbound(
    outbound: Vec<((MemberId, MemberId), Vec<EncodedFrame>)>,
    members: &BTreeMap<MemberId, EndpointState>,
    connections: &mut BTreeMap<(MemberId, MemberId), quinn::Connection>,
    links: &mut BTreeMap<(MemberId, MemberId), DirectedSocketLink>,
    progress: &Mutex<DrainProgress>,
    flush_done: &AtomicBool,
) {
    for ((from, to), frames) in outbound {
        let count_frames = frames.len() as u64;
        let drop_all = |links: &mut BTreeMap<(MemberId, MemberId), DirectedSocketLink>| {
            if let Some(link) = links.get_mut(&(from.clone(), to.clone())) {
                link.stats.dropped += count_frames;
            }
        };
        let Some(addr) = members.get(&to).map(|state| state.addr) else {
            drop_all(links);
            continue;
        };
        let Some(connection) = connect(connections, members, &from, addr).await else {
            drop_all(links);
            continue;
        };
        for (index, frame) in frames.into_iter().enumerate() {
            let remaining = count_frames - index as u64;
            let dropped = |links: &mut BTreeMap<(MemberId, MemberId), DirectedSocketLink>| {
                if let Some(link) = links.get_mut(&(from.clone(), to.clone())) {
                    link.stats.dropped += remaining;
                }
            };
            let Ok(mut stream) = connection.open_uni().await else {
                connections.remove(&(from.clone(), to.clone()));
                dropped(links);
                break;
            };
            #[allow(
                clippy::cast_possible_truncation,
                reason = "one envelope is bounded by MAX_FRAME_BYTES; the length always fits a u32"
            )]
            let length_prefix = u32::try_from(frame.bytes.len()).unwrap_or(u32::MAX);
            let mut framed = Vec::with_capacity(frame.bytes.len() + 4);
            framed.extend_from_slice(&length_prefix.to_le_bytes());
            framed.extend_from_slice(&frame.bytes);
            if stream.write_all(&framed).await.is_err() {
                connections.remove(&(from.clone(), to.clone()));
                dropped(links);
                break;
            }
            let _ = stream.finish();
            DrainProgress::record_written(progress, &(from.clone(), to.clone()));
        }
    }
    flush_done.store(true, Ordering::Release);
}

/// Drives one inbound pass over every member endpoint: accepts new
/// connections into the endpoint's retained readiness state and reads every
/// uni stream that is ready on any connection it holds, queueing each frame
/// into the addressed member's bounded inbound queue and retiring it from
/// the drain's awaited count. Each socket future is polled with a short
/// bounded timeout so the pass returns to the caller, which decides on
/// causality; the pass itself asserts nothing about time. Connections stay
/// in the retained set across passes and pumps — a frame written on an
/// existing connection is readable only while that connection's streams are
/// polled — and leave it when the peer closes them.
async fn drive_inbound(
    members: &BTreeMap<MemberId, EndpointState>,
    addresses: &BTreeMap<SocketAddr, MemberId>,
    accepted: &mut BTreeMap<MemberId, Vec<quinn::Connection>>,
    inbound: &mut BTreeMap<MemberId, VecDeque<EncodedFrame>>,
    class_bytes: &mut BTreeMap<FrameKind, FrameClassBytes>,
    inbound_overflowed: &mut u64,
    progress: &Mutex<DrainProgress>,
) {
    for (member, state) in members {
        // New connections first: a peer's handshake completes only while its
        // incoming connection is being accepted.
        while let Ok(Some(incoming)) = tokio::time::timeout(POLL, state.endpoint.accept()).await {
            if let Ok(connection) = incoming.await {
                accepted.entry(member.clone()).or_default().push(connection);
            }
        }
        // Then every retained connection: each is polled until its streams
        // are exhausted for this pass, and leaves the set only when the
        // peer closed it — a poll timeout is not closure, it is "nothing
        // ready on this connection right now".
        let live = accepted.entry(member.clone()).or_default();
        let mut index = 0;
        while index < live.len() {
            let connection = live[index].clone();
            if connection.close_reason().is_some() {
                live.remove(index);
                continue;
            }
            match tokio::time::timeout(POLL, connection.accept_uni()).await {
                // One ready stream: read its frame, then keep polling this
                // connection for the next one.
                Ok(Ok(mut stream)) => {
                    let from = addresses.get(&connection.remote_address()).cloned();
                    let Some(frame) = read_one_frame(&mut stream).await else { continue };
                    if let Ok(kind) = frame.peek_kind() {
                        class_bytes
                            .entry(kind)
                            .or_default()
                            .record(frame.bytes.len(), frame.payload_len);
                    }
                    if let Some(queue) = inbound.get_mut(member) {
                        if queue.len() >= MAX_LINK_FRAMES {
                            *inbound_overflowed += 1;
                        } else {
                            queue.push_back(frame);
                        }
                    }
                    if let Some(from) = &from {
                        DrainProgress::record_arrival(progress, from, member);
                    }
                }
                // The peer closed the connection: forget it.
                Ok(Err(_)) => {
                    live.remove(index);
                }
                // Nothing ready on this connection right now: keep it and
                // move on to the next.
                Err(_) => {
                    index += 1;
                }
            }
        }
    }
}

/// Returns the connection from `from` toward `addr`, connecting once and
/// reusing, with the member's own client certificate.
async fn connect(
    connections: &mut BTreeMap<(MemberId, MemberId), quinn::Connection>,
    members: &BTreeMap<MemberId, EndpointState>,
    from: &MemberId,
    addr: SocketAddr,
) -> Option<quinn::Connection> {
    let target =
        members.iter().find(|(_, state)| state.addr == addr).map(|(member, _)| member.clone())?;
    let pair = (from.clone(), target);
    if let Some(connection) = connections.get(&pair) {
        if connection.close_reason().is_none() {
            return Some(connection.clone());
        }
        connections.remove(&pair);
    }
    let endpoint = &members.get(&pair.0)?.endpoint;
    let connection = endpoint.connect(addr, "127.0.0.1").ok()?.await.ok()?;
    connections.insert(pair, connection.clone());
    Some(connection)
}

/// Reads exactly one length-prefixed frame from one uni stream.
async fn read_one_frame(stream: &mut quinn::RecvStream) -> Option<EncodedFrame> {
    let mut length = [0_u8; 4];
    stream.read_exact(&mut length).await.ok()?;
    let length = u32::from_le_bytes(length) as usize;
    if length == 0 || length > crate::envelope::MAX_FRAME_BYTES {
        return None;
    }
    let mut bytes = vec![0_u8; length];
    stream.read_exact(&mut bytes).await.ok()?;
    let payload_len = crate::envelope::peek_payload_len(&bytes)?;
    Some(EncodedFrame { bytes, payload_len })
}

impl MemberTransport for QuicLane {
    fn link(&mut self, from: &MemberId, to: &MemberId) -> &mut LinkControls {
        &mut self
            .links
            .entry((from.clone(), to.clone()))
            .or_insert_with(DirectedSocketLink::new)
            .controls
    }

    fn link_stats(&self, from: &MemberId, to: &MemberId) -> LinkStats {
        self.links.get(&(from.clone(), to.clone())).map_or_else(LinkStats::default, |link| {
            LinkStats {
                held: link.held.len() as u64,
                queued: link.outbound.len() as u64,
                ..link.stats
            }
        })
    }

    fn send(
        &mut self,
        from: &MemberId,
        to: &MemberId,
        frame: EncodedFrame,
    ) -> Result<(), TransportError> {
        let now = self.clock;
        let link =
            self.links.entry((from.clone(), to.clone())).or_insert_with(DirectedSocketLink::new);
        if link.controls.partition {
            link.stats.dropped += 1;
            return Ok(());
        }
        if let Some(remaining) = link.controls.drop_next {
            if remaining > 0 {
                link.controls.drop_next = Some(remaining - 1);
                link.stats.dropped += 1;
                return Ok(());
            }
            link.controls.drop_next = None;
        }
        if link.controls.delay_pumps > 0 {
            if link.held.len() >= MAX_LINK_FRAMES {
                link.stats.overflowed += 1;
                return Err(TransportError::LinkFull);
            }
            link.held.push_back((now + u64::from(link.controls.delay_pumps), frame));
            return Ok(());
        }
        link.enqueue(&frame);
        Ok(())
    }

    fn pump(&mut self) {
        self.clock = self.clock.saturating_add(1);
        let now = self.clock;
        // Release due hold-backs, newest-first under the reorder control.
        for link in self.links.values_mut() {
            if link.held.is_empty() {
                continue;
            }
            let due: Vec<EncodedFrame> = link
                .held
                .iter()
                .filter(|(at, _)| *at <= now)
                .map(|(_, frame)| frame.clone())
                .collect();
            link.held.retain(|(at, _)| *at > now);
            let order: Vec<EncodedFrame> =
                if link.controls.reorder { due.into_iter().rev().collect() } else { due };
            let before = link.stats.delivered;
            for frame in order {
                link.enqueue(&frame);
            }
            if link.controls.reorder && link.stats.delivered > before {
                link.stats.reordered += link.stats.delivered - before;
            }
        }
        self.drain();
    }

    fn now(&self) -> LaneInstant {
        LaneInstant::at_ticks(self.clock)
    }

    fn receive(&mut self, member: &MemberId, selector: FrameSelector) -> Vec<EncodedFrame> {
        let mut frames = Vec::new();
        if let Some(inbound) = self.inbound.get_mut(member) {
            let mut retained = VecDeque::new();
            while let Some(frame) = inbound.pop_front() {
                let matches = frame.peek_kind().is_ok_and(|kind| selector.matches(kind));
                if matches {
                    frames.push(frame);
                } else {
                    retained.push_back(frame);
                }
            }
            *inbound = retained;
        }
        frames
    }

    fn pending(&self, member: &MemberId) -> usize {
        self.inbound.get(member).map_or(0, VecDeque::len)
    }

    fn class_bytes(&self) -> Vec<(FrameKind, FrameClassBytes)> {
        [
            FrameKind::CatchUpRequest,
            FrameKind::CatchUpBatch,
            FrameKind::Push,
            FrameKind::AckReport,
            FrameKind::AckBatch,
        ]
        .into_iter()
        .filter_map(|kind| self.class_bytes.get(&kind).map(|bytes| (kind, *bytes)))
        .collect()
    }

    fn heal(&mut self) {
        for link in self.links.values_mut() {
            link.controls.clear();
        }
    }
}

/// The typed failure of the socket lane's construction.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum QuicLaneError {
    /// The async runtime could not be built.
    Runtime,
    /// A member endpoint could not be bound.
    Bind,
    /// The lane certificates could not be minted or configured.
    Certificates,
}

fn install_rustls_provider() -> Result<(), QuicLaneError> {
    #[cfg(feature = "production-fips")]
    {
        aws_lc_rs::try_fips_mode().map_err(|_| QuicLaneError::Certificates)?;
        let provider = rustls::crypto::aws_lc_rs::default_provider();
        if !provider.fips() {
            return Err(QuicLaneError::Certificates);
        }
        let _ = provider.install_default();
        if !rustls::crypto::CryptoProvider::get_default().is_some_and(|active| active.fips()) {
            return Err(QuicLaneError::Certificates);
        }
    }
    #[cfg(feature = "local-crypto")]
    {
        let _ = rustls::crypto::ring::default_provider().install_default();
    }
    Ok(())
}
