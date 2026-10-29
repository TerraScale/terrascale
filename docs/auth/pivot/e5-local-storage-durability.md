# Auth storage durability after E5

TerraBase’s leaderless replication and remote acknowledgement are removed by pivot E5. Auth no longer sends `TB-Durability: remote_durable` or applies collection-specific exceptions. Its SDK writes use TerraBase’s local durable commit, with the same conditional writes, transactional deletion fences, authentication, request isolation and transport failure handling.

A successful Auth write currently confirms local persistence only. It does not promise survival of losing the serving node or region. R1–R10 own replacement replication and acknowledgement guarantees. The pre-pivot durability notes describe the former remote-acknowledgement policy and its historical latency incidents.

The Auth Fast and Slow store, identity and concurrency integrations exercise the real TerraBase process with this policy. Dedicated header-recorder tests for the deleted remote-acknowledgement handler are removed with the handler.
