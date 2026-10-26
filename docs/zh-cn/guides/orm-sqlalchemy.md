---
title: Use SQLAlchemy
description: Connect SQLAlchemy 2 to a TerraBase database with the sqlalchemy-libsql dialect, create tables, and run CRUD, relationships and transactions with the ORM.
---

<!-- i18n:fallback:zh-cn -->
> **翻译待审。** 此页面尚无经人工审校的中文译文，暂显示英文原文。

<div lang="en" dir="ltr">

# Use SQLAlchemy

SQLAlchemy 2 talks to TerraBase through the `sqlalchemy-libsql` dialect (`sqlite+libsql://`) over the libSQL remote protocol. You keep SQLAlchemy's SQLite dialect, models and sessions. Read [Connect with libSQL clients](/zh-cn/guides/libsql-clients) for the URL, tokens and supported SQL.

A complete app is in [`examples/orm-sqlalchemy`](https://github.com/TerraScale/ts-docs/tree/main/examples/orm-sqlalchemy): models, table creation, CRUD, a relationship and transactions.

This example reserves `sqlalchemy_authors` and `sqlalchemy_posts`, separate from the Documents quickstart's `posts` collection and the other ORM examples. Use these tables only for the sample data. Treat the complete app and inline walkthrough as alternatives: both insert the same sample rows, so start with empty reserved tables.

## Install

```sh
pip install "sqlalchemy==2.0.44" "sqlalchemy-libsql==0.2.0"
```

## Connect

Create an API key with **Read and write** access to the database in the dashboard ([Authentication](/zh-cn/guides/authentication)). Keep the key on your server. The engine exchanges it for a fresh access token each time it opens a connection:

```python
# db.py
import base64
import json
import os
import urllib.parse
import urllib.request

from sqlalchemy import create_engine, event


def access_token() -> str:
    """Client credentials exchange: the key ID is the part before "_"."""
    key = os.environ["TERRASCALE_API_KEY"]
    basic = base64.b64encode(f"{key.split('_', 1)[0]}:{key}".encode()).decode()
    request = urllib.request.Request(
        "https://auth.terrascale.tech/terrascale/oauth/token",
        data=urllib.parse.urlencode({"grant_type": "client_credentials"}).encode(),
        headers={"Authorization": f"Basic {basic}"},
    )
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)["access_token"]


project = os.environ["TERRASCALE_PROJECT_ID"]
database = os.environ["TERRASCALE_DATABASE"]
# Keep the trailing slash; secure=true selects HTTPS.
engine = create_engine(
    f"sqlite+libsql://api.terrascale.tech/v1/projects/{project}/databases/{database}/libsql/?secure=true"
)


@event.listens_for(engine, "do_connect")
def _fresh_token(dialect, conn_rec, cargs, cparams):
    # Access tokens last 15 minutes: every new pooled connection gets a fresh one.
    cparams["auth_token"] = access_token()
```

Pooled connections outlive their token. For a long-running server, set `pool_recycle=600` on `create_engine` so connections are replaced before their token expires.

## Models and tables

```python
# models.py
from sqlalchemy import ForeignKey, String
from sqlalchemy.orm import DeclarativeBase, Mapped, mapped_column, relationship


class Base(DeclarativeBase):
    pass


class Author(Base):
    __tablename__ = "sqlalchemy_authors"

    id: Mapped[int] = mapped_column(primary_key=True)
    name: Mapped[str] = mapped_column(String(100))
    email: Mapped[str] = mapped_column(String(200), unique=True)
    posts: Mapped[list["Post"]] = relationship(back_populates="author")


class Post(Base):
    __tablename__ = "sqlalchemy_posts"

    id: Mapped[int] = mapped_column(primary_key=True)
    author_id: Mapped[int] = mapped_column(ForeignKey("sqlalchemy_authors.id"), index=True)
    title: Mapped[str] = mapped_column(String(200))
    published: Mapped[bool] = mapped_column(default=False)
    author: Mapped[Author] = relationship(back_populates="posts")
```

```python
from db import engine
from models import Base

Base.metadata.create_all(engine)  # creates missing tables and their indexes
```

`ForeignKey` is accepted but not enforced, as SQLite does with foreign keys off: the relationship works, but the database stores a post whose `author_id` matches no author.

## Queries

```python
from sqlalchemy import select
from sqlalchemy.orm import Session, selectinload

with Session(engine) as session:
    session.add(Author(name="Ada", email="ada@example.com",
                       posts=[Post(title="Notes on the Analytical Engine", published=True), Post(title="Draft")]))
    session.commit()

    ada = session.scalars(
        select(Author).where(Author.email == "ada@example.com").options(selectinload(Author.posts))
    ).one()
    rows = session.execute(select(Post.title, Author.name).join(Post.author)).all()

    draft = session.scalars(select(Post).where(Post.title == "Draft")).one()
    draft.published = True
    session.commit()

    session.delete(draft)
    session.commit()
```

## Transactions

```python
from sqlalchemy.exc import IntegrityError

with Session(engine) as session, session.begin():
    grace = Author(name="Grace", email="grace@example.com")
    session.add(grace)
    session.flush()
    session.add(Post(author_id=grace.id, title="Compilers"))
# Committed here; an exception inside the block rolls everything back.

try:
    with Session(engine) as session, session.begin():
        session.add(Author(name="Linus", email="linus@example.com"))
        session.add(Author(name="Dup", email="ada@example.com"))  # unique violation
except (IntegrityError, ValueError) as error:
    pass  # nothing was written
```

Transactions are optimistic: a commit that conflicts with another writer fails with `SQLITE_BUSY`, and nothing is written. Retry the whole transaction.

The `libsql-experimental` driver under `sqlalchemy-libsql` raises a plain `ValueError` for database errors, with the SQLite code in the message (for example `SQLITE_CONSTRAINT_UNIQUE`). It does not raise a DB-API error, so SQLAlchemy cannot turn it into `IntegrityError`. Catch both, and check the message when you need the code.

## Limits

- Table reflection (`MetaData.reflect`, `inspect(engine)`) and Alembic autogenerate work: TerraBase answers the SQLite catalog read-only (tables, columns, indexes, foreign keys and named `UNIQUE` constraints). Migrations that alter an existing table need `ALTER TABLE`, which TerraBase refuses.
- `LargeBinary` columns cannot hold values: blob values are refused.
- The [libSQL limits](/zh-cn/guides/libsql-clients#limits) apply: reads scan the tables they reference, and a transaction holds at most 256 statements.

</div>
