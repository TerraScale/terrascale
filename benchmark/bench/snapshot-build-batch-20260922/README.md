# Artifact history and correction

This directory contains the **second** run, started at 2026-09-22 02:11:34 UTC.
Its receipt digest is `c786ed04c4ac204522ddf119d92d15a4bd1b7b452e3336aab4e223481a729c8e`.
The raw measurements and generated report/receipt are preserved unchanged.

The second invocation reused the first output directory and overwrote its raw
results and receipt. Only `first-run-console.log` remains from the first run;
its printed receipt digest cannot reconstruct the missing receipt. The first
run's median batch time was 278.248 ms and its median atomic time was 7,972.253 ms.
Do not treat the first run as a fully preserved benchmark.

The directory now named `../snapshot-build-batch-20260922-second-copy/` was
originally mislabeled `snapshot-build-batch-20260922-first`. It was copied after
the second run and contains the same second-run results, not another trial.

Both console logs and the generated Markdown contain a stale explanation that
both lanes synchronize every append. The batched lane actually flushes ordered
transaction history, write-stamp and data phases before its commit record. Only
atomic materialization uses the per-append path. This is an editorial correction;
no timings or raw measurements have been changed. The second invocation used
`--no-build` and therefore did not include the already corrected report wording.

These runs overlapped another actor's 10-million-document benchmark on the same
host. They do not establish a general performance advantage or power-loss
qualification. Use a fresh, uniquely named output directory for future runs.
