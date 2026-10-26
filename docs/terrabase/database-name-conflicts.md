# Database name conflicts

Regions accept writes without consensus, so two regions can accept the same database name in one project at the same
moment (or on both sides of a partition). A name conflict never deletes a database: both databases keep their ids,
data, backups, keys and files. Only one keeps the name.

## Rule

- **Winner:** the database with the smaller ULID keeps the name, in every region.
- **Loser:** it keeps everything except the name, and moves to the first free name of a list every region derives
  identically: `<name>-conflict-<tail>`, where `<tail>` is the last 6, 8, 10, 13, 16, 20, then 26 characters of
  its own lowercase ULID. `<name>` is cut so the result fits `^[a-z][a-z0-9_-]{0,62}$`. A name is free when no live
  database carries it under its index entry.
- The loser's record keeps `previousName` (the name it lost) and `conflictOf` (the id of the database that kept it).
  Both appear on the management API's database body. A database that loses a conflict name again keeps both values
  and only moves on to the next name in the list.
- The organization's audit log gets one `database.name_conflict` event (actor `system`, target: the loser's id).
  The customer renames the loser to choose another name.

## Requests during and after convergence

- Requests by id always reach the same database.
- A name resolves only to a database whose record carries that name. During convergence, the lost name serves the
  winner only.
- An index entry whose database now carries another name is free for a new database. Convergence drops the index
  entries for a database's previous name and conflict names once its record carries another name.
- Deleting a database releases its name only when the index entry is still its own.
- Retrying a `create_database` commit with the loser's id and its original name returns the loser.

## Who moves the loser

`CatalogConvergence` (every 2 seconds) on the loser's home region writes the move, the index entry and the audit
event in one catalog transaction. Its other holders keep it as it is until the move replicates. If the home region is
away, the loser keeps the lost name in its record, but by name every region resolves the winner until the home returns.

## Rolling deploy

The record fields are written only when set, and older nodes ignore unknown fields. A node older than this change still
deletes a losing database it holds, so the guarantee holds once every node runs this change.

## Organization and project names

Organization names (per creator) and project names (per organization) are not converged: when two regions accept one
at once, both records stay live under their ids and nothing deletes either. The duplicate names stay visible until
the customer renames one.

## Production proof

`scripts/terrabase/name-conflict-proof.sh` runs on a ts-orch host (`ssh ts-sjc-1 'bash -s' < scripts/terrabase/name-conflict-proof.sh`).
It creates a fresh user's Starter organization and project through the internal listeners, then creates one name in gru
and fra at the same moment until both regions accept it. In every region it then expects both databases, the conflict
name with `previousName` and `conflictOf`, and one `database.name_conflict` audit event. Afterwards it deletes the
databases, the project and the organization. Hobby allows one database, so a same-name race needs a plan with two or more.

2026-10-26 12:11Z, batch 13 (`67537ac39`), from ts-sjc-1: `twin3` was accepted by both gru and fra. Every region kept
`01M5W1YJQK9MDHFRH7NCPDZ41Y` (home fra) as `twin3` and `01M5W1YJSA06XFVRBSE1501B3K` (home gru) as
`twin3-conflict-501b3k`, with one audit event. Result: PASS.
