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
//! (one tick per pump, all protocol vocabulary tick-valued), but each pump
//! additionally drains real socket traffic inside a bounded wall-clock
//! window; and delivery timing is the network's, so receipts record
//! measured ticks as lane facts of that run, never as reproducible
//! constants. No wall-clock value appears in any receipt and no performance
//! claim is made anywhere.

use std::collections::{BTreeMap, VecDeque};
use std::net::{IpAddr, Ipv4Addr, SocketAddr};
use std::sync::Arc;
use std::time::Duration;

use crate::clock::LaneInstant;
use crate::envelope::{EncodedFrame, FrameKind};
use crate::identity::MemberId;
use crate::transport::{
    FrameClassBytes, FrameSelector, LinkControls, LinkStats, MAX_LINK_FRAMES, MemberTransport,
    TransportError,
};

/// The default wall-clock window one pump drains socket traffic inside.
pub const DEFAULT_PUMP_WINDOW: Duration = Duration::from_millis(300);

/// The per-poll timeout when watching for new connections or streams.
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

/// The QUIC engine-socket lane.
pub struct QuicLane {
    runtime: tokio::runtime::Runtime,
    members: BTreeMap<MemberId, EndpointState>,
    links: BTreeMap<(MemberId, MemberId), DirectedSocketLink>,
    connections: BTreeMap<(MemberId, MemberId), quinn::Connection>,
    inbound: BTreeMap<MemberId, VecDeque<EncodedFrame>>,
    class_bytes: BTreeMap<FrameKind, FrameClassBytes>,
    clock: u64,
    window: Duration,
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
        let _ = rustls::crypto::ring::default_provider().install_default();
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
            inbound: BTreeMap::new(),
            class_bytes: BTreeMap::new(),
            clock: 0,
            window: DEFAULT_PUMP_WINDOW,
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
            let mut endpoint = quinn::Endpoint::server(
                quinn::ServerConfig::with_crypto(Arc::new(server_config)),
                SocketAddr::new(IpAddr::V4(Ipv4Addr::LOCALHOST), 0),
            )
            .map_err(|_| QuicLaneError::Bind)?;
            let quic_client_config =
                quinn::crypto::rustls::QuicClientConfig::try_from(client_config)
                    .map_err(|_| QuicLaneError::Certificates)?;
            endpoint
                .set_default_client_config(quinn::ClientConfig::new(Arc::new(quic_client_config)));
            let addr = endpoint.local_addr().map_err(|_| QuicLaneError::Bind)?;
            lane.members.insert(member.clone(), EndpointState { endpoint, addr });
            lane.inbound.insert(member, VecDeque::new());
        }
        Ok(lane)
    }

    /// Overrides the pump drain window (a fixture fact; the default is
    /// [`DEFAULT_PUMP_WINDOW`]).
    #[must_use]
    pub fn with_pump_window(mut self, window: Duration) -> Self {
        self.window = window;
        self
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

    /// Drains the socket pipeline inside the pump window: flushes every
    /// direction's outbound frames over its connection while concurrently
    /// driving the inbound side — the peer's `accept` must be polled for a
    /// connecting handshake to complete, so the two run joined — and
    /// receives every frame that arrived into the addressed member's inbound
    /// queue. Every flush failure is counted as an observable drop on the
    /// direction's counters, never a silent loss.
    fn drain(&mut self) {
        let window = self.window;
        let mut outbound: Vec<((MemberId, MemberId), Vec<EncodedFrame>)> = Vec::new();
        for (pair, link) in &mut self.links {
            if link.outbound.is_empty() {
                continue;
            }
            let frames: Vec<EncodedFrame> = link.outbound.drain(..).collect();
            outbound.push((pair.clone(), frames));
        }
        let runtime = &self.runtime;
        let members = &self.members;
        let connections = &mut self.connections;
        let inbound = &mut self.inbound;
        let class_bytes = &mut self.class_bytes;
        let inbound_overflowed = &mut self.inbound_overflowed;
        let links = &mut self.links;
        runtime.block_on(async {
            let deadline = tokio::time::Instant::now() + window;
            // The outbound flush and the inbound driver run concurrently.
            let flush = async {
                for ((from, to), frames) in outbound {
                    let Some(addr) = members.get(&to).map(|state| state.addr) else {
                        continue;
                    };
                    let count_frames = frames.len() as u64;
                    let drop_all =
                        |links: &mut BTreeMap<(MemberId, MemberId), DirectedSocketLink>| {
                            if let Some(link) = links.get_mut(&(from.clone(), to.clone())) {
                                link.stats.dropped += count_frames;
                            }
                        };
                    let connected =
                        tokio::time::timeout(window, connect(connections, members, &from, addr))
                            .await;
                    let Ok(Some(connection)) = connected else {
                        drop_all(links);
                        continue;
                    };
                    for frame in frames {
                        let opened = tokio::time::timeout(window, connection.open_uni()).await;
                        let Ok(Ok(mut stream)) = opened else {
                            connections.remove(&(from.clone(), to.clone()));
                            if let Some(link) = links.get_mut(&(from.clone(), to.clone())) {
                                link.stats.dropped += 1;
                            }
                            break;
                        };
                        #[allow(clippy::cast_possible_truncation, reason = "one envelope is bounded by MAX_FRAME_BYTES; the length always fits a u32")]
                        let length_prefix = u32::try_from(frame.bytes.len()).unwrap_or(u32::MAX);
                        let mut framed = Vec::with_capacity(frame.bytes.len() + 4);
                        framed.extend_from_slice(&length_prefix.to_le_bytes());
                        framed.extend_from_slice(&frame.bytes);
                        let wrote = tokio::time::timeout(window, stream.write_all(&framed)).await;
                        if wrote.map_or(true, |result| result.is_err()) {
                            if let Some(link) = links.get_mut(&(from.clone(), to.clone())) {
                                link.stats.dropped += 1;
                            }
                            break;
                        }
                        let _ = stream.finish();
                    }
                }
            };
            let receive = async {
                loop {
                    let quiet =
                        drive_inbound(members, inbound, class_bytes, inbound_overflowed).await;
                    if quiet || tokio::time::Instant::now() >= deadline {
                        break;
                    }
                }
            };
            tokio::join!(flush, receive);
            // One more bounded pass for frames that arrived while the flush
            // was writing, still inside the window.
            let _ = drive_inbound(members, inbound, class_bytes, inbound_overflowed).await;
        });
    }
}

/// Drives one inbound pass over every member endpoint: accepts new
/// connections and reads every uni stream that is ready, queueing its frame
/// into the addressed member's bounded inbound queue. Returns whether the
/// pass stayed quiet (nothing arrived).
async fn drive_inbound(
    members: &BTreeMap<MemberId, EndpointState>,
    inbound: &mut BTreeMap<MemberId, VecDeque<EncodedFrame>>,
    class_bytes: &mut BTreeMap<FrameKind, FrameClassBytes>,
    inbound_overflowed: &mut u64,
) -> bool {
    let mut quiet = true;
    for (member, state) in members {
        while let Ok(Some(incoming)) = tokio::time::timeout(POLL, state.endpoint.accept()).await {
            quiet = false;
            let Ok(connection) = incoming.await else { break };
            while let Ok(Ok(mut stream)) = tokio::time::timeout(POLL, connection.accept_uni()).await
            {
                quiet = false;
                if let Some(frame) = read_one_frame(&mut stream).await {
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
                }
            }
        }
    }
    quiet
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
        [FrameKind::CatchUpRequest, FrameKind::CatchUpBatch, FrameKind::Push, FrameKind::AckReport]
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
