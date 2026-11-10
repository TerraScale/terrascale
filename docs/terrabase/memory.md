# Memory admission

TerraBase admits request and result buffers before allocation through the process and organization memory budgets. Keep each reservation until serialization, cancellation or cleanup releases the memory. Shape work must charge its owning tenant even when shared or running in background.

Native page caches, managed buffers and operating-system caches have different lifetimes. Managed reservation estimates do not establish a hard cap on every native allocation. Queued storage work retains only the tenant and resources it needs, and cancellation must drain owned readers before releasing admission.

Resource limits have one setting in code. Callers receive the existing bounded admission errors rather than bypassing validation or allocating outside the budget. See [shared-process tenancy](shared-process-tenancy.md), [quality of service](qos.md) and [shape metrics](shape-metrics.md).

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
