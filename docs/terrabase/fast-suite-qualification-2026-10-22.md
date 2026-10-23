# Fast suite qualification, 2026-10-22

Dev-host qualification only; no repeat or stress loops were added to GitHub Actions.
The final required `test` check remains the merge gate. Repetitions completed before
the requirement changed from twenty to five are retained rather than rerun.

## Fixes in this change

| Issue | Correction | Evidence |
| --- | --- | --- |
| #225 | Wait for completed readiness passes before advancing fake time; retain the throwing logger, three probes and both HTTP 503 assertions. | Fixed readiness 20/20 on delta; baseline also passed 20/20, so this run did not reproduce the race. |
| #217 | Authenticate benchmark internal clients, reject malformed credentials, prevent redirects/authority replacement, protect temporary shell header files, and supply DrainBench's child credential. | Full Go vet/build/race suite passed; DrainBench Release built with zero warnings/errors; final credential/path/redirect race checks passed three times. |
| #310 | Size the cancel/resume test deadline from completed contention measurements; preserve all 3,000 rows, partial-progress and final validation assertions. | Final qualification and deadline-lifetime review are recorded below. |

Auth qualification used `3c88887f0947149f05cf313d96a4300d647337f1`, pinned sibling
SDK `78e311fe1dcb6dd603190800ed540d08e06de5fd`, .NET
`11.0.100-rc.1.26425.128`, CPU/GC count 8, default auth parallelism 4 and nice 19.
It passed 100 cases: readiness 20, original restart 20, clean same-key restart 20,
regional storage 37, real application credential senders 2, plus one initial test.
There were no failures or skips. The auth source is unchanged by subsequent fixes.

Auth logs: delta `/home/mariogk/fast-flakes-auth-20261022-logs/`.
Fixed driver SHA256:
`626c4e81284cd73174f966b645cfaec35616670eb46ff12909cdb4fa36d9df1c`.

Benchmark qualification used `3c88887f` for `nice -n 19 make benchmark-check`
(401 seconds) and the DrainBench Release build (11 seconds). The actual PITR jq
filter rejected multiline credentials. Final focused race checks used `a3597072`
and `c75e8503`, whose benchmark trees are identical
(`8bdcdfa07ab91621231fa845f5932435ff8c2f58`). No live cloud benchmark was run.
Logs: mariogk-dev `/home/mariogk/fast-flakes-benchmark-20261022-logs/`, under each SHA.
Final repeat log SHA256 values:

```text
34a5a1d2c94d3bae3b80298586db2ae0a392c81ee20996ad1c9c61a70a472640  credential-tests.log
0998df1c5ca630ed91b106f060285be545e5641f73b25763e05f881701d123b6  repeat-2.log
5b169aa95bda6b7bbbb6e6853321efedeedbbeb5cff15f3c1df837ca389900d2  repeat-3.log
```

## Import deadline measurements

Baseline main `e7fa366ddb289f5612c93d316b316fb92c568151` passed unloaded in
26.624 seconds but exceeded the original 120-second deadline in all sixteen
instances sharing four CPUs. Native statement reuse did not improve that control
and is not retained in the final diff.

Five diagnostic imports shared one CPU on delta. Each cancelled at 1,000 rows,
verified the partial checkpoint, resumed and validated all 3,000 rows. Full TUnit
durations were 481.344–484.213 seconds. The longest additional 500-row batch took
55.692 seconds. The test deadline is `ceil(484.213 + 55.692) = 540` seconds: the
slowest observed completion plus one measured batch of headroom. This is a test
budget, not a production throughput improvement or a universal maximum.

An initial diagnostic exposed the fixture's five-minute JWT expiring during
polling. Test authentication stays active with the same project and scopes;
the test credential must cover its deadline. Polling must pass the deadline's
cancellation token to the actual HTTP request and body read.

Raw diagnostic logs on delta:

```text
/home/mariogk/remote-builds/fast-flakes-baseline-20261022-retry-logs/
/home/mariogk/remote-builds/fast-flakes-import-completion-20261022-retry-logs/
/home/mariogk/remote-builds/fast-flakes-import-completion-fresh-token-20261022-logs/
```

The dev-host checkout fetched `aed12977136c606e609d2e004839d28cee8381d9`,
restored both native driver files from baseline `e7fa366d`, then overlaid the
qualified test/harness sources before building. This is source-overlay evidence;
it does not claim the fetched commit already contained the final fix.
SDK `78e311fe` and CPU/GC count 8 match the auth qualification. After a Release
build, run five concurrent instances on the dev host only:

```sh
nice -n 19 taskset -c 0 dotnet run --project tests/TerraBase.Fast --configuration Release --no-build -- --treenode-filter '/*/TerraBase.Fast.Import/ImportHardeningTests/Cancelling_a_running_copy_stops_it_and_resume_continues_to_a_validated_import' --minimum-expected-tests 1 --no-ansi
```

The corrected final-source build passed, followed by five concurrent samples on
one CPU: **5/5 passed, zero failed/skipped**, TUnit durations 96.468–100.928 seconds
(CLI 103.592–107.493 seconds). The final test token covers the full 540-second
budget; HTTP polling and body reads receive the deadline cancellation token.
An intermediate build caught two analyzer errors before any tests; both were
corrected before the final successful build. All seven assertions remain intact.

Final raw logs on delta:
`/home/mariogk/remote-builds/fast-flakes-import-qualified-cancellation-final-540s-20261022-logs/`.
The local combined JSON evidence bundle is
`/tmp/fast-flakes-import-validation-20261022.json`, SHA256
`9b2add5e3ce7095d8d280a4810d08c2ad56b2cb7bc00a2b97872f8679616dd67`.
Final qualified source hashes:

```text
32c0243ca62047a77342e3b3a591aa6697dd577ab589ac606eb53a31c2e7f425  ImportHardeningTests.cs
a7040c1f24655a91fa0331c85daeeef195eeb7e8b782304359b55cdd1b93eecd  ImportClient.cs
9194e83f3ae94b00257d5a207f0fe59ff1dc42851742ead2f9304948470ba465  ImportApiTests.cs
```

Final per-sample log SHA256 values:

```text
3499c1672bfc6119e038c49798f86adcec2c590d85db492bbfb8f20ba3ae8a49  import-completion-01.log
7da4e8962cde01e3912becf9961e005055859138e13dc1acdb1ea13e97f1d908  import-completion-02.log
82f1186ab040d98a009c57ef919b16c5febc848a74d3182704b471dffeb6f0c5  import-completion-03.log
2caeba1a13e9fd3c6cd13d8eb62ca5b9d3671c58d29fc1664a2340cd3b528865  import-completion-04.log
7d158e7d38577c6e8be80936f6d585fab74b71059f0aaf455c1e0f79247ab0d2  import-completion-05.log
```

## Existing main fixes qualified separately

| Issue | Merged correction | Evidence |
| --- | --- | --- |
| #259 | Restart relay retains its front sockets and allocates a fresh backend port 0 on restart (`f653fb83`, #272). | Original restart 20/20; clean same-key restart 20/20 in the auth qualification above. |
| #306 | Frontier timer rearm/commit wake synchronization (`f653fb83`, #272). | Main `e7fa366d`: 20/20 focused repeats on four CPUs, test durations 4.311–10.546 seconds. |
| #177 | Dispose the canceled reader/transaction and recover using a fresh transaction (`f7178322`, `e6116dbc`). | Main `e7fa366d`: 20 repeats of seven QueryValueRegressionTests cases, 140 passed, zero failed/skipped; first five 35/35. |
| #243, #244 | Pin SDK/SDK image (`d875729d`, #248); remove stale provider lock entry (`043d7f91`, #341). | [Main required CI](https://github.com/TerraScale/terrascale/actions/runs/39689535857) passed. Slow binaries execute: auth 25/25; TerraBase 61/62 with a real bucket failure tracked under #250. |

Frontier logs: mariogk-dev
`/home/mariogk/remote-builds/fast-flakes-frontier-20261022-logs/`;
`measurements.json` SHA256
`0672b6774f1f1dfed8801b7eadf36dbb6b99a40607b444c6b73dd016a8b6b733`.

Cancellation fixture logs: mariogk-dev
`/home/mariogk/remote-builds/fast-flakes-cancel-20261022-logs/20261022T133627Z/`;
manifest SHA256
`6cd06b4449fc413266b02dd19b3c8fc6b1c6f701ef76f3d784eab6381eed3c18`.
Its source SHA256 is
`470fd053fe739e98ccfcb9b1edc2d282440946ddbf6a87996ee59a2c390efb1c`.

No workflows, storage format, production authentication, public API or migration
behavior changed. Main image/deploy failures remain owned by the deploy lane.
