---
title: Use SQLAlchemy and Alembic
description: Run SQLAlchemy's SQLite dialect and Alembic migrations over the Python SDK.
---

<!-- i18n:fallback:es -->
> **Traducción pendiente.** Esta página aún no tiene revisión humana en español y muestra el contenido original en inglés.

<div lang="en" dir="ltr">

# Use SQLAlchemy and Alembic

The Python SDK ships a SQLAlchemy dialect that reuses SQLAlchemy's SQLite dialect over TerraScale's SQL API. Engines, Core, the ORM, sessions, savepoints and Alembic migrations work against your TerraScale database; everything the SQL API refuses (see [SQL](/es/concepts/sql)) is refused through the dialect too.

## Install

```sh
python -m pip install "terrascale[sqlalchemy]" alembic
```

The `sqlalchemy` extra ships in the next SDK release. Until then, install the SDK from the repository: inside a clone run

```sh
python -m pip install "git+https://github.com/TerraScale/terrascale@main#subdirectory=sdk/python"
```

## Connect

Export your project ID and API key, then build the engine. The key goes in the URL's password field or is read from `TERRASCALE_API_KEY`; the project ID is the host and the database name the path:

```bash
read -rs TERRASCALE_API_KEY && export TERRASCALE_API_KEY
export TERRASCALE_PROJECT_ID=YOUR_PROJECT_ULID
```

```python
import os

from sqlalchemy import create_engine

engine = create_engine(f"terrascale://{os.environ['TERRASCALE_PROJECT_ID']}/main")

keyed = create_engine(
    "terrascale://:" + os.environ["TERRASCALE_API_KEY"] + "@"
    + os.environ["TERRASCALE_PROJECT_ID"] + "/main"
)
```

Pass `region=` as a URL query parameter to pin a region, for example `terrascale://<project_id>/main?region=gru`.

## ORM quick start

```python
from sqlalchemy import Text, create_engine, select
from sqlalchemy.orm import DeclarativeBase, Mapped, Session, mapped_column, sessionmaker

engine = create_engine(f"terrascale://{os.environ['TERRASCALE_PROJECT_ID']}/main")


class Base(DeclarativeBase):
    pass


class Note(Base):
    __tablename__ = "notes"

    id: Mapped[int] = mapped_column(primary_key=True)
    body: Mapped[str] = mapped_column(Text)


Base.metadata.create_all(engine)

with Session(engine) as session:
    note = Note(id=1, body="hello")
    session.add(note)
    nested = session.begin_nested()
    session.add(Note(id=2, body="inner"))
    nested.rollback()
    session.commit()
    print(session.scalars(select(Note).where(Note.id == 1)).all())
```

`sessionmaker(engine)` works the same way.

## Transactions and limits

The dialect defaults to `SERIALIZABLE` transactions over the SQL WebSocket session: the first statement implicitly begins a transaction, `commit()` and `rollback()` close it, and `begin_nested()` issues a real `SAVEPOINT`. A transaction lives at most 5 seconds: past the limit the server rolls it back, so keep transactions small.

Pass `isolation_level="AUTOCOMMIT"` to `create_engine` for the one-request-per-statement HTTP path with no server-side transaction; each statement (or executemany batch) is then its own atomic request.

## Migrations with Alembic

Alembic runs through the dialect. SQLite cannot alter most table definitions in place, so set `render_as_batch=True`: Alembic then rebuilds the table (create a copy, copy the rows, drop the old table, rename), which is plain SQL the SQL API accepts. `alembic revision --autogenerate` also works: reflection runs over the SQL API's schema queries (`sqlite_schema` and the schema-reading `PRAGMA`s).

```sh
alembic init migrations
```

Then edit `migrations/env.py` to point Alembic at the engine and enable batch mode:

```python
import os

from alembic import context
from sqlalchemy import create_engine

from myapp import Base

target_metadata = Base.metadata


def run_migrations_online() -> None:
    engine = create_engine(
        f"terrascale://{os.environ['TERRASCALE_PROJECT_ID']}/main"
    )
    with engine.connect() as connection:
        context.configure(
            connection=connection,
            target_metadata=target_metadata,
            render_as_batch=True,
            compare_type=True,
        )
        with context.begin_transaction():
            context.run_migrations()


run_migrations_online()
```

A migration uses `batch_alter_table` for every structural change:

```python
def upgrade() -> None:
    with op.batch_alter_table("notes") as batch_op:
        batch_op.add_column(sa.Column("title", sa.Text()))
        batch_op.alter_column("body", existing_type=sa.Text(), nullable=False)
```

Every batch migration rewrites the whole table inside one transaction, so it counts against the per-transaction limits: at most 1,024 rows changed, 8 MiB of database growth and 5 seconds ([limits](/es/limits)). A batch migration fits while the table stays small. On a large table, copy in chunks instead: create the new table, run `INSERT INTO new SELECT … WHERE id BETWEEN …` chunks with `isolation_level="AUTOCOMMIT"` (each chunk is one atomic request), then drop and rename — the copy is no longer atomic, so run it where concurrent writers are paused or tolerable. Autogenerate also reports a spurious `modify_nullable` on `INTEGER PRIMARY KEY` columns, a known SQLite-reflection artifact; skip those diffs by hand.

## What isn't supported

- Schema-write and non-schema `PRAGMA`s: only the schema-reading ones work (`table_info`, `table_xinfo`, `table_list`, `index_list`, `index_info`, `index_xinfo`, `foreign_key_list`, `foreign_key_check`) plus reading `PRAGMA foreign_keys`. Foreign keys are always enforced; turning them off is refused.
- `ATTACH`, `DETACH`, `VACUUM` (including `VACUUM INTO`), `ANALYZE`, temporary and virtual tables, `load_extension` and file functions.
- Explicit `BEGIN`/`COMMIT`/`ROLLBACK` statements (the dialect manages the transaction); `SAVEPOINT`, `RELEASE` and `ROLLBACK TO` are accepted.
- Anything else the SQL API refuses with `sql_sandbox`; see [SQL](/es/concepts/sql) and [Errors](/es/guides/errors).

</div>
