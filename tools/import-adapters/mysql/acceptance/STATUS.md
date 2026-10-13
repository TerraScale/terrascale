# Acceptance evidence and unmet prerequisites

Recorded **2026-10-12**, local MySQL adapter worktree, baseline `41a15eb3`.
Only authored synthetic files and accessible repository source were used.

| Phase | Result / evidence |
| --- | --- |
| Baseline adapter | Preserved 127/127 parallel synthetic CLI checks and 2-table/3-row `fixtures/expected.sql` evidence at `41a15eb3`; not relabeled as real-export evidence |
| Local tool preflight | Ran: PATH has no mysqldump, mariadb-dump, mysql, mariadb, mysqld, mariadbd, docker, terrabase or sqlite3; Python/curl/dotnet and Podman **5.8.8** are available |
| Cached image/runtime preflight | Ran low priority: Podman has no MySQL/MariaDB/TerraBase image and no running container; unrelated images were not repurposed |
| Ready core/build preflight | Ran read-only: no built TerraBase or TerraBase.Fast bin directories in this or the primary checkout. Active dotnet/csc/browser work; compilers observed at roughly 366% and 198% CPU, load 11.12/7.68/6.87 on 12 CPUs. Later load rose to 17.82/11.02/8.33. Unknown transient listeners were not probed/reused |
| Contract/source check | Read accessible commit `567bfd33fe1257a922c4f8df4e8217517fc06268`: import routes/models/tests, mapping, canonical checksums and entity identity handling. No core edits or operational histories/messages |
| Acceptance oracle | Ran low priority: `nice -n 19 python3 -I -B tools/import-adapters/mysql/acceptance/verify.py self-check` passed; synthetic COPY conversion and constructed responses only, no real exporter/server |
| Recipe/static checks | Ran low priority: all 7 Bash blocks and embedded Python parse successfully without executing recipe commands. Installed Podman help confirms isolation/resource/cidfile/pull flags and removal of this container's anonymous volumes with `--rm`. Baseline file diff against `41a15eb3` is empty; scoped `git diff --cached --check` passed |
| Native source seed/export | **UNRUN** for both exact version candidates; no container start, pull/install, source connection or SQL execution |
| Converter on native dump / negative exporter profiles | **UNRUN**; only synthetic converter/oracle evidence exists |
| TerraBase preview/import/readback acceptance | **UNRUN**; actual core route tests in the engine do not establish acceptance of this adapter's bytes |
| Root checks / publication | **UNRUN**: no restore/build/release checks, Alpha CI, push, PR, merge or deployment; separate serial admission remains required |

## Exact unmet prerequisites

1. **No owned idle admission window.** Release/replication/auth/dashboard work has
   priority. Rerun the README's read-only contention checks; do not interrupt it.
2. **No installed source tools or cached approved images.** Need an already-local
   GPL community image containing matching server/client/exporter **8.4.0** or
   **11.4.2**, its immutable sha256 image ID/available RepoDigest, verified versions
   and reviewed entrypoint. Image IDs/digests remain unknown. Pull/install is not
   authorized; the recipe uses `--pull=never` and socket-only `--network=none`.
3. **No ready owned core fixture.** Need a prebuilt/presently-running isolated core
   at the pinned engine commit (or verified compatible successor), fixture-issued
   synthetic project identity, private TLS/auth/region curl config and known loopback
   URL. No standalone launcher/identity bootstrap command is established here.
4. **No real compatibility evidence.** Execute the README's bounded recipe only
   after prerequisites exist. Keep real exporter refusal as failure evidence;
   never rewrite/drop records. Record native versions/counts/hashes, dry-run,
   converter bundle, preview, completed validation and typed readback separately.

All commands for unmet phases are explicitly **UNRUN** in [README.md](README.md).
The existing [PR body](../PR.md) is preserved; this acceptance recipe does not
remove its CI/publication blocker or claim universal MySQL/MariaDB support.

## Publication-review follow-up — 2026-10-13

The user admitted one noncritical serial publication slot, after Mongo draft #188
completed normal CI. Other imports/payment/docs holds remain untouched.
Fresh checks at exact `1d5f746d` passed 127/127 synthetic tests, oracle, syntax and
scope checks; no open PR overlapped this adapter directory. Independent review
then found two blockers before any push: the pinned core ignores unnamed ALTER
constraints, and TIME accepted non-ASCII digits.

The adapter-only correction emits unnamed constraints inline in CREATE TABLE,
keeps named ADD CONSTRAINT clauses, and requires ASCII TIME digits. It adds a
separate `fixtures/expected-core.sql` golden, leaving both original fixtures and
the original PR body byte-identical to `41a15eb3`. The native acceptance oracle now
checks primary-key/ALTER shapes the pinned reader recognizes. Core files, APIs,
workflows and all unrun native/core acceptance phases stay unchanged.

Corrected-source verification: **131/131** low-priority parallel synthetic CLI
checks passed, plus the acceptance oracle, seven Bash/embedded Python syntax
checks and scoped whitespace checks. Historical COPY rows are unchanged between
the preserved and current goldens. A second independent review and fresh release
queue preflight are still required before publication; no push or PR yet.
