# SQLAlchemy on TerraBase

SQLAlchemy 2 with the `sqlalchemy-libsql` dialect against a TerraBase
database: models, `create_all`, CRUD, a relationship and transactions.
Guide: https://docs.terrascale.tech/guides/orm-sqlalchemy

```sh
python -m venv .venv && .venv/bin/pip install -r requirements.txt
export TERRASCALE_API_KEY=...        # a Read and write key for the database
export TERRASCALE_PROJECT_ID=... TERRASCALE_DATABASE=...
.venv/bin/python app.py
```

Keep the key in your secret manager or private shell. `app.py` inserts the
same rows each run: drop the `sqlalchemy_authors` and `sqlalchemy_posts` tables to run it again.
