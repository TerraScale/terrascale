---
title: Python quickstart
description: Create, read, update and query TerraBase documents with the Python SDK.
---

<!-- i18n:fallback:es -->
> **Traducción pendiente.** Esta página aún no tiene revisión humana en español y muestra el contenido original en inglés.

<div lang="en" dir="ltr">

# Python quickstart

Run a complete document workflow against your TerraScale database in about five minutes: create a collection, write a document, read it back, update it, query it and delete it.

## Prerequisites

- Python 3.10 or newer
- A TerraScale account, a project with a database, and an API key. Follow steps 1–4 of [Getting started](/es/getting-started/): the key needs **Read and write** access to that database.
- The **Current project** ID (a ULID) from the dashboard's **Organization** page, and the API key. Export both:

```bash
read -rs TERRASCALE_API_KEY && export TERRASCALE_API_KEY   # keeps the key out of history
export TERRASCALE_PROJECT_ID=YOUR_PROJECT_ULID
export TERRASCALE_DATABASE_NAME=main                       # your database's name
```

## Install the SDK

The package is not on PyPI yet; install it from the monorepo:

```bash
python -m pip install "git+https://github.com/TerraScale/terrascale@main#subdirectory=sdk/python"
```

## Run the quickstart

Save this as `quickstart.py`. The client reads the key and project from the environment and exchanges the key for short-lived access tokens automatically.

```python
import os

from terrascale import ConflictError, TerraScale, query as q

with TerraScale() as ts:                    # reads TERRASCALE_API_KEY / TERRASCALE_PROJECT_ID
    db = ts.database(os.environ["TERRASCALE_DATABASE_NAME"])

    # A new database has no collections; name_taken means the collection already exists.
    try:
        db.create_collection("todos")
    except ConflictError as exc:
        if exc.code != "name_taken":
            raise
    todos = db.collection("todos")

    # Write a document under a server-generated id.
    created = todos.create({"title": "Ship the quickstart", "done": False})
    print("created", created.id, "at version", created.version)

    # Read it back.
    doc = todos.get(created.id)
    print("read", doc.id, doc.data["title"])

    # Update it conditionally: if_match rejects the write when another writer moved the document on.
    updated = todos.patch(doc.id, merge={"done": True}, if_match=doc.version)
    print("patched at version", updated.version)

    # Query for the change just written.
    for d in todos.items(q.eq("/done", True), limit=10):
        print("query matched", d.id, d.data["title"])

    # Delete it.
    todos.delete(doc.id, if_match=updated.version)
    print("deleted", doc.id)
```

Run it with the key exported in your shell:

```bash
python quickstart.py
```

Expected output (ids and versions vary):

```text
created 01JDV7QZGK0DMB3W3QV2AB5CDM at version tbv1-22c40936cb275b5fd9e31f293a3883e8
read 01JDV7QZGK0DMB3W3QV2AB5CDM Ship the quickstart
patched at version tbv1-11c4ca9f65a4729652a1e907835fd792
query matched 01JDV7QZGK0DMB3W3QV2AB5CDM Ship the quickstart
deleted 01JDV7QZGK0DMB3W3QV2AB5CDM
```

## How it works

- **The key never reaches TerraBase.** The client exchanges it with ts-auth for a short-lived access token, caches it and refreshes it automatically. Keep the key on your server; see [authentication](/es/guides/authentication).
- **Collections are created explicitly.** `create_collection` answers `409 name_taken` when the name exists, which makes the quickstart safe to run twice.
- **Versions are opaque concurrency tokens.** Pass the version from a read into `if_match` on the next write; TerraBase answers `412` when the document changed in between. See [errors and safe retries](/es/guides/errors).
- **Documents are JSON.** Document ids are canonical ULIDs; filter paths are JSON Pointers such as `/done`.

## Next steps

- [SDK reference](/es/guides/sdks) — the five SDKs and their install commands
- [JavaScript](/es/getting-started/javascript), [C#](/es/getting-started/csharp) and [Go](/es/getting-started/go) quickstarts
- [SQL over HTTP](/es/guides/sql-http) for the SQL interface

</div>
