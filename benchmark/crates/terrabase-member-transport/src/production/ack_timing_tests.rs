use super::*;

struct TimingReport {
    roundtrips: Vec<Duration>,
    sends: Vec<Duration>,
    datagrams: u64,
}

async fn measure(ack_delay: Option<Duration>) -> TimingReport {
    let (client_endpoint, server_endpoint) =
        super::tests::endpoint_pair_with_max_frame_and_ack_delay(4096, ack_delay);
    let peer = server_endpoint.config.local_identity.member().clone();
    let mut roundtrips = Vec::with_capacity(64);
    let mut sends = Vec::with_capacity(64);
    let mut datagrams = 0;

    for sequence in 0..64u8 {
        let started = tokio::time::Instant::now();
        let (client, accepted) = tokio::time::timeout(Duration::from_secs(2), async {
            tokio::join!(
                client_endpoint.connect(&peer, ProductionConnectionClass::AuthConsensus),
                server_endpoint.accept(),
            )
        })
        .await
        .expect("bounded authenticated connect");
        let client = client.expect("client authentication");
        let server = accepted.expect("server accept").expect("server authentication");
        let request = vec![sequence; 64];
        let response = vec![sequence.wrapping_add(1); 64];
        let send_started = tokio::time::Instant::now();
        let (sent, received) = tokio::time::timeout(Duration::from_secs(2), async {
            tokio::join!(client.send_frame(&request), server.receive_frame())
        })
        .await
        .expect("bounded request");
        sent.expect("request sent");
        assert_eq!(received.expect("request received"), request);
        sends.push(send_started.elapsed());
        tokio::time::timeout(Duration::from_secs(2), server.send_frame(&response))
            .await
            .expect("bounded response send")
            .expect("response sent");
        let received = tokio::time::timeout(Duration::from_secs(2), client.receive_frame())
            .await
            .expect("bounded response receive")
            .expect("response received");
        assert_eq!(received, response);
        datagrams += client.connection.stats().udp_tx.datagrams;
        datagrams += client.connection.stats().udp_rx.datagrams;
        drop(server);
        tokio::time::timeout(Duration::from_secs(2), client.complete_request())
            .await
            .expect("bounded request completion")
            .expect("request completion");
        roundtrips.push(started.elapsed());
    }

    TimingReport { roundtrips, sends, datagrams }
}

fn percentile(mut values: Vec<Duration>, percentile: usize) -> Duration {
    values.sort_unstable();
    let index = (values.len() * percentile).div_ceil(100).saturating_sub(1);
    values[index]
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
#[ignore = "diagnostic benchmark; run explicitly when investigating QUIC ACK delay"]
async fn compare_default_and_one_millisecond_ack_delay() {
    for (label, delay) in [("default", None), ("one_ms", Some(Duration::from_millis(1)))] {
        let report = measure(delay).await;
        println!(
            "ack_timing label={label} median_roundtrip_us={} p95_roundtrip_us={} median_send_us={} p95_send_us={} datagrams={}",
            percentile(report.roundtrips.clone(), 50).as_micros(),
            percentile(report.roundtrips, 95).as_micros(),
            percentile(report.sends.clone(), 50).as_micros(),
            percentile(report.sends, 95).as_micros(),
            report.datagrams,
        );
    }
}
