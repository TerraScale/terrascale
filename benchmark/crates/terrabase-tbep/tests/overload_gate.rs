//! The overload gate (P4): a server-wide bound on concurrently
//! executing host operations. A frame arriving at a saturated bound
//! answers the typed `overloaded` error with machine-readable retry
//! guidance; supervision health and query cancellation bypass the
//! bound — the operator must always observe the server, and the remedy
//! for a slow query must never be refused by the pressure it relieves.

use std::os::unix::net::UnixStream;
use std::sync::Arc;
use std::sync::mpsc::{Receiver, Sender, channel};
use std::time::Duration;

use terrabase_tbep::generated::{
    CancelOutcome, ChannelBindRequest, DocumentReadRequest, ErrorCode, QueryCancelRequest,
};
use terrabase_tbep::host::HostingError;
use terrabase_tbep::{IdempotencyKey, SyncDeliveryHost, TbepBody, TbepFrame, TbepHost, TbepServer};

/// A host whose one document read blocks until the test releases it —
/// and which announces its entry, so the test knows the slot is held.
/// Every other operation delegates to a real sync-delivery host.
struct BlockingHost {
    inner: SyncDeliveryHost,
    entered: Sender<()>,
    release: std::sync::Mutex<Receiver<()>>,
}

impl TbepHost for BlockingHost {
    fn bind_channel(
        &self,
        request: &ChannelBindRequest,
    ) -> Result<terrabase_tbep::generated::ChannelBindResponse, HostingError> {
        self.inner.bind_channel(request)
    }

    fn document_read(
        &self,
        tenant: &str,
        request: &DocumentReadRequest,
    ) -> Result<terrabase_tbep::generated::DocumentReadResponse, HostingError> {
        let _ = self.entered.send(());
        let release = self.release.lock().expect("the release latch is not poisoned");
        let _ = release.recv();
        drop(release);
        // The inner sync-delivery host refuses document reads (the
        // engine-backed host serves them); the gate test only needs the
        // read to answer at all.
        let _ = (tenant, request);
        Ok(terrabase_tbep::generated::DocumentReadResponse {
            found: false,
            document: None,
            position: None,
        })
    }

    fn document_write(
        &self,
        tenant: &str,
        request: &terrabase_tbep::generated::DocumentWriteRequest,
        key: &str,
    ) -> Result<terrabase_tbep::generated::DocumentWriteResponse, HostingError> {
        self.inner.document_write(tenant, request, key)
    }

    fn document_delete(
        &self,
        tenant: &str,
        request: &terrabase_tbep::generated::DocumentDeleteRequest,
        key: &str,
    ) -> Result<terrabase_tbep::generated::DocumentDeleteResponse, HostingError> {
        self.inner.document_delete(tenant, request, key)
    }

    fn query_execute(
        &self,
        tenant: &str,
        request: &terrabase_tbep::generated::QueryExecuteRequest,
    ) -> Result<terrabase_tbep::generated::QueryExecuteResponse, HostingError> {
        self.inner.query_execute(tenant, request)
    }

    fn query_cancel(
        &self,
        _tenant: &str,
        _request: &QueryCancelRequest,
    ) -> Result<terrabase_tbep::generated::QueryCancelResponse, HostingError> {
        // This host serves no queries, so every cancel names an unknown
        // one — the honest answer the bypassed frame still receives.
        Ok(terrabase_tbep::generated::QueryCancelResponse {
            outcome: terrabase_tbep::generated::CancelOutcome::UnknownQuery,
        })
    }

    fn family_read(
        &self,
        tenant: &str,
        request: &terrabase_tbep::generated::FamilyReadRequest,
    ) -> Result<terrabase_tbep::generated::FamilyReadResponse, HostingError> {
        self.inner.family_read(tenant, request)
    }

    fn family_write(
        &self,
        tenant: &str,
        request: &terrabase_tbep::generated::FamilyWriteRequest,
        key: &str,
    ) -> Result<terrabase_tbep::generated::FamilyWriteResponse, HostingError> {
        self.inner.family_write(tenant, request, key)
    }

    fn backbone_subscribe(
        &self,
        tenant: &str,
        request: &terrabase_tbep::generated::BackboneSubscribeRequest,
    ) -> Result<
        (
            terrabase_tbep::generated::BackboneSubscribeResponse,
            std::sync::mpsc::Receiver<terrabase_tbep::host::TbepNotification>,
        ),
        HostingError,
    > {
        self.inner.backbone_subscribe(tenant, request)
    }

    fn backbone_acknowledge(
        &self,
        tenant: &str,
        request: &terrabase_tbep::generated::BackboneAcknowledgeRequest,
    ) -> Result<terrabase_tbep::generated::BackboneAcknowledgeResponse, HostingError> {
        self.inner.backbone_acknowledge(tenant, request)
    }

    fn backbone_unsubscribe(
        &self,
        tenant: &str,
        request: &terrabase_tbep::generated::BackboneUnsubscribeRequest,
    ) -> Result<terrabase_tbep::generated::BackboneUnsubscribeResponse, HostingError> {
        self.inner.backbone_unsubscribe(tenant, request)
    }

    fn sync_session_setup(
        &self,
        tenant: &str,
        request: &terrabase_tbep::generated::SyncSessionSetupRequest,
    ) -> Result<terrabase_tbep::generated::SyncSessionSetupResponse, HostingError> {
        self.inner.sync_session_setup(tenant, request)
    }

    fn sync_delta_pull(
        &self,
        tenant: &str,
        request: &terrabase_tbep::generated::SyncDeltaPullRequest,
    ) -> Result<terrabase_tbep::generated::SyncDeltaPullResponse, HostingError> {
        self.inner.sync_delta_pull(tenant, request)
    }

    fn sync_snapshot_pull(
        &self,
        tenant: &str,
        request: &terrabase_tbep::generated::SyncSnapshotPullRequest,
    ) -> Result<terrabase_tbep::generated::SyncSnapshotPullResponse, HostingError> {
        self.inner.sync_snapshot_pull(tenant, request)
    }

    fn sync_checkpoint_ack(
        &self,
        tenant: &str,
        request: &terrabase_tbep::generated::SyncCheckpointAckRequest,
    ) -> Result<terrabase_tbep::generated::SyncCheckpointAckResponse, HostingError> {
        self.inner.sync_checkpoint_ack(tenant, request)
    }

    fn sync_mutation_submit(
        &self,
        tenant: &str,
        request: &terrabase_tbep::generated::SyncMutationSubmitRequest,
        key: &str,
    ) -> Result<terrabase_tbep::generated::SyncMutationSubmitResponse, HostingError> {
        self.inner.sync_mutation_submit(tenant, request, key)
    }

    fn sync_session_close(
        &self,
        tenant: &str,
        request: &terrabase_tbep::generated::SyncSessionCloseRequest,
    ) -> Result<terrabase_tbep::generated::SyncSessionCloseResponse, HostingError> {
        self.inner.sync_session_close(tenant, request)
    }

    fn topology_read(
        &self,
        tenant: &str,
    ) -> Result<terrabase_tbep::generated::TopologyReadResponse, HostingError> {
        self.inner.topology_read(tenant)
    }

    fn admin_status(&self) -> Result<terrabase_tbep::generated::AdminStatusResponse, HostingError> {
        self.inner.admin_status()
    }

    fn health(&self) -> Result<terrabase_tbep::generated::SupervisionHealthResponse, HostingError> {
        self.inner.health()
    }

    fn begin_shutdown(
        &self,
        request: &terrabase_tbep::generated::SupervisionShutdownRequest,
    ) -> Result<terrabase_tbep::generated::SupervisionShutdownResponse, HostingError> {
        self.inner.begin_shutdown(request)
    }
}

/// One connected client stream with a sequential call discipline.
struct Client {
    stream: UnixStream,
    correlation: u64,
}

impl Client {
    fn connect(socket: &std::path::Path) -> Self {
        Self::connect_as(socket, "tenant-a")
    }

    fn connect_as(socket: &std::path::Path, tenant: &str) -> Self {
        let mut stream = UnixStream::connect(socket).expect("the client connects");
        // Bind the channel first (the bind itself is a gated frame).
        let frame = TbepFrame::seal(
            ChannelBindRequest { principal: "backend".into(), tenant: tenant.into() },
            1,
            0,
            None,
        )
        .expect("seals");
        frame.write_stream(&mut stream).expect("the bind writes");
        let answer = TbepFrame::read_stream(&mut stream).expect("the bind answers").expect("frame");
        assert!(matches!(answer.body(), TbepBody::ChannelBindResponse(_)));
        Self { stream, correlation: 1 }
    }

    fn call(
        &mut self,
        body: impl Into<TbepBody>,
        idempotency: Option<&IdempotencyKey>,
    ) -> TbepFrame {
        self.correlation += 1;
        let frame =
            TbepFrame::seal(body, self.correlation, 0, idempotency).expect("the body seals");
        frame.write_stream(&mut self.stream).expect("the client writes");
        let deadline = std::time::Instant::now() + Duration::from_secs(10);
        loop {
            let answer =
                TbepFrame::read_stream(&mut self.stream).expect("healthy").expect("a frame");
            if answer.causation() == self.correlation {
                return answer;
            }
            assert!(std::time::Instant::now() < deadline, "the answer never arrived");
        }
    }
}

#[test]
fn a_saturated_bound_answers_the_typed_overloaded_error_and_bypasses_stay_serving() {
    let socket = std::env::temp_dir().join(format!(
        "tbep-overload-{}-{}.sock",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_or(0, |elapsed| elapsed.as_nanos())
    ));
    let (entered_sender, entered_receiver) = channel::<()>();
    let (release_sender, release_receiver) = channel::<()>();
    let host = Arc::new(BlockingHost {
        inner: SyncDeliveryHost::new(1),
        entered: entered_sender,
        release: std::sync::Mutex::new(release_receiver),
    });
    // The bound of one: a single held operation saturates the gate.
    let mut server =
        TbepServer::bind_with_inflight_bound(&socket, host, 1).expect("the listener binds");

    // Both channels bind before the gate is held.
    let mut holder = Client::connect(&socket);
    let mut probe = Client::connect(&socket);

    // The holder's read blocks inside the host, holding the only slot.
    let read_holder = std::thread::spawn(move || {
        let answer = holder.call(
            DocumentReadRequest {
                collection: "orders".into(),
                document: "d-01".into(),
                level: None,
            },
            None,
        );
        assert!(matches!(answer.body(), TbepBody::DocumentReadResponse(_)));
    });
    entered_receiver
        .recv_timeout(Duration::from_secs(10))
        .expect("the holding read entered the host");

    // A gated frame at the saturated bound answers the typed overloaded
    // error with machine-readable retry guidance.
    let answer = probe.call(
        DocumentReadRequest { collection: "orders".into(), document: "d-02".into(), level: None },
        None,
    );
    let TbepBody::Error(error) = answer.body() else {
        panic!("the saturated read answers an error, got {:?}", answer.kind());
    };
    assert_eq!(error.code, ErrorCode::Overloaded);
    assert!(
        error.detail.contains("retry_after_ms="),
        "the retry guidance is machine-readable: {}",
        error.detail
    );

    // Health bypasses the bound — the operator still observes.
    let answer = probe.call(terrabase_tbep::generated::SupervisionHealthRequest {}, None);
    assert!(
        matches!(answer.body(), TbepBody::SupervisionHealthResponse(_)),
        "health stays serving, got {:?}",
        answer.kind()
    );

    // Query cancellation bypasses the bound — the remedy is never
    // refused by the pressure it relieves.
    let answer = probe.call(QueryCancelRequest { query_id: 404 }, None);
    let TbepBody::QueryCancelResponse(response) = answer.body() else {
        panic!("the cancel answered, got {:?}", answer.kind());
    };
    assert!(matches!(response.outcome, CancelOutcome::UnknownQuery));

    // Release: the held read answers, and the gate frees for the next
    // gated frame. Closing the release channel after the one release
    // means a later read enters the host and returns immediately rather
    // than waiting for a second release that never comes.
    let _ = release_sender.send(());
    drop(release_sender);
    read_holder.join().expect("the holder finished");
    let answer = probe.call(
        DocumentReadRequest { collection: "orders".into(), document: "d-03".into(), level: None },
        None,
    );
    assert!(
        matches!(answer.body(), TbepBody::DocumentReadResponse(_)),
        "the freed gate serves again, got {:?}",
        answer.kind()
    );

    server.shutdown();
}

#[test]
fn a_saturated_tenant_share_overloads_only_that_tenant() {
    let socket = std::env::temp_dir().join(format!(
        "tbep-noisy-{}-{}.sock",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_or(0, |elapsed| elapsed.as_nanos())
    ));
    let (entered_sender, entered_receiver) = channel::<()>();
    let (release_sender, release_receiver) = channel::<()>();
    let host = Arc::new(BlockingHost {
        inner: SyncDeliveryHost::new(1),
        entered: entered_sender,
        release: std::sync::Mutex::new(release_receiver),
    });
    // The per-tenant share of one (the server-wide bound stays high at
    // 128): a single held operation saturates the noisy tenant's share
    // while the server as a whole has headroom — the noisy-neighbor
    // shape.
    let mut server = TbepServer::bind_with_bounds(&socket, host, 128, 1).expect("binds");

    // The noisy tenant's read blocks inside the host, holding the
    // tenant's only share.
    let holder_socket = socket.clone();
    let holder = std::thread::spawn(move || {
        let mut holder = Client::connect_as(&holder_socket, "tenant-noisy");
        let answer = holder.call(
            DocumentReadRequest {
                collection: "orders".into(),
                document: "held".into(),
                level: None,
            },
            None,
        );
        assert!(
            matches!(answer.body(), TbepBody::DocumentReadResponse(_)),
            "the held read answers after release"
        );
    });
    entered_receiver
        .recv_timeout(Duration::from_secs(10))
        .expect("the holding read entered the host");

    // The noisy tenant's NEXT frame answers the typed overload naming
    // the TENANT share — not the server-wide bound (1 of 128 used).
    let mut noisy_probe = Client::connect_as(&socket, "tenant-noisy");
    let answer = noisy_probe.call(
        DocumentReadRequest { collection: "orders".into(), document: "next".into(), level: None },
        None,
    );
    let TbepBody::Error(error) = answer.body() else {
        panic!("the noisy tenant's next frame overloads, got {:?}", answer.kind());
    };
    assert_eq!(error.code, ErrorCode::Overloaded);
    assert!(
        error.detail.contains("tenant in-flight share reached"),
        "the refusal names the tenant share: {}",
        error.detail
    );
    assert!(
        error.detail.contains("retry_after_ms="),
        "the retry guidance is machine-readable: {}",
        error.detail
    );

    // The quiet tenant proceeds untouched on the same server: its read
    // ENTERS the host while the noisy tenant saturates its own share —
    // the entry is the isolation proof, before any release.
    let quiet_socket = socket.clone();
    let quiet = std::thread::spawn(move || {
        let mut quiet = Client::connect_as(&quiet_socket, "tenant-quiet");
        let answer = quiet.call(
            DocumentReadRequest {
                collection: "orders".into(),
                document: "quiet".into(),
                level: None,
            },
            None,
        );
        assert!(
            matches!(answer.body(), TbepBody::DocumentReadResponse(_)),
            "the quiet tenant is served, got {:?}",
            answer.kind()
        );
    });
    entered_receiver
        .recv_timeout(Duration::from_secs(10))
        .expect("the quiet tenant's read entered the host under the noisy tenant's saturation");

    // Release everyone: closing the latch fails both held reads, both
    // tenants' frames answer, and the shares free.
    drop(release_sender);
    quiet.join().expect("the quiet tenant finished");
    holder.join().expect("the holder finished");

    // Health stays serving for the noisy tenant too — the bypass
    // classes of the server-wide gate bypass the tenant gate.
    let answer = noisy_probe.call(terrabase_tbep::generated::SupervisionHealthRequest {}, None);
    assert!(
        matches!(answer.body(), TbepBody::SupervisionHealthResponse(_)),
        "health stays serving under tenant pressure, got {:?}",
        answer.kind()
    );

    // Release freed the share: the noisy tenant serves again.
    let answer = noisy_probe.call(
        DocumentReadRequest { collection: "orders".into(), document: "after".into(), level: None },
        None,
    );
    assert!(
        matches!(answer.body(), TbepBody::DocumentReadResponse(_)),
        "the tenant's share frees after release, got {:?}",
        answer.kind()
    );
    server.shutdown();
}
