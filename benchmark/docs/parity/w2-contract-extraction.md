# W2 contract extraction and integration

Status: reviewed GO and integrated/pushed at `8771f13`. Strict qualification
remains blocked by the same 42 unresolved PB-5000 through PB-5041 test gaps.

Date: 2026-08-23

This record covers the W2 contract freeze integration. It joins six approved
V1 extraction fragments, assigns stable IDs, rebuilds the state overlays and
inventory, and records the checks that ran in this worktree. It does not claim
that any TerraBaseV2 behavior exists.

## Identities and inputs

- V1 source: TerraBase commit `fa99b9257776dbe625e6a2388c74c9ce916637ee`.
- V2 integration base: `9ccee3567d5063f4c6205177b3a736cc948e3543`.
- Integration branch: `fleet/w2-contract-integration`.
- Ticket: none, as required by the workspace charter.
- All six source commits changed only their named contract fragment. They were
  cherry-picked without conflicts.
- Approved source commits and resulting cherry-picks:

  | Domain | Approved commit | Cherry-picked commit | Rows |
  | --- | --- | --- | ---: |
  | adapters | `593c619` | `0fa2d6387d27204da20cfd407bfbc0a9532e079a` | 34 |
  | documents | `40a40281ba3c9e5a4fbb776161d2853119eae2b7` | `0951684ac05bd0aebe3edca706da638a0b9cbafe` | 29 |
  | families | `7fe134a` | `bd6c4f5b9a79e9a84360f80d590a2764d51b5094` | 46 |
  | query | `7ad6366` | `3582d8829aec5dd0090f638c6a69244cdd3e42c2` | 63 |
  | replication-storage | `21fe94706ce34628cb32246937a6872d16cb719b` | `909423ffb7e355ef6d29ef00b66e73c8b4c088a7` | 49 |
  | transfer | `3ed5179b2f0aaa0f19f6b722a072e351c2caa4dc` | `476c0135ea435d8add7ca87791aa4c0c689e8c50` | 37 |

The six fragments contain 258 rows. No row was dropped or merged.

## Correction pass after independent review

The first independent contract review returned NO-GO `A-REV-CONTRACT-001` because
the first integration had pathless query and replication scopes, stale V1
paths/symbols, and a secret scanner that assumed a `tests/` directory existed.
The bounded corrections resolved those findings. This branch records the
bounded correction assignment `A-INT-CONTRACT-FIX-001`; the later bounded
validator-hardening correction is recorded below as `A-INT-CONTRACT-FIX-004`.

The correction adds an explicit, reproducible citation to all 258 scopes. The
audit resolves 251 rows against the exact V1 checkout and treats seven rows
(PB-5042 through PB-5048) as charter-backed V2 additions with an explicit
`No claim that V1` non-claim. The final audit counts are 267 source citations,
231 exact test citations, and 42 unresolved replication/storage test gaps.
Citation syntax is:

```text
[v1:src/path/file.cs::Symbol]
[v1:test/path/file.cs::TestName]
[charter:specs/GOAL.md::Section heading]
```

The validator checks safe path containment, file existence, declaration shape,
test path or declaration shape, and the pinned V1 commit. The corrected
representative rows include PB-1014, PB-1031, PB-1032, PB-2003, PB-3007,
PB-3015, PB-3020, PB-3040, PB-3045, PB-6000, and PB-6034. Query and index
rows now point to the exact V1 test files named by their contract prose. The
remaining 42 gaps are structured in `parity/v1-test-gaps.json`, each naming
the replication/storage contract and the focused evidence still required.
The citations are evidence anchors only; they do not imply that V2 behavior
has been built or qualified.

The `A-INT-CONTRACT-FIX-004` hardening pass requires the supplied V1 root to
be a clean Git worktree at the pinned commit; tracked, staged, and untracked
changes are rejected, while linked Git worktrees remain supported. Every root
and cited path is canonicalized before containment is checked. In-root
symlinks are accepted when their real target remains inside the root, and
escaping symlinks, parent paths, and absolute citation tokens are rejected.
Declaration anchors remain deliberately non-semantic: the matcher accepts
types, constructors, methods, properties/fields, and test declarations while
rejecting calls, assignments, member access, comments, literals, and
incidental substrings. PB-3007 and PB-3045 now cite the exact
`DatabaseSmuggler` declaration in addition to their existing evidence. This
correction changed no IDs, overlays, statuses, Proof IDs, or the 42 gaps. The
independent reliability review accepted the corrected W2 tree as GO, and the
integrator pushed it as `8771f13`.

Contract-audit mode is allowed to complete with those explicit obligations
and prints `CONTRACT AUDIT COMPLETE WITH 42 UNRESOLVED TEST GAPS`. Strict mode
is the qualification gate and fails closed. It must not be reported as a
pass while the report contains gaps.

## Canonical ranges and classification counts

IDs are assigned by domain and source-array order. The ranges are stable and
non-overlapping. Fragment owners remain the owner slots declared in the
handoff.

| Domain | Owner | Canonical range | Rows | Classification counts |
| --- | --- | --- | ---: | --- |
| adapters | WS-CONTRACT/adapters | PB-1000 to PB-1033 | 34 | admin 7, legacy 4, optional 3, protocol 20 |
| documents | WS-CONTRACT/documents | PB-2000 to PB-2028 | 29 | admin 1, core 22, legacy 2, protocol 4 |
| families | WS-CONTRACT/families | PB-3000 to PB-3045 | 46 | admin 2, core 6, legacy 6, optional 31, protocol 1 |
| query | WS-CONTRACT/query | PB-4000 to PB-4062 | 63 | admin 3, core 37, legacy 1, optional 18, protocol 4 |
| replication-storage | WS-CONTRACT/replication-storage | PB-5000 to PB-5048 | 49 | core 45, legacy 1, optional 2, protocol 1 |
| transfer | WS-CONTRACT/transfer | PB-6000 to PB-6036 | 37 | admin 4, core 20, legacy 1, optional 11, protocol 1 |
| Total |  | PB-1000 to PB-6036 across six ranges | 258 | core 130, optional 65, legacy 15, admin 17, protocol 31 |

The mapping is positional. The left side below is the ID in the approved
worker fragment, and the right side is the canonical ID in the integrated
fragment. The mapping is an audit record, not a claim that rows with similar
names have identical behavior.

## Old-branch to canonical ID mapping

- adapters: PB-0012 -> PB-1000; PB-0013 -> PB-1001; PB-0014 -> PB-1002; PB-0015 -> PB-1003; PB-0016 -> PB-1004; PB-0017 -> PB-1005; PB-0018 -> PB-1006; PB-0019 -> PB-1007; PB-0020 -> PB-1008; PB-0021 -> PB-1009; PB-0022 -> PB-1010; PB-0023 -> PB-1011; PB-0024 -> PB-1012; PB-0025 -> PB-1013; PB-0026 -> PB-1014; PB-0027 -> PB-1015; PB-0028 -> PB-1016; PB-0029 -> PB-1017; PB-0030 -> PB-1018; PB-0031 -> PB-1019; PB-0032 -> PB-1020; PB-0033 -> PB-1021; PB-0034 -> PB-1022; PB-0035 -> PB-1023; PB-0036 -> PB-1024; PB-0037 -> PB-1025; PB-0038 -> PB-1026; PB-0039 -> PB-1027; PB-0040 -> PB-1028; PB-0041 -> PB-1029; PB-0042 -> PB-1030; PB-0043 -> PB-1031; PB-0044 -> PB-1032; PB-0045 -> PB-1033
- documents: PB-0001 -> PB-2000; PB-0002 -> PB-2001; PB-0007 -> PB-2002; PB-0015 -> PB-2003; PB-0016 -> PB-2004; PB-0017 -> PB-2005; PB-0018 -> PB-2006; PB-0019 -> PB-2007; PB-0020 -> PB-2008; PB-0021 -> PB-2009; PB-0022 -> PB-2010; PB-0023 -> PB-2011; PB-0024 -> PB-2012; PB-0025 -> PB-2013; PB-0026 -> PB-2014; PB-0027 -> PB-2015; PB-0028 -> PB-2016; PB-0029 -> PB-2017; PB-0030 -> PB-2018; PB-0031 -> PB-2019; PB-0032 -> PB-2020; PB-0033 -> PB-2021; PB-0034 -> PB-2022; PB-0035 -> PB-2023; PB-0036 -> PB-2024; PB-0037 -> PB-2025; PB-0038 -> PB-2026; PB-0039 -> PB-2027; PB-0040 -> PB-2028
- families: PB-0005 -> PB-3000; PB-0006 -> PB-3001; PB-0015 -> PB-3002; PB-0016 -> PB-3003; PB-0017 -> PB-3004; PB-0018 -> PB-3005; PB-0019 -> PB-3006; PB-0020 -> PB-3007; PB-0021 -> PB-3008; PB-0022 -> PB-3009; PB-0023 -> PB-3010; PB-0024 -> PB-3011; PB-0025 -> PB-3012; PB-0026 -> PB-3013; PB-0027 -> PB-3014; PB-0028 -> PB-3015; PB-0029 -> PB-3016; PB-0030 -> PB-3017; PB-0031 -> PB-3018; PB-0032 -> PB-3019; PB-0033 -> PB-3020; PB-0034 -> PB-3021; PB-0035 -> PB-3022; PB-0036 -> PB-3023; PB-0037 -> PB-3024; PB-0038 -> PB-3025; PB-0039 -> PB-3026; PB-0040 -> PB-3027; PB-0041 -> PB-3028; PB-0042 -> PB-3029; PB-0043 -> PB-3030; PB-0044 -> PB-3031; PB-0045 -> PB-3032; PB-0046 -> PB-3033; PB-0047 -> PB-3034; PB-0048 -> PB-3035; PB-0049 -> PB-3036; PB-0050 -> PB-3037; PB-0051 -> PB-3038; PB-0052 -> PB-3039; PB-0053 -> PB-3040; PB-0054 -> PB-3041; PB-0055 -> PB-3042; PB-0056 -> PB-3043; PB-0057 -> PB-3044; PB-0058 -> PB-3045
- query: PB-0003 -> PB-4000; PB-0004 -> PB-4001; PB-0300 -> PB-4002; PB-0301 -> PB-4003; PB-0302 -> PB-4004; PB-0303 -> PB-4005; PB-0304 -> PB-4006; PB-0305 -> PB-4007; PB-0306 -> PB-4008; PB-0307 -> PB-4009; PB-0308 -> PB-4010; PB-0309 -> PB-4011; PB-0310 -> PB-4012; PB-0311 -> PB-4013; PB-0312 -> PB-4014; PB-0313 -> PB-4015; PB-0314 -> PB-4016; PB-0315 -> PB-4017; PB-0316 -> PB-4018; PB-0317 -> PB-4019; PB-0318 -> PB-4020; PB-0319 -> PB-4021; PB-0320 -> PB-4022; PB-0321 -> PB-4023; PB-0322 -> PB-4024; PB-0323 -> PB-4025; PB-0324 -> PB-4026; PB-0325 -> PB-4027; PB-0326 -> PB-4028; PB-0327 -> PB-4029; PB-0328 -> PB-4030; PB-0329 -> PB-4031; PB-0330 -> PB-4032; PB-0331 -> PB-4033; PB-0332 -> PB-4034; PB-0333 -> PB-4035; PB-0334 -> PB-4036; PB-0335 -> PB-4037; PB-0336 -> PB-4038; PB-0337 -> PB-4039; PB-0338 -> PB-4040; PB-0339 -> PB-4041; PB-0340 -> PB-4042; PB-0341 -> PB-4043; PB-0342 -> PB-4044; PB-0343 -> PB-4045; PB-0344 -> PB-4046; PB-0345 -> PB-4047; PB-0346 -> PB-4048; PB-0347 -> PB-4049; PB-0348 -> PB-4050; PB-0349 -> PB-4051; PB-0350 -> PB-4052; PB-0351 -> PB-4053; PB-0352 -> PB-4054; PB-0353 -> PB-4055; PB-0354 -> PB-4056; PB-0355 -> PB-4057; PB-0356 -> PB-4058; PB-0357 -> PB-4059; PB-0358 -> PB-4060; PB-0359 -> PB-4061; PB-0360 -> PB-4062
- replication-storage: PB-0010 -> PB-5000; PB-0011 -> PB-5001; PB-0100 -> PB-5002; PB-0101 -> PB-5003; PB-0102 -> PB-5004; PB-0103 -> PB-5005; PB-0104 -> PB-5006; PB-0105 -> PB-5007; PB-0106 -> PB-5008; PB-0107 -> PB-5009; PB-0108 -> PB-5010; PB-0109 -> PB-5011; PB-0110 -> PB-5012; PB-0111 -> PB-5013; PB-0112 -> PB-5014; PB-0113 -> PB-5015; PB-0114 -> PB-5016; PB-0115 -> PB-5017; PB-0116 -> PB-5018; PB-0117 -> PB-5019; PB-0118 -> PB-5020; PB-0119 -> PB-5021; PB-0120 -> PB-5022; PB-0121 -> PB-5023; PB-0122 -> PB-5024; PB-0123 -> PB-5025; PB-0124 -> PB-5026; PB-0125 -> PB-5027; PB-0126 -> PB-5028; PB-0127 -> PB-5029; PB-0128 -> PB-5030; PB-0129 -> PB-5031; PB-0130 -> PB-5032; PB-0131 -> PB-5033; PB-0132 -> PB-5034; PB-0133 -> PB-5035; PB-0134 -> PB-5036; PB-0135 -> PB-5037; PB-0136 -> PB-5038; PB-0137 -> PB-5039; PB-0138 -> PB-5040; PB-0139 -> PB-5041; PB-0140 -> PB-5042; PB-0141 -> PB-5043; PB-0142 -> PB-5044; PB-0143 -> PB-5045; PB-0144 -> PB-5046; PB-0145 -> PB-5047; PB-0146 -> PB-5048
- transfer: PB-0008 -> PB-6000; PB-0009 -> PB-6001; PB-0015 -> PB-6002; PB-0016 -> PB-6003; PB-0017 -> PB-6004; PB-0018 -> PB-6005; PB-0019 -> PB-6006; PB-0020 -> PB-6007; PB-0021 -> PB-6008; PB-0022 -> PB-6009; PB-0023 -> PB-6010; PB-0024 -> PB-6011; PB-0025 -> PB-6012; PB-0026 -> PB-6013; PB-0027 -> PB-6014; PB-0028 -> PB-6015; PB-0029 -> PB-6016; PB-0030 -> PB-6017; PB-0031 -> PB-6018; PB-0032 -> PB-6019; PB-0033 -> PB-6020; PB-0034 -> PB-6021; PB-0035 -> PB-6022; PB-0036 -> PB-6023; PB-0037 -> PB-6024; PB-0038 -> PB-6025; PB-0039 -> PB-6026; PB-0040 -> PB-6027; PB-0041 -> PB-6028; PB-0042 -> PB-6029; PB-0043 -> PB-6030; PB-0044 -> PB-6031; PB-0045 -> PB-6032; PB-0046 -> PB-6033; PB-0047 -> PB-6034; PB-0048 -> PB-6035; PB-0049 -> PB-6036

## Manifest, overlays, and generated inventory

The manifest keeps the pinned source commit and lists only accepted evidence
IDs referenced by the merged rows: E-006, E-010, E-011, E-012, E-013, E-014,
E-015, E-027, and E-029.

There is exactly one overlay for each of the 258 canonical rows. Every overlay
has status `unresolved` and an empty `proof_ids` list. No row has an
implementation, qualification, or accepted-exclusion claim. The generated
inventory contains 258 sorted rows and resolves every overlay reference.

## Cross-fragment overlap review

A name comparison found no exact duplicate item names. Several rows are close
enough to deserve reviewer attention, but their contract boundaries are not
identical on the available evidence. I retained all of them.

| Candidate rows | Why they may look duplicated | Decision |
| --- | --- | --- |
| PB-2023 and PB-6007 | document session streaming and document transfer streaming | retain; session/projection behavior and transfer behavior have different callers |
| PB-4035 and PB-6008 | query streaming and query transfer streaming | retain; query execution semantics and transfer wire behavior need separate proof |
| PB-3017 and PB-6009 | time-series range streaming and time-series transfer streaming | retain; family semantics and transfer semantics are separate |
| PB-5013, PB-5014, and PB-6010 | storage snapshot/incremental chain and backup operation | retain; storage format facts and backup controls require separate receipts |
| PB-1020 and PB-6002 | TCP changes stream and changes API | retain; adapter transport and changes contract are separate |
| PB-1021, PB-6003 to PB-6006 | TCP subscription stream and subscription protocol behavior | retain; transport and subscription delivery semantics are different |
| PB-3004, PB-3011, PB-3021, PB-3028 and PB-5028 to PB-5030 | family-specific replication and system replication | retain; family payload rules must not be collapsed into cluster behavior |
| PB-2002 and PB-5022 to PB-5023 | document tombstones/conflicts and replication conflict/tombstone handling | retain; local document state and replicated conflict handling are distinct |

No alias was created because no pair met the identical-semantics and identical-
evidence test. The canonical mapping above preserves every source row for a
later disposition decision.

## Coverage claims and non-claims

This integration proves that the pinned V1 extraction set is represented by
258 schema-valid contract rows, with one owner/seam record and one mutable
overlay per row. It also proves deterministic inventory generation for this
tree.

It does not prove V2 implementation, V1/V2 behavioral parity, multi-region
convergence, recovery safety, benchmark validity, or a speed improvement.
Proof IDs remain empty by design. Optional, legacy, admin, and protocol rows
remain visible and unresolved.

## Downstream seam handoff

The rows hand off to the owners and seams recorded in each fragment. Documents
feed the engine and transaction builders. Query feeds query and index builders.
Families feed family implementations. Replication-storage feeds storage,
replication, sharding, backup, and recovery builders. Transfer feeds streaming,
subscription, backup/import, ETL, queue-sink, and Platform Adapter builders.
Adapters feeds the typed protocol and Platform Adapter builders. No row grants a
generic HTTP escape hatch or a private control-plane operation.

Every behavior owner must return focused tests and exact Proof IDs before an
overlay can move past `unresolved`. Recovery and replication claims require
their receipts. Benchmark claims require a valid TerraBase.Benchmark receipt.

## Known ambiguities and missing decisions

The extraction records contract observations. It does not decide the
replication algorithm, consistency and acknowledgement defaults, conflict
policy, optional or legacy disposition, protocol versioning, production
topology limits, final distribution license, or benchmark workload set.
Those decisions remain open for the parent and the later evidence lanes.

The V1 rows use source evidence IDs from the accepted E-001 through E-029
discovery set. This W2 integration does not add new discovery evidence and does
not treat a discovery note as implementation proof.

## Validation commands and results

The following checks and dispatch commands are recorded for this worktree:

- JSON syntax: `find parity -type f -name '*.json' -print0 | xargs -0 -n1
  jq empty`, passed for 267 JSON files.
- Parity generation: `uv run --no-project --python 3.14.7 python3
  scripts/validate_parity.py --generate`, passed with 258 rows.
- Parity check: `uv run --no-project --python 3.14.7 python3
  scripts/validate_parity.py --check`, passed with 258 rows.
- Determinism: two inventory hashes matched at
  `70f9e65755c9169f3fafa93775a5dd0b9d8f2f2d5c5c4fafd7bf63271d18f004`.
- Tooling tests: `uv run --no-project --python 3.14.7 python3
  scripts/test_parity_tooling.py`, 9 tests passed.
- Citation black-box tests: `python3 scripts/test_v1_citations.py`, 18 tests
  passed, including clean/dirty/staged/untracked Git worktrees, linked
  worktrees, wrong/non-Git roots, realpath containment, in-root and escaping
  symlinks, parent/absolute paths, declaration versus expression-context
  checks, contract versus strict mode, missing-root fail-closed behavior, and
  the machine-readable gap report.
- Contract citation audit dispatch command, requiring the parent to provide a
  clean pinned V1 checkout through `TERRABASE_V1_ROOT` and the TerraScale
  workspace through `TERRASCALE_WORKSPACE_ROOT`:

  ```sh
  : "${TERRABASE_V1_ROOT:?dispatch must provide the clean pinned V1 root}" &&
  : "${TERRASCALE_WORKSPACE_ROOT:?dispatch must provide the TerraScale workspace root}" &&
  workspace_charter="${TERRASCALE_WORKSPACE_ROOT}/specs/GOAL.md" &&
  test -f "$workspace_charter" &&
  v2_root="$(git rev-parse --show-toplevel)" &&
  python3 scripts/validate_v1_citations.py \
    --mode contract \
    --v1-root "$TERRABASE_V1_ROOT" \
    --workspace-root "$TERRASCALE_WORKSPACE_ROOT" \
    --gap-report "$v2_root/parity/v1-test-gaps.json"
  ```

  The charter path is `${TERRASCALE_WORKSPACE_ROOT}/specs/GOAL.md`;
  the explicit file check fails closed before validation. With both dispatch
  roots supplied, the contract audit must report 258 rows, 251 V1-backed rows,
  267 source citations, 231 test citations, and 42 unresolved test gaps. The
  report is an audit result, not qualification.
- Strict audit: the dispatch command above with `--strict` is the qualification
  gate. It must fail while 42 unresolved test gaps remain, and `./scripts/check.sh`
  must fail closed until those obligations are closed. This worktree does not
  claim a strict audit run without the dispatch-provided pinned V1 checkout.
- Secret-scan black-box tests: `uv run --no-project --python 3.14.7 python3
  scripts/test_secret_scan.py`, 4 tests passed. `./scripts/check_secrets.sh`
  passed and honestly reported that this repository has no `tests/` root; the
  scanner now accepts `--root`, scans only existing declared roots, and
  distinguishes no-match, match, and scanner-error exit codes.
- Direct Draft 2020-12 validation through the optional `jsonschema` package:
  unrun because the package is unavailable.
- Full repository check: `./scripts/check.sh` is not a qualification pass while
  strict V1 gaps remain. Rust format, check, release tests, clippy,
  documentation, parity, secret, and license checks were run separately where
  available; the strict citation gate must stop qualification on the 42 gaps.
  The optional `cargo-deny` advisory check was unavailable and recorded as
  unrun.
- No push, merge, issue, pull request, sibling-repository edit, or database
  implementation was performed from this W2 extraction worktree. The later
  integrator pushed the corrected tree as `8771f13`.

The next gate is W3 core design and behavior work. W2 is independently reviewed
GO and remains a contract freeze only. It does not claim V2 implementation,
parity qualification, recovery safety, multi-region convergence, or benchmark
validity. Strict qualification correctly remains closed until the same 42
PB-5000 through PB-5041 test gaps are resolved with the required evidence.
