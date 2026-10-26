# RT6 endpoint matrix operator evidence

`batch15/` and `batch16/` each hold one successful run's JSONL and its log. The JSONL files contain synthetic tenant IDs, scoped-key IDs, serving revisions, observations and cleanup statuses. They contain no API-key secrets, peer credentials or email codes.

Batch 15 needed five attempts; only the JSONL of `run5` is kept. `run1` failed on the key-response parser. An explicit operator helper revoked the orphaned scoped key, returned 204 deleting its project, and the original teardown then observed project 404 and deleted the organization with 204.

`run2` failed because the harness tried a database name on a legacy management URL which accepts only a ULID. Its tenant was removed. The corrected harness probes name and ULID selectors through the SQL data route.

`run3` established name/ULID SQL routes in all regions, then could not connect from ts-sjc-1 to the public database ports. Its tenant was removed. `delta-direct-preflight.json` records successful public connectivity from delta. `relay-tls-preflight.txt` records the six verified TLS handshakes through the subsequent relay.

`run4` established native selectors and regional origins and completed the first CRUD repeat, then a sjc public Postgres connection reset before STARTTLS at 14:46:25Z. The same timestamp appears in the ts-sjc-1 journal's ts-orch control-socket restart warning. This is a coincident ingress interruption, not a diagnosed database defect. Its tenant was removed.

`run5` uses the same test workload and allows transient read reconnections throughout the existing 30-second observation window, recording each read error. Writes never retry. Its final JSONL summary and report determine the result; partial earlier attempts do not contribute successful-run counts.

`scripts/terrabase/endpoint-replication-proof.{sh,py}` is the operator harness used by batch 15 `run5` and, unchanged, by batch 16. It requires Python 3, curl with h2c, SSH, the ts-orch container runtime and sudo access already authorized on the serving host. The shell reads only the running container's peer-secret file into a temporary mode-600 header file, generates a fresh acting user ULID, and removes its temporary directory at exit. The native key stays in process memory. The harness deletes its own scoped key, database, project and organization and checks project absence in every region.

The OCI host could not reach public native ports directly. A delta SSH session created six temporary reverse forwards, each bound to 127.0.0.1 with an automatically assigned port: three to the regional public IPs on 5432 and three on 7464. The allocated ports were written to `native-relays.json` beside the remote harness, mapping each region to its `postgres` and `wire` loopback ports. TLS retained the public SNI and system certificate/hostname verification. That SSH session and the relay map are removed after each run. Batch 16 repeated the same relay setup and preflight from a new SSH session. No service image, listener configuration, firewall or data outside the dedicated proof tenant was changed.

Timings include the controller, new curl processes, network traversal, TLS session establishment when needed and the SSH relay. They are not an isolated engine benchmark.
