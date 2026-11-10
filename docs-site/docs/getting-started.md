---
title: Getting started overview
---

# Getting started

Start with the [getting started guide](/getting-started/) to create an account, a database and an API key. Then run your first query with the [SQL quickstart](/concepts/sql#quickstart).

## Run one SQL statement over HTTP

Send one statement to the SQL HTTP API. Replace the placeholder token and database URL with yours.

```bash
curl -sS --fail-with-body \
  -H "Authorization: Bearer YOUR_ACCESS_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"statements":[{"sql":"SELECT 1"}]}' \
  "https://api.terrascale.tech/v1/projects/YOUR_PROJECT_ID/databases/YOUR_DATABASE/sql/execute"
```

The server forwards a timed-out local publish with `TerraBase-Publish-Id` set to the same transaction id, and the home applies that write once.
