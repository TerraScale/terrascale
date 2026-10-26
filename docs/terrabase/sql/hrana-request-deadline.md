# Hrana request deadline

A Hrana pipeline or v3 cursor shares the SQL request deadline of five seconds across execute, batch, sequence and describe operations. Autocommit statements do not restart that deadline. Sequence statements also share the enclosing request deadline.

After the deadline, remaining SQL operations return `SQLITE_INTERRUPT` without running. Earlier autocommit writes retain their normal commit semantics; a pipeline is not an atomic transaction. Clients should inspect individual results and use an explicit transaction when atomicity is required. An expired script stops before transaction controls, so a late COMMIT cannot publish its earlier writes. Its open transaction remains available in the baton for an explicit rollback or recovery in a new request. Stream close remains available for cleanup. The next HTTP request receives a fresh deadline, including when it resumes the same baton.

The deadline is checked before SQL dispatch; an operation already running may finish after five seconds.

The existing per-statement work limits and transaction replay rules still apply. This changes resource enforcement without changing request or response schemas, SDK methods, persisted data, or storage formats. No configuration switches are added.

`HranaDeadlineTests` exercises the real HTTP listeners and storage engine for pipelines, batches, cursors and scripts. It advances an isolated clock after a real table read, verifies the following write never executes, and resumes the baton in a new request. These regression cases require CI execution; static inspection alone is not runtime proof.
