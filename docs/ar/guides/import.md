---
title: Import an existing database
description: Pending one-time import from PostgreSQL or Turso into an isolated TerraScale database, with dry-run preview and validation.
head: [['meta', { name: 'robots', content: 'noindex' }]]
---

<!-- i18n:fallback:ar -->
> **الترجمة قيد المراجعة.** لا تتوفر بعد مراجعة بشرية لهذه الصفحة بالعربية، لذلك يظهر المحتوى الأصلي بالإنجليزية.

<div lang="en" dir="ltr">

# Import an existing database

Copy a PostgreSQL or Turso database into TerraScale once. Preview the result first, import into an isolated destination and validate it before your application depends on it.

::: warning Pending feature
Guided import is in development. No import command, dashboard control or API route is published, and this page does not establish availability. Items marked **pending** await the serving release's import contract. Rehearse with synthetic data only. Do not import production data until import is announced for your release.
:::

Prepare your source with its guide:

- [Import from PostgreSQL](/ar/guides/import-postgresql)
- [Import from Turso](/ar/guides/import-turso)

## What an import is

An import is a one-time bulk copy taken from one consistent snapshot of the source. It is not replication. Writes made to the source after the snapshot are not copied, and there is no ongoing change data capture or two-way sync.

The import reads the source and never writes to it. Give it a dedicated read-only credential so the source stays unchanged even if that credential leaks.

## Workflow

1. Rehearse with the synthetic fixture in your source guide.
2. Create a dedicated, expiring read-only credential for the source.
3. Hand it over through the supported credential input only. See [credentials](#hand-over-credentials-securely).
4. Run the dry-run preview. It is the default and writes nothing.
5. Resolve every unsupported type and key warning in the preview.
6. Import into a new, isolated destination database.
7. [Validate](#validate-the-result) counts and checksums against the source.
8. Switch your application only after validation. Keep the source until you accept the result.

## Hand over credentials securely

- Create a credential that only the import uses. Grant it read access only and set an expiry. Revoke it when the import finishes.
- Enter it only in the import's credential input (**pending**: dashboard or CLI prompt). Never paste a password, token or connection string into chat, a support ticket, an issue, a commit, a URL, a shared document or a command-line argument that lands in shell history.
- Require TLS with certificate verification.
- Do not open the source to the whole internet. If it has an IP allowlist, use only the egress addresses published for the import (**pending**).
- Progress, logs and error reports must never contain the credential. If one does, revoke the credential and report the exposure.

Treat preview and error reports as customer data. They can contain table names, keys and sample values.

## Snapshot consistency

Every table must be read at one point in time, so related rows, counts and checksums agree. Each source guide explains the mechanism and what it costs the source.

For the strongest result, freeze writes to the source during the import, or import from a point-in-time copy such as a restored backup or a branch. A frozen or copied source also makes counts comparable and keeps a resumed job on the same data. Plan cutover so writes after the snapshot are either blocked or knowingly left behind.

## Dry-run preview

Every import starts as a dry run. It writes nothing to TerraScale and does not change the source. Review, for each table:

- Its destination name and how each record's identity is derived.
- The mapping for each column, including lossy and unsupported columns.
- Which keys and constraints are kept, reported or dropped.
- Estimated rows and size against [limits](/ar/limits) and [pricing](/ar/pricing/).

**Pending:** the preview's report fields, whether it reads every row or a sample, and how you record an explicit mapping choice for a column.

## Isolated destination

By default an import writes to a new, empty database used only by that import. **Pending:** whether any non-default option writes into an existing database. Point a test copy of your application at the destination first.

Choose placement before you import. Data is copied into the destination's selected placement; see [replication](/ar/concepts/replication).

## Keys, constraints and schema objects

The preview must state how each source object is handled. Plan your application around the confirmed policy, not around the source's behavior.

| Source object | Import policy |
| --- | --- |
| Primary key, including composite keys | **Pending**: derivation of record identity |
| Table without a primary key | **Pending**: rejected, or imported with a generated identity |
| Unique, foreign key and check constraints | **Pending**: enforced, reported or dropped |
| `NOT NULL`, defaults and generated columns | **Pending** |
| Sequences, identity and autoincrement counters | **Pending**: whether the next value is carried over |
| Indexes | **Pending**: indexes the destination creates for you |
| Views, triggers, functions and policies | **Pending** |
| Users, roles, grants and passwords | Not imported. Set up TerraScale access through [authentication](/ar/guides/authentication). |

## Progress, cancellation and resume

An import runs as a job. The import is designed for:

- **Bounded streaming.** The job reads the source in bounded batches. It does not load a whole table into memory or hold the source longer than the snapshot needs.
- **Progress.** Per-table rows read, rows written and errors.
- **Cancellation.** Cancelling stops the job and leaves the source unchanged.
- **Resume without duplicates.** Resuming continues from the last committed checkpoint without duplicating records. This is job-level behavior. The Documents API itself does not deduplicate mutations; see [errors](/ar/guides/errors).

**Pending:** job states, progress fields, error codes, checkpoint granularity and what a cancelled job leaves in the destination. A live PostgreSQL or Turso snapshot does not survive a dropped connection. The contract must say whether a resumed job restarts affected tables or reports that its result spans more than one snapshot. A frozen or copied source avoids the question.

## Validate the result

1. Compare per-table row counts from the import report with source counts taken at the same point. Each source guide has a count query.
2. Compare per-table checksums from the validation report (**pending**: algorithm and canonical form). A checksum is meaningful only when both sides hash the same canonical value; for a converted column, the report must say what it hashed.
3. Spot-check edge values: the largest integers, exact decimals, timestamps and time zones, binary columns, `NULL` and empty strings.
4. Run your application's read-only tests against the destination.

Do not switch your application while any check is unresolved.

## Retry and recovery

| Situation | What to do |
| --- | --- |
| Network failure, `429` or `503` | Resume the same job. Do not start a second job into the same destination. |
| Source credential expired or revoked | Issue a new read-only credential, hand it over securely, then resume (**pending**). |
| Unsupported type or constraint failure | Resolve it in a new dry run, then import into a new isolated destination. |
| Source schema changed during the import | Cancel, then import again from a fresh snapshot. |
| Validation mismatch | Keep the application on the source. Investigate the report, then import again into a new destination. |

A failed or cancelled import leaves the source unchanged. Delete an abandoned destination yourself, after confirming you no longer need it.

## Limitations

- One-time bulk copy only: no change data capture, continuous sync or automated cutover.
- Sources: PostgreSQL and Turso.
- Source users, roles, grants and passwords are not imported.
- Each imported record is subject to the destination's [limits](/ar/limits).
- **Pending:** supported source versions, maximum database size, throughput, and how imported writes and storage are metered. Review [pricing](/ar/pricing/) before a large import.

</div>
