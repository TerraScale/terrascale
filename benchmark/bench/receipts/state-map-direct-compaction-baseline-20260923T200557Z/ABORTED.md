# Aborted direct compaction probe

This probe was intentionally stopped after 11.846 seconds at the coordinator's
request so the strict 1M current-source runner could proceed. It completed no
records and produced no compaction phase. The receipt is retained as evidence
and must not be used for performance or correctness claims.
