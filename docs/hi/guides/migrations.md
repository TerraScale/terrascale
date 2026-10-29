---
title: Schema migrations
---

<!-- i18n:fallback:hi -->
> **अनुवाद लंबित।** इस पृष्ठ का मानव-समीक्षित हिंदी अनुवाद अभी उपलब्ध नहीं है, इसलिए मूल अंग्रेज़ी सामग्री दिखाई जा रही है।

<div lang="en" dir="ltr">

# Schema migrations

`ts migrate` keeps the SQL tables of a database in step with migration files in your repository. Each file creates or drops tables and indexes, or changes rows. The CLI applies each file once, in order, and records it in the database. Requires ts CLI v0.0.4 or later.

## Commands

```text
ts migrate new <name>                    # add the next migrations/NNNN_<name>.sql
ts migrate up <db>                       # apply pending migrations in order
ts migrate status <db>                   # list the migrations and their states
ts migrate diff <db> [<base>]            # print the schema changes against base
```

- `migrate new` adds the next numbered file.
- `migrate up` applies each pending file in one transaction, and records it in the `ts_migrations` table.
- `migrate status` lists the files and their states instead of applying them.
- `migrate diff` prints the tables and indexes that a database adds, drops or changes against `base`. By default, `base` is the database it branched from.

## Migration files

Migration files live in `./migrations`, relative to the working directory. Name them `NNNN_<name>.sql`: four digits, an underscore, then lowercase letters, digits or `_`, and the `.sql` extension. The CLI applies them in numeric order.

```sh
ts migrate new "add orders"
```

This writes `migrations/0001_add_orders.sql`, which starts with a `-- add orders` comment line. Add your statements below it:

```sql
CREATE TABLE orders (
  id TEXT PRIMARY KEY,
  customer TEXT NOT NULL,
  total_cents INTEGER NOT NULL
);

CREATE INDEX orders_customer ON orders (customer);
```

A file can hold several statements separated by `;`. A `;` inside quotes or comments does not end a statement. A file can have up to 255 statements, and each statement can be up to 16 KiB. Do not write `BEGIN` or `COMMIT`. Each file runs as one transaction together with its history row, so a file that fails applies nothing.

## Apply migrations

```sh
ts migrate up app
```

`up` prints `Applied <file>` for each file it applies, or `app is up to date` when nothing is pending. Running it again does nothing.

The `ts_migrations` table has one row per applied file, with the columns `name` (the primary key), `checksum` and `applied_at`. The checksum is the SHA-256 of the file. If a file's content changes after it was applied, `up` refuses to run it. Add a new migration instead of editing an applied file.

`status` shows a `MIGRATION` and `STATE` table. Each state means:

- `pending`: the file has not been applied yet.
- `applied`: the file was applied and has not changed.
- `changed`: the file was applied, but its content differs from the recorded checksum.
- `applied, file missing`: the history has the migration, but the file is gone.

## Compare schemas

`diff` compares tables with their columns and declared types, and indexes. It prints `--- <base>` and `+++ <db>`, then one line for each difference:

```text
--- app
+++ app-orders
+ index orders_customer ON orders (customer)
+ table orders (id TEXT, customer TEXT, total_cents INTEGER)
```

- `+` marks something the database adds.
- `-` marks something the database drops.
- A changed table shows `-` with its old columns, then `+` with the new ones.

If nothing differs, it prints `<db> has the schema of <base>`. `diff` does not compare `NOT NULL`, defaults or primary keys.

## Read tables

`ts exec <db> <statement>` runs one SQL statement in its own transaction and prints each row as a JSON line, for example `ts exec app 'SELECT customer, total_cents FROM orders LIMIT 10'`.

## Example workflow

Branch the database, test the migration on the branch, then apply it to the source:

```sh
ts db branch app app-orders
ts migrate new create_orders
# edit migrations/0001_create_orders.sql
ts migrate up app-orders
ts migrate diff app-orders
ts migrate up app
ts migrate status app
```

The branch is an independent copy, so a bad migration does not reach `app`. Branch-per-pull-request workflows are covered in [Branches](/hi/guides/branches), which is being updated for SQL.

## CI

In CI, set `TERRASCALE_API_KEY` to an API key instead of running `ts auth login`. Database commands such as `db`, `exec` and `migrate` then act as that key in its project. An organization key also needs `-project <id>`, for example `ts -project <id> migrate up app`. `auth`, `whoami`, `projects` and `keys` still need `ts auth login`.

</div>
