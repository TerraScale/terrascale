# Stream frontier runtime proof

This standalone request run needs a fresh two-region journey namespace, the built TerraBase service, Node 24,
and nats-server on PATH. It starts three TerraBase processes (two in the home region) and one JetStream server and stops them on completion.
It uses the existing journey object-store stand-in for encrypted replication payloads.

    export DOTNET_ROOT=$HOME/.local/share/mise/dotnet-root
    export DOTNET_PROCESSOR_COUNT=4
    export E2E_REGIONS=2
    bash e2e/env/local.sh prepare
    bash e2e/env/local.sh start
    bash e2e/env/local.sh exec node e2e/replication/frontier.mjs
    bash e2e/env/local.sh stop

Run it once per fresh namespace: the object-store stand-in keeps catalog anchors in memory until the namespace stops.
The regular journey does not configure JetStream and cannot exercise this consumer.

The run waits on applied notices, broker advisories, acknowledgement-progress messages and process log events, with
no polling. It checks retained redelivery after restart; two broker-driven writer changes with a head retrying an
unavailable object-store read; the next write at sequence 1 after deleting and recreating the origin stream; an insert
whose data key read is refused (403) for at least six attempts while the update of the same row waits unapplied and
another database keeps applying, then both apply in order with the updated value; a child insert held behind a refused
parent insert; a held database receiving more than the consumer's 4,096 in-flight messages while another database
still applies and the consumer's pending acknowledgements stay at or under 32, then all of them applying in stream order;
a held database whose read-back call loses its broker connection (a relay in front of the writer's broker connection
closes it on that call), then every message applying in stream order; the writer lease lapsing and being renewed on the
same node (the writer process is stopped for eight seconds, longer than the six second lease) while a read-back head is
held, then every message applying in stream order through a recreated consumer; a writer change with the old writer's
pull outstanding, after which the new writer's fresh consumer delivers 300 messages to it in stream order, the old
writer applies none and nothing is redelivered (the second home-region node decides them against its own copy, which
in this environment lacks databases' later schema, so their outcomes are not asserted); a node whose epoch is older
than an existing consumer neither deleting nor reading it; a same-node replay of 20 applied messages writing none of
the pages that every new apply writes, counted from the database's write-ahead log frames; an unknown database held and
retried instead of parked; a deleted database's message and an empty payload parked with durable records; and the
held-database gauge and oldest age growing and returning to zero.

The run changes the real JetStream regional ownership record, including the matching predecessor/preparation
metadata, and waits for the target node's retry event to establish that it became the writer. This isolates
consumer recovery from the rollout snapshot preparation path. The data-key read returns 503 (or 403 in the later steps) at the
third-party stand-in until the run releases it; the later transaction changes a different row so both effects are
independently asserted. The script uses only the journey object-store hostname and its
journey-shapes bucket alias. The stand-in maps that alias to the custody bucket used by the service.
Parked-record replay, an unknown database that the catalog learns later, and two nodes that both act as writer at
the same moment are not covered.
