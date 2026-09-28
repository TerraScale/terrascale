# HTTPS document point-read fixture

This recipe provisions one private, run-owned local-v1 shard for
`https-document/`. It follows
`crates/tb-server/tests/client_process.rs::packaged_initializer_bootstraps_fresh_local_v1_for_https_document_caller`:
run the packaged `terrabase-init`, commit one durable value, restart the
packaged server over the same data directory, then capture the verified GET
response used as the driver's exact JSON oracle.

The seed uses tenant `11111111-1111-1111-1111-111111111111`, database
`22222222-2222-2222-2222-222222222222`, shard `7`, collection
`61616161-6161-6161-6161-616161616161`, and document
`71717171-7171-7171-7171-717171717171`. It writes explicit null field 1 and
typed `i64` field 3 with value `73`, matching the packaged initializer Fast
fixture. The benchmark key is generated per run by the packaged
`terrabase-keygen`, scoped to that one collection with `read` permission and
`rate_per_second=100`, `burst=200`, and `max_in_flight=1`. A separate,
run-only write key seeds the value and is removed from the registry before the
server restarts for measurement.

Each run generates a fresh P-256 local CA and a server certificate with
`IP:127.0.0.1` SAN. Both server listeners bind IPv4 loopback only. The server
runs in a transient user systemd scope with `MemoryMax=8589934592`; the
process-level memory budget matches the Fast process fixture at 256 MiB with
16 MiB headroom. A rootless user namespace is unnecessary because this server
binds unprivileged loopback ports and writes only into an owner-only run
directory. The actual cgroup values are captured with the run receipt.

After the product Fast suite passes and the coordinator opens the run slot,
run the harness with the exact packaged musl binaries from the approved
TerraBase checkout and the built HTTPS driver. For example:

```sh
python3 bench/fixtures/https-document-point-read/run_fixture.py \
  --integration-repo /home/mariogk/Projects/TerraScale/terrabase \
  --server-binary /home/mariogk/Projects/TerraScale/terrabase/target/x86_64-unknown-linux-musl/debug/tb-server \
  --init-binary /home/mariogk/Projects/TerraScale/terrabase/target/x86_64-unknown-linux-musl/debug/terrabase-init \
  --keygen-binary /home/mariogk/Projects/TerraScale/terrabase/target/x86_64-unknown-linux-musl/debug/terrabase-keygen \
  --driver-binary /home/mariogk/Projects/TerraScale/ts-benchmark/https-document/target/release/terrabase-https-document-point-read \
  --run-id https-doc-20260926-a1
```

The harness does not build binaries. It refuses a reused input or result
directory, snapshots the source tree and binary hashes, verifies TLS and the
durable commit after restart, then runs the driver's 1,000 exact-response
reads with `inter_request_delay_ms=20`. The delay is outside each per-request
latency timer; the receipt records the configured 50 reads/s offer cap. The
driver writes samples, receipt, and a Markdown summary under
`bench/https-document-point-read/<run-id>/`. The harness adds the actual server
configs, source manifest, fixture manifest, seed response, public CA, and a
server log after checking that neither generated token appears in the log.

The generated data, private TLS key, registry, and both bearer-token files
live only under the ignored `runtime/<run-id>/` directory. On success the
harness stops both server scopes, copies non-secret provenance into the
benchmark result directory, then removes the run-owned data and credentials.
On failure it stops the scopes and removes credentials; private run inputs are
retained for diagnosis. No pre-existing data or keys are read.
