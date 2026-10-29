# PITR fresh-target setup smoke, 2026-10-29

Local S3 emulator on delta; exploratory, not qualification or performance evidence. Review-fix working tree based on `51599bc2a` for PR #898. No production credentials or source database files were used. Path B prepares the pinned identity through the test-only catalog fixture; it does not recover a production catalog.

| Check | Result | Scope |
|---|---|---|
| Extended PitrLab fresh-target checks | Pass | Prepare twice, prove source documents absent, HTTP 201 restore, 30/30 texts, restart, 30/30 texts and unchanged versions. Source and target use the configured master for database encryption. |
| Standalone Path B | Pass | Fixture preparation and adoption, HTTP 201 restore of the source snapshot, pre/post-restart verification and partition comparison. Source snapshot has zero workload documents. |
| Corrupted snapshot on a separate fresh target | Expected refusal | Preparation/adoption succeeds; restore returns HTTP 409 `backup_unreadable`. |
| Full PitrLab self-check | Fail | 20/29 checks pass; 9 existing audit/mutation assertions fail because the auditor treats `generations/<id>` markers as unparseable backup keys. No assertions changed. |
| Full benchmark source step | Fail | 6/20 semantic probes fail because legacy fence IDs are not canonical ULIDs. Exact `C0` cut also fails `invalid_document_id`. Full workload matrix was not run. |

All commands ran at nice 19. Builds: `make terrabase-build` with the documented local `TsDotnetRoot` override, `make -C benchmark build`, and (from `benchmark/`) `go build -o /tmp/e2-pitr-fresh-bench ./cmd/pitr-bench`.

Integration command: `TERRABASE_PITR_JOB=/tmp/e2-pitr-fresh-fsnsgcc9/selfcheck-job.json dotnet tests/TerraBase.E2E/bin/Debug/net11.0/TerraBase.E2E.dll --treenode-filter '/*/*/PitrLabHost/*' --minimum-expected-tests 1 --output detailed --no-ansi`; the job names mode `self-check` and an isolated temporary root.

Script commands used `BASE=/tmp/e2-pitr-fresh-fsnsgcc9/pathb-live`, the Debug TerraBase binary, its SHA256, `COMMIT=51599bc2a0c00d9ad65946ccb9763533e607a35f`, `MODE=exploratory DOCS=1000 SPLIT=1000 PARTS=2 WRITERS=1`, the built PitrLab DLL and Go executable. The local apphost additionally needed `DOTNET_ROOT=/home/mariogk/.local/share/mise/dotnet-root`.

After the failed `source` probe, the targeted smoke used `pitr-bench cut --run "$BASE/source" --name C0 --partial load-a`, then script steps `copy pathb3` and `path-b "$BASE/copies/pathb3" smoke3 C0` with `AT_MS=1793294839991`, the copied source snapshot time. `copy pristine` and `fault corrupt_snapshot <source-snapshot-key> C0 corruptsnapshot` used the same time. This bounded, empty-source run exercises target setup and restore/restart; it makes no workload, cut-matrix or losslessness claim. The separate integration check supplies nonempty restore coverage.

Evidence remains locally under `/tmp/e2-pitr-fresh-fsnsgcc9` and `/tmp/e2-pitr-*.log`. Source, targets, samplers and emulator processes were stopped after validation. Early retries exposed fixture-key incompatibility and listener-port collision; both were corrected before the results above.
