//! The GAP-R03 replication observation frames (PB-5035): the six typed
//! observation operations registered in the TBEP operation registry's
//! topology/admin block, their content-free record bodies, and the typed
//! fail-closed behavior of a shipped host that does not serve them yet.
//!
//! The engine-side seam is `src/replication/observation.rs` (decision
//! record `docs/replication/gapr03-replication-observations.md` D4): the
//! registry carries the wire slots, the generated bindings follow the
//! registry on both sides, and serving the frames from a live engine
//! process is the engine-backed host integrator action the record names.
//! Until that lands, a replication observation request on a bound channel
//! is answered with the typed `operation_refused` — never a misleading
//! `malformed_frame` "wrong direction".

use std::sync::Arc;

use terrabase_tbep::IdempotencyKey;
use terrabase_tbep::SyncDeliveryHost;
use terrabase_tbep::TbepFrame;
use terrabase_tbep::TbepServer;
use terrabase_tbep::envelope::stream_wire_bytes;
use terrabase_tbep::generated::BUDGETS;
use terrabase_tbep::generated::ClockStamp;
use terrabase_tbep::generated::ConflictOutcome;
use terrabase_tbep::generated::ConflictOutcomeKind;
use terrabase_tbep::generated::ConflictRecord;
use terrabase_tbep::generated::DenialRecord;
use terrabase_tbep::generated::DenialSeam;
use terrabase_tbep::generated::DepositionRecord;
use terrabase_tbep::generated::FencedRecord;
use terrabase_tbep::generated::FrameKind;
use terrabase_tbep::generated::FRAME_ROWS;
use terrabase_tbep::generated::ReplicationConflictsRequest;
use terrabase_tbep::generated::ReplicationConflictsResponse;
use terrabase_tbep::generated::ReplicationCursorRequest;
use terrabase_tbep::generated::ReplicationCursorResponse;
use terrabase_tbep::generated::ReplicationDenialsRequest;
use terrabase_tbep::generated::ReplicationDenialsResponse;
use terrabase_tbep::generated::ReplicationDepositionsRequest;
use terrabase_tbep::generated::ReplicationDepositionsResponse;
use terrabase_tbep::generated::ReplicationFencedRequest;
use terrabase_tbep::generated::ReplicationFencedResponse;
use terrabase_tbep::generated::ReplicationHealthRequest;
use terrabase_tbep::generated::ReplicationHealthResponse;
use terrabase_tbep::generated::ReplicationStatus;
use terrabase_tbep::generated::TbepBody;

/// The six observation operations with their request/response code pairs.
const OBSERVATION_CODES: &[(FrameKind, FrameKind, u16, u16)] = &[
    (FrameKind::ReplicationConflictsRequest, FrameKind::ReplicationConflictsResponse, 306, 562),
    (FrameKind::ReplicationFencedRequest, FrameKind::ReplicationFencedResponse, 307, 563),
    (
        FrameKind::ReplicationDepositionsRequest,
        FrameKind::ReplicationDepositionsResponse,
        308,
        564,
    ),
    (FrameKind::ReplicationCursorRequest, FrameKind::ReplicationCursorResponse, 309, 565),
    (FrameKind::ReplicationHealthRequest, FrameKind::ReplicationHealthResponse, 310, 566),
    (FrameKind::ReplicationDenialsRequest, FrameKind::ReplicationDenialsResponse, 311, 567),
];

#[test]
fn the_observation_operations_register_in_the_topology_admin_block() {
    for (request, response, request_code, response_code) in OBSERVATION_CODES {
        assert_eq!(request.code(), *request_code, "the request code");
        assert_eq!(response.code(), *response_code, "the response answers at request + 0x100");
        // The registry's 0x0130 block: topology/admin reads (304, 305) and
        // now the replication observation family.
        let row = FRAME_ROWS
            .iter()
            .find(|row| row.code == *request_code)
            .unwrap_or_else(|| panic!("the observation request has a taxonomy row"));
        assert_eq!(row.family, "replication", "the observation family");
        assert_eq!(row.class, "observation", "the observation budget class");
        // Observations are reads: no idempotency key rides them.
        assert!(
            !request.carries_idempotency(),
            "an observation request carries no idempotency key"
        );
    }
    // The observation budget class exists and is written.
    let budget = BUDGETS
        .iter()
        .find(|(class, _)| *class == "observation")
        .expect("the observation budget class is recorded");
    assert_eq!(*budget, ("observation", 65_536), "64 bounded records at identity width");
}

#[test]
fn observation_requests_refuse_an_idempotency_key() {
    // Reads never carry the retry identity: the pairing is enforced at
    // seal time, exactly as for the other read families.
    let key = IdempotencyKey::new("retry-observation").expect("valid key");
    let refused =
        TbepFrame::seal(ReplicationConflictsRequest {}, 1, 0, Some(&key)).expect_err("refuses");
    assert!(
        matches!(refused, terrabase_tbep::EnvelopeError::IdempotencyPairing(_)),
        "an observation request with a key refuses the pairing: {refused:?}"
    );
    let sealed = TbepFrame::seal(ReplicationConflictsRequest {}, 1, 0, None).expect("seals");
    assert_eq!(sealed.kind(), FrameKind::ReplicationConflictsRequest);
}

#[test]
fn observation_bodies_round_trip_content_free() {
    // One maximal-shape conflicts response: two operations, two members,
    // two stamps, a winner outcome — identities, clocks, and verdicts
    // only; no field of the record types can carry a document body.
    let record = ConflictRecord {
        tenant: "tenant-a".into(),
        operations: vec!["op-a".into(), "op-b".into()],
        members: vec!["m-a1".into(), "m-b1".into()],
        clocks: vec![ClockStamp { ticks: 10, counter: 1 }, ClockStamp { ticks: 10, counter: 2 }],
        outcome: ConflictOutcome { kind: ConflictOutcomeKind::Winner, member: "m-b1".into() },
    };
    let response = ReplicationConflictsResponse {
        member: "m-b1".into(),
        records: vec![record; 64],
    };
    let frame = TbepFrame::seal(response, 7, 6, None).expect("the response seals");
    let wire = frame.to_bytes();
    let decoded = TbepFrame::from_bytes(&wire).expect("the response decodes");
    assert_eq!(decoded.kind(), FrameKind::ReplicationConflictsResponse);
    match decoded.body() {
        TbepBody::ReplicationConflictsResponse(body) => {
            assert_eq!(body.member, "m-b1");
            assert_eq!(body.records.len(), 64);
            assert_eq!(body.records[0].outcome.kind, ConflictOutcomeKind::Winner);
            assert_eq!(body.records[0].clocks[1].counter, 2);
        }
        other => panic!("the decoded body is the conflicts response: {other:?}"),
    }
    assert_eq!(decoded.to_bytes(), wire, "the re-encode is byte-exact");
    let budget = BUDGETS
        .iter()
        .find_map(|(class, bytes)| (*class == "observation").then_some(*bytes))
        .expect("the class has a budget");
    assert!(
        stream_wire_bytes(&decoded) <= budget,
        "a maximal bounded conflicts response fits its written budget"
    );

    // The remaining record shapes round-trip with their typed facts.
    let fenced = TbepFrame::seal(
        ReplicationFencedResponse {
            member: "m-b1".into(),
            records: vec![FencedRecord {
                range: "main".into(),
                batch_epoch: 1,
                highest_epoch: 2,
                source: "m-a1".into(),
            }],
        },
        8,
        6,
        None,
    )
    .expect("seals");
    assert!(matches!(
        TbepFrame::from_bytes(&fenced.to_bytes()).expect("decodes").body(),
        TbepBody::ReplicationFencedResponse(_)
    ));
    let depositions = TbepFrame::seal(
        ReplicationDepositionsResponse {
            member: "m-a1".into(),
            records: vec![DepositionRecord {
                range: "main".into(),
                held_epoch: 1,
                owner_epoch: 2,
                new_owner: "m-b1".into(),
            }],
        },
        9,
        6,
        None,
    )
    .expect("seals");
    assert!(matches!(
        TbepFrame::from_bytes(&depositions.to_bytes()).expect("decodes").body(),
        TbepBody::ReplicationDepositionsResponse(_)
    ));
    let cursor = TbepFrame::seal(
        ReplicationCursorResponse { member: "m-a1".into(), position: Some(42) },
        10,
        6,
        None,
    )
    .expect("seals");
    match TbepFrame::from_bytes(&cursor.to_bytes()).expect("decodes").body() {
        TbepBody::ReplicationCursorResponse(body) => assert_eq!(body.position, Some(42)),
        other => panic!("the cursor body decodes: {other:?}"),
    }
    let empty_cursor = TbepFrame::seal(
        ReplicationCursorResponse { member: "m-a1".into(), position: None },
        11,
        6,
        None,
    )
    .expect("seals");
    match TbepFrame::from_bytes(&empty_cursor.to_bytes()).expect("decodes").body() {
        TbepBody::ReplicationCursorResponse(body) => assert_eq!(body.position, None),
        other => panic!("the empty cursor decodes: {other:?}"),
    }
    let health = TbepFrame::seal(
        ReplicationHealthResponse { member: "m-a1".into(), status: ReplicationStatus::Degraded },
        12,
        6,
        None,
    )
    .expect("seals");
    match TbepFrame::from_bytes(&health.to_bytes()).expect("decodes").body() {
        TbepBody::ReplicationHealthResponse(body) => {
            assert_eq!(body.status, ReplicationStatus::Degraded);
        }
        other => panic!("the health body decodes: {other:?}"),
    }
    let denials = TbepFrame::seal(
        ReplicationDenialsResponse {
            member: "m-a1".into(),
            records: vec![DenialRecord {
                seam: DenialSeam::ServeRequest,
                scope_tenant: "tenant-b".into(),
                attempted_tenant: "tenant-a".into(),
                counterparty: "m-b1".into(),
            }],
        },
        13,
        6,
        None,
    )
    .expect("seals");
    assert!(matches!(
        TbepFrame::from_bytes(&denials.to_bytes()).expect("decodes").body(),
        TbepBody::ReplicationDenialsResponse(_)
    ));
}

#[test]
fn observation_requests_are_refused_typed_until_a_host_serves_them() {
    // The shipped hosts do not serve the observation frames; the wire
    // must say so typed (operation_refused), not misreport the request
    // as a direction violation.
    let socket =
        std::env::temp_dir().join(format!("tbep-uds-observation-{}.sock", std::process::id()));
    let _server = TbepServer::bind(&socket, Arc::new(SyncDeliveryHost::new(3)))
        .expect("the UDS listener binds");
    let mut stream = std::os::unix::net::UnixStream::connect(&socket).expect("connects");
    let bind = TbepFrame::seal(
        terrabase_tbep::generated::ChannelBindRequest {
            principal: "backend".into(),
            tenant: "tenant-a".into(),
        },
        1,
        0,
        None,
    )
    .expect("binds");
    bind.write_stream(&mut stream).expect("the bind writes");
    let bound = TbepFrame::read_stream(&mut stream).expect("the bind answers").expect("a frame");
    assert!(matches!(bound.body(), TbepBody::ChannelBindResponse(_)));

    for (request, _, _, _) in OBSERVATION_CODES.iter() {
        let body = match request {
            FrameKind::ReplicationConflictsRequest => {
                TbepBody::from(ReplicationConflictsRequest {})
            }
            FrameKind::ReplicationFencedRequest => TbepBody::from(ReplicationFencedRequest {}),
            FrameKind::ReplicationDepositionsRequest => {
                TbepBody::from(ReplicationDepositionsRequest {})
            }
            FrameKind::ReplicationCursorRequest => TbepBody::from(ReplicationCursorRequest {}),
            FrameKind::ReplicationHealthRequest => TbepBody::from(ReplicationHealthRequest {}),
            FrameKind::ReplicationDenialsRequest => TbepBody::from(ReplicationDenialsRequest {}),
            other => panic!("an unknown observation kind in the table: {other:?}"),
        };
        let frame = TbepFrame::seal(body, 2, 0, None).expect("the observation request seals");
        frame.write_stream(&mut stream).expect("the request writes");
        let answer = TbepFrame::read_stream(&mut stream).expect("the server answers").expect("a frame");
        assert_eq!(answer.causation(), 2, "the answer names its request");
        match answer.body() {
            TbepBody::Error(error) => {
                assert_eq!(
                    error.code,
                    terrabase_tbep::generated::ErrorCode::OperationRefused,
                    "the refusal is typed: {:?}",
                    error.detail
                );
            }
            other => panic!("the unserved observation is refused typed, got {other:?}"),
        }
    }
}
