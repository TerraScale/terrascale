//! Synthetic controls for the parity helpers; no network, credentials or tenant.
use super::*;
use serde_json::json;

fn op(delay: Duration, result: Result<(), &'static str>) -> Op {
    Arc::new(move || {
        Box::pin(async move {
            if !delay.is_zero() {
                sleep(delay).await;
            }
            result
        })
    })
}

fn seed() -> Seed {
    let body = serde_json::from_str(&seed_body(256)).unwrap();
    Seed {
        size: 256,
        id: DocumentId::generate(),
        version: format!("tbv1-{}", "0".repeat(32)),
        body,
    }
}

#[test]
fn peers_cover_every_region_and_reject_malformed_catalogs() {
    let peers = "# ts-orch\none gru 100.64.0.1\ntwo sjc 100.64.0.2\n\nthree fra 100.64.0.3\nfour gru 100.64.0.4\n";
    assert_eq!(parse_peers(peers).unwrap(), ["fra", "gru", "sjc"]);
    assert_eq!(
        parse_peers(&format!("{peers}five nrt 100.64.0.5")).unwrap(),
        ["fra", "gru", "nrt", "sjc"]
    );
    assert_eq!(
        parse_peers("one gru 100.64.0.1\ntwo sjc 100.64.0.2"),
        Err("a required serving region is absent")
    );
    for bad in [
        "one gru",
        "one gru host",
        "one GRU 100.64.0.1",
        "one gru 100.64.0.1 extra",
    ] {
        assert_eq!(parse_peers(bad), Err("peer catalog is malformed"));
    }
}

#[test]
fn seeds_are_exact_compact_json() {
    for size in [256, 4096] {
        let text = seed_body(size);
        assert_eq!(text.len(), size);
        assert_eq!(
            serde_json::to_string(&serde_json::from_str::<Value>(&text).unwrap()).unwrap(),
            text
        );
    }
}

#[test]
fn raw_answers_need_status_protocol_one_region_and_the_document() {
    let seed = seed();
    let good = json!({"id": seed.id.to_string(), "version": seed.version, "state": "value", "document": seed.body});
    let body = good.to_string();
    let (gru, sjc): (&[u8], &[u8]) = (b"gru", b"sjc");
    let answer = |status, protocol, regions: &[&[u8]], body: &str| {
        let answer = RawAnswer {
            status,
            protocol,
            regions: regions.to_vec(),
            body: body.as_bytes(),
        };
        check_raw(&answer, Http::HTTP_2, "gru", &seed)
    };
    assert_eq!(answer(StatusCode::OK, Http::HTTP_2, &[gru], &body), Ok(()));
    assert_eq!(
        answer(StatusCode::NOT_FOUND, Http::HTTP_2, &[gru], &body),
        Err("status")
    );
    assert_eq!(
        answer(StatusCode::OK, Http::HTTP_11, &[gru], &body),
        Err("protocol")
    );
    assert_eq!(
        answer(StatusCode::OK, Http::HTTP_2, &[], &body),
        Err("region")
    );
    assert_eq!(
        answer(StatusCode::OK, Http::HTTP_2, &[gru, gru], &body),
        Err("region")
    );
    assert_eq!(
        answer(StatusCode::OK, Http::HTTP_2, &[sjc], &body),
        Err("region")
    );
    assert_eq!(
        answer(StatusCode::OK, Http::HTTP_2, &[gru], "{"),
        Err("body")
    );
    for (field, value) in [
        ("id", json!(DocumentId::generate().to_string())),
        ("version", json!("tbv1-1")),
        ("state", json!("conflict")),
        ("document", json!({"payload": "y"})),
    ] {
        let mut wrong = good.clone();
        wrong[field] = value;
        assert_eq!(
            answer(StatusCode::OK, Http::HTTP_2, &[gru], &wrong.to_string()),
            Err("document")
        );
    }
}

#[test]
fn reservoir_stays_bounded() {
    let mut measured = Measured::default();
    for i in 0..20_000 {
        measured.record(Duration::from_millis(i), Instant::now());
    }
    assert_eq!((measured.ok, measured.latencies.len()), (20_000, RESERVOIR));
    assert_eq!(Measured::quantile(&[1.0, 2.0, 3.0, 4.0], 0.5), Some(2.0));
    assert_eq!(Measured::quantile(&[], 0.99), None);
}

#[tokio::test(start_paused = true)]
async fn window_keeps_slow_tail_and_extends_throughput_through_drain() {
    // Starts at 0 s (warmup), 2.5 s and 5 s (window); the last completes at 7.5 s.
    let measured = measure(op(Duration::from_millis(2500), Ok(())), 1).await;
    assert_eq!(
        (
            measured.started,
            measured.ok,
            measured.latencies.len(),
            measured.failed
        ),
        (3, 2, 2, 0)
    );
    assert!(!measured.stuck);
    assert!(
        (measured.elapsed_s - 5.5).abs() < 1e-6,
        "{}",
        measured.elapsed_s
    );
}

#[tokio::test(start_paused = true)]
async fn failures_stop_workers_and_stuck_cells_are_reported() {
    let failed = measure(op(Duration::ZERO, Err("transport")), 4).await;
    assert!(failed.failed >= 1 && failed.failed == failed.started);
    assert_eq!((failed.error, failed.ok), (Some("transport"), 0));
    let stuck = measure(op(Duration::from_secs(100), Ok(())), 2).await;
    assert_eq!((stuck.stuck, stuck.started, stuck.ok), (true, 2, 0));
}

#[tokio::test(start_paused = true)]
async fn worker_panics_are_recorded_as_failures() {
    let panics: Op = Arc::new(|| Box::pin(async { panic!("synthetic worker failure") }));
    let measured = measure(panics, 1).await;
    assert_eq!((measured.started, measured.failed, measured.ok), (1, 1, 0));
    assert_eq!(measured.error, Some("worker_failed"));
    assert!(!measured.stuck);
}

#[tokio::test(start_paused = true)]
async fn readiness_is_bounded_and_stop_interrupts() {
    assert!(!ready(&op(Duration::from_secs(20), Ok(()))).await);
    assert!(ready(&op(Duration::from_secs(1), Ok(()))).await);
    let (interrupt, receiver) = watch::channel(false);
    let stop = Stop {
        interrupt: receiver,
        deadline: Instant::now() + Duration::from_secs(3600),
    };
    interrupt.send(true).unwrap();
    assert!(matches!(
        stop.guard(sleep(Duration::from_secs(60))).await,
        Err(Failure::Interrupted)
    ));
}
