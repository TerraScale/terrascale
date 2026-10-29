"""TerraScale Python quickstart: create, read, query, SQL and delete.

    export TERRASCALE_API_KEY=...        # dashboard: API keys (Full access)
    export TERRASCALE_PROJECT_ID=...     # dashboard: Organization -> Current project
    export TERRASCALE_DATABASE_NAME=main
    python quickstart.py
"""

import os

from terrascale import ConflictError, TerraScale
from terrascale import query as q

with TerraScale() as ts:
    db = ts.database(os.environ.get("TERRASCALE_DATABASE_NAME", "main"))
    try:
        db.create_collection("py_quickstart")
    except ConflictError:
        pass  # already exists
    docs = db.collection("py_quickstart")

    created = docs.create({"title": "Hello from Python", "done": False})
    doc = docs.get(created.id)
    print("read:", doc.data)

    page = docs.query(q.eq("/done", False), sort=[q.asc("/title")], limit=10)
    print("query:", len(page.items), "item(s)")

    rows = db.sql("SELECT * FROM py_quickstart LIMIT 10")
    print("sql:", rows.matched, "row(s)")

    docs.delete(doc.id, if_match=doc.version)
    print("deleted", doc.id)
