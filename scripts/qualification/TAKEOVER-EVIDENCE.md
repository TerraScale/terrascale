# Qualification takeover evidence — 2026-09-09

Ownership was transferred by `/root` after both prior writers completed. No
live .NET process remained; `/root` explicitly confirmed this. No other source
directories, external benchmark processes, commits, or provider configuration
were changed. The coordinator and .NET source are ready for independent Astra
review, not self-approved.

## Cause and prevention

The inspected live Program accepted either 409 or 412, discarded duplicate PUT
receipts, checked only replay body value 3, and considered an identity anywhere in
CurrentSnapshot successful. Its wall-clock while condition did not bound the
ReadNextAsync await. Cleanup stopped on the first non-404 error. Thus status-only,
body-only, and identity-only assertions could claim success without proving the
required invariant; an uncooperative read could hang forever.

The live coordinator resolved the workspace root then used `$root/../ts-*`,
selecting the wrong sibling directory. Explicit cleanup used sequential commands
under set-e and the exit trap excluded cleanup. Its fallback cleanup used
`|| true`, suppressing every failure. Raw child output was written before any
redaction.

The .NET fix shares discriminating checks with its regression test: exact
412/precondition_failed; missing 404; unchanged revision and complete expected
body after stale denial; equal authoritative write/read ChangeToken (SDK checks
strong ETag equals revision); exact reactive identity/sdk/runId/value 5 at a
revision later than attach; bounded awaited read and separate cancellation probe;
bounded disposal and per-ID cleanup; attempt-all aggregate cleanup failure without
raw exception messages. A synchronously completed empty read also checks deadline
cancellation explicitly, since WaitAsync on an already completed task can return
without observing cancellation.

The shell entrypoint now delegates the small coordinator to Python stdlib. It
uses the actual workspace sibling paths, one phase loop, attempt-all cleanup,
original failure status preservation, separate cleanup status records, exclusive
log creation, 64 KiB child output caps, 600-second child/process-group deadlines,
and streaming redaction of both API keys before any output is written to disk.
No raw temporary child log exists. Safe run IDs prevent log path traversal.

Recurring-error prevention: compare the final working files and their hashes to
acceptance cases before claiming a correction. Earlier correction claims did not
match the live source provided for takeover. Tests now fail on the actual weak
conditions rather than merely proving that the consumer compiles. Root can add
this demonstrated pattern to future worker briefs; no claim is made about why
the earlier reports and files diverged.

## Exact verification

Supplied immutable package:
`/tmp/tmp.7excehFN86/TerraScale.DotNet.0.1.0.nupkg`
SHA-256 `0231d57cb8a654aff5049151a7c7420f0c047acd4c93a8aa6d1fc0a721635579`.
No per-phase pack or SDK source build occurred. The runner retains package ID,
version 0.1.0, SHA checks, isolated cache, and NuGet source mapping, with an added
copied-artifact SHA check before restore.

Final production consumer invocation used run.sh --phase seed, that package/hash,
api/auth.example.invalid origins and synthetic p/d/k/fixer inputs. Restore and
compilation succeeded. Terminal exit 1 was the expected unreachable-origin
failure, emitted as redacted JSON. This is compiler evidence, not live journey
qualification. Captured terminal output:

```text
Determining projects to restore...
Restored .../scripts/qualification/Qualification.csproj (in 913 ms).
{"status":"error","error":"Name or service not [REDACTED]nown (api.example.invalid:443)"}
```

Commands run from ts-dotnet:

```sh
dotnet restore scripts/qualification/tests/Regression.csproj --source /tmp/tmp.7excehFN86 --source https://api.nuget.org/v3/index.json --packages scripts/qualification/tests/obj/packages
dotnet run --project scripts/qualification/tests/Regression.csproj --no-restore --configuration Release -- --results-directory scripts/qualification/tests/TestResults
python3 -B -m unittest discover -s scripts/qualification/tests -p 'test_*.py' -v
```

TUnit 1.56.18: total 1, succeeded 1, failed 0, skipped 0. The test covers 409 and
wrong-code rejection, receipt/revision mismatch, wrong effects, all three cleanup
attempts, never-completing and immediate-empty reads, ignored cancellation,
successful cancellation, initial snapshot, wrong value, wrong identity and wrong
run rejection. Report: `tests/TestResults/Regression-linux-net10.0-report.html`.
Package preflight unittest: 1 passed; nonexistent path, wrong SHA, wrong package
identity, spaces/leading dash in file name, and unsafe run ID rejected before
restore.

From workspace:

```sh
python3 -B -m unittest discover -s ts-dashboard/scripts/qualification -p 'test_*.py' -v
bash -n ts-dashboard/scripts/qualification/run-local-composition.sh ts-dotnet/scripts/qualification/run.sh
```

Coordinator unittest: 5 passed. Includes real injected child exit 33 followed by
four failed cleanups (all exit 7), preserved original 33, redaction in all six
generated logs, exact root paths, explicit cleanup failure, output chunk splits,
64 KiB cap, timeout while stdout is open and after it closes. Shell syntax passed.
No broad gates, TerraBase builds, or live provider tests were run.

## Final source identities

```text
0edfbefbffacc5554ab5389baa2e816cda819c268af3ea2c75bd967fdfd3d210  ts-dotnet/scripts/qualification/Program.cs
4d2ad262931857ec9c2e11fda9eb8285a5e2c1529da1410327c2bf7aa69529ef  ts-dotnet/scripts/qualification/JourneyChecks.cs
cabcb470e9ea039a275c288ce9440768fc03aed39423a27c571443a97adc1914  ts-dotnet/scripts/qualification/run.sh
1e130a4acb2257f2b8539dc15d37900981e6a2ac840d766fd425454b30ecee3e  ts-dotnet/scripts/qualification/Qualification.csproj
98a410b2a1c7fd6474a03a66d62db511c7de57ad159673f8d5921059c3297c8b  ts-dotnet/scripts/qualification/tests/Program.cs
fd81cc35410a0a5f9c629074f1cf6f3d944dfcceebf4e9e69872a0eff5002215  ts-dotnet/scripts/qualification/tests/Regression.csproj
71f109e7ca036243e3563ab10a7479b56cd857184663e14da0b4507676f156bb  ts-dotnet/scripts/qualification/tests/test_package_inputs.py
da6703cd15e00291e15792b6b0df3bd5472d7c6605149303f700477a1902661a  ts-dashboard/scripts/qualification/run-local-composition.sh
8844762f1f9b4e33b7c59e2a16b20e9e57d864d1328a89a7d895ce5204da27fa  ts-dashboard/scripts/qualification/local_composition.py
f18250f7d57e8a149bbf42e8538896f202d47b97a34db4b8c9178d0ddd015635  ts-dashboard/scripts/qualification/test_local_composition.py
```

## Remaining bounded work

Luna review preparation should freeze these owned directories and include the
SDK document receipt decoder and reactive snapshot field representation as
unchanged dependencies. Obtain separate Astra review of the fix and its runner
interactions. A production journey still requires issued credentials and the real
four SDK composition; local injected tests cannot qualify that boundary. Any
review corrections should be assigned explicitly after this ownership handoff.
