//! The same-host UDS characterization harness (SE-14): the measurement
//! behind the binding evidence receipt (`docs/sync/tbep-binding-evidence.md`).
//!
//! This is engineering evidence, not a public claim (PD-2026-08-26-03
//! claim discipline unchanged): the numbers this harness prints are
//! local timings on one machine under one run, recorded with method and
//! environment in the receipt, and no latency, throughput, or comparison
//! statement may be derived from them without a Benchmark Receipt from
//! the exclusive benchmark lane. The deterministic half of the receipt —
//! per-frame byte accounting — comes from the golden corpus, not from
//! here.
//!
//! Run from the crate directory:
//!
//! ```text
//! cargo test --test uds_characterization -- --ignored --nocapture
//! ```

use std::os::unix::net::UnixStream;
use std::sync::Arc;
use std::time::Duration;
use std::time::Instant;

use terrabase_tbep::IdempotencyKey;
use terrabase_tbep::SyncDeliveryHost;
use terrabase_tbep::TbepFrame;
use terrabase_tbep::TbepServer;
use terrabase_tbep::TbepValue;
use terrabase_tbep::generated::ChannelBindRequest;
use terrabase_tbep::generated::SupervisionHealthRequest;
use terrabase_tbep::generated::SyncChange;
use terrabase_tbep::generated::SyncField;
use terrabase_tbep::generated::SyncMutationSubmitRequest;
use terrabase_tbep::generated::SyncSessionSetupRequest;

/// One measured sample set: count, total, and the sorted latencies.
struct Samples(Vec<Duration>);

impl Samples {
    fn mean(&self) -> Duration {
        self.0.iter().sum::<Duration>() / u32::try_from(self.0.len()).expect("fits")
    }

    fn percentile(&self, fraction: u32, denominator: u32) -> Duration {
        let denominator = usize::try_from(denominator).expect("fits");
        let index =
            self.0.len().saturating_sub(1) / denominator * usize::try_from(fraction).expect("fits");
        self.0[index.min(self.0.len() - 1)]
    }
}

#[test]
#[ignore = "measurement harness: prints the engineering evidence recorded in docs/sync/tbep-binding-evidence.md"]
fn same_host_uds_round_trip_characterization() {
    const CONTROL_ROUNDS: usize = 10_000;
    const MUTATION_ROUNDS: usize = 2_000;
    let socket = std::env::temp_dir().join(format!("tbep-char-{}.sock", std::process::id()));
    let mut server =
        TbepServer::bind(&socket, Arc::new(SyncDeliveryHost::new(1))).expect("the listener binds");
    let mut stream = UnixStream::connect(&socket).expect("connects");
    let mut correlation = 0u64;

    let mut round_trip = |body: terrabase_tbep::generated::TbepBody,
                          idempotency: Option<&IdempotencyKey>|
     -> Duration {
        correlation += 1;
        let frame = TbepFrame::seal(body, correlation, 0, idempotency).expect("seals");
        let start = Instant::now();
        frame.write_stream(&mut stream).expect("writes");
        loop {
            let answer = TbepFrame::read_stream(&mut stream)
                .expect("the stream stays healthy")
                .expect("the answer arrives");
            if answer.causation() == correlation {
                return start.elapsed();
            }
        }
    };

    // Bind the channel (tenant-a) and open a session.
    round_trip(
        ChannelBindRequest { principal: "backend".into(), tenant: "tenant-a".into() }.into(),
        None,
    );
    let session = round_trip(
        SyncSessionSetupRequest { collection: "orders".into(), resume: None }.into(),
        None,
    );

    // Warm-up (JIT-free workload; still discarded per method discipline).
    for _ in 0..200 {
        round_trip(SupervisionHealthRequest {}.into(), None);
    }

    // The measured rows: the minimal control frame and the mutation.

    let mut control = Samples(Vec::with_capacity(CONTROL_ROUNDS));
    for _ in 0..CONTROL_ROUNDS {
        control.0.push(round_trip(SupervisionHealthRequest {}.into(), None));
    }
    control.0.sort();

    let key = IdempotencyKey::new("char-op").expect("key");
    let mut mutations = Samples(Vec::with_capacity(MUTATION_ROUNDS));
    for index in 0..MUTATION_ROUNDS {
        let key = IdempotencyKey::new(&format!("char-{index}")).expect("key");
        mutations.0.push(round_trip(
            SyncMutationSubmitRequest {
                session_id: 1,
                document: format!("d-{index}"),
                change: SyncChange::Put(vec![SyncField {
                    name: "qty".into(),
                    value: TbepValue::Integer(i64::try_from(index).expect("fits")),
                }]),
            }
            .into(),
            Some(&key),
        ));
    }
    mutations.0.sort();
    let _ = key;

    println!("method: same-host UDS, one connection, synchronous request/answer, std UnixStream");
    println!(
        "environment: {}, {} cpus",
        std::env::consts::OS,
        std::thread::available_parallelism().map_or(0, std::num::NonZero::get)
    );
    println!(
        "rows: control={CONTROL_ROUNDS} mutation={MUTATION_ROUNDS} (after 200 discarded warm-up rounds)"
    );
    println!("session_setup_first_round_trip_us: {}", session.as_micros());
    println!("control_mean_us: {}", control.mean().as_micros());
    println!("control_p50_us: {}", control.percentile(1, 2).as_micros());
    println!("control_p99_us: {}", control.percentile(99, 100).as_micros());
    println!("control_max_us: {}", control.0.last().map_or_default(std::time::Duration::as_micros));
    println!("mutation_mean_us: {}", mutations.mean().as_micros());
    println!("mutation_p50_us: {}", mutations.percentile(1, 2).as_micros());
    println!("mutation_p99_us: {}", mutations.percentile(99, 100).as_micros());
    println!(
        "mutation_max_us: {}",
        mutations.0.last().map_or_default(std::time::Duration::as_micros)
    );

    let _ = stream.shutdown(std::net::Shutdown::Both);
    server.shutdown();
}
