# SQLAlchemy and Alembic

Source: [SQLAlchemy guide](https://docs.terrascale.tech/guides/orm-sqlalchemy).

## 1. Install the dialect

The guide's terrascale[sqlalchemy] extra is intended for the next SDK release;
Python registry publication is pending. Install source and dependencies:

~~~sh
python -m pip install "git+https://github.com/TerraScale/terrascale@main#subdirectory=sdk/python"
python -m pip install SQLAlchemy alembic
~~~

Sources: [SDK availability](https://docs.terrascale.tech/guides/sdks),
[SQLAlchemy install](https://docs.sqlalchemy.org/en/20/intro.html#installation),
[Alembic install](https://alembic.sqlalchemy.org/en/latest/front.html#installation).

## 2. Connect and define the schema

The URL host is the project ID and path is the database name. Leave the key
in TERRASCALE_API_KEY rather than including it in a logged URL.
A region query parameter, such as region=gru, pins a region.

~~~python
import os
from sqlalchemy import Text, create_engine, select
from sqlalchemy.orm import DeclarativeBase, Mapped, Session, mapped_column

engine = create_engine(
    f"terrascale://{os.environ['TERRASCALE_PROJECT_ID']}/{os.environ['TERRASCALE_DATABASE_NAME']}"
)

class Base(DeclarativeBase):
    pass

class Note(Base):
    __tablename__ = "notes"
    id: Mapped[int] = mapped_column(primary_key=True)
    body: Mapped[str] = mapped_column(Text)
~~~

For a disposable prototype Base.metadata.create_all(engine) creates tables.
For deployed schemas, choose Alembic ownership instead.

## 3. Configure Alembic ownership

~~~sh
alembic init migrations
~~~

Set target_metadata to Base.metadata in migrations/env.py. In its online runner,
use the engine above and configure the actual connection:

~~~python
with engine.connect() as connection:
    context.configure(
        connection=connection,
        target_metadata=Base.metadata,
        render_as_batch=True,
        compare_type=True,
    )
    with context.begin_transaction():
        context.run_migrations()
~~~

Import context from alembic and Base from the application's model module.
Use Alembic revision --autogenerate and upgrade head via the project's CLI;
review the generated batch_alter_table operations before applying.
Source: [Alembic workflow](https://alembic.sqlalchemy.org/en/latest/tutorial.html).
Alembic owns history here; do not also apply its revisions through ts migrate.

## 4. Verify and handle dialect limits

~~~python
with Session(engine) as session:
    session.add(Note(id=1, body="hello"))
    session.commit()
    print(session.scalars(select(Note).where(Note.id == 1)).all())
~~~

Default SERIALIZABLE transactions use a SQL WebSocket session and last at most
five seconds; begin_nested uses real savepoints. AUTOCOMMIT uses one atomic HTTP
request per statement or executemany batch.

Batch table rebuilds rewrite all rows in one transaction: 1,024 changed rows,
8 MiB growth and five seconds still apply. Large copies need chunks under
AUTOCOMMIT and a plan for paused or tolerable concurrent writes. Skip spurious
modify_nullable diffs on INTEGER PRIMARY KEY columns after manual review.

Foreign keys are always enforced. Schema-writing/non-schema PRAGMAs, ATTACH,
DETACH, VACUUM, ANALYZE, temporary/virtual tables and file functions are refused.
Do not issue SQL BEGIN/COMMIT/ROLLBACK yourself; the dialect manages transactions.
Source: [SQLAlchemy limits](https://docs.terrascale.tech/guides/orm-sqlalchemy).
