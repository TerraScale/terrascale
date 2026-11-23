---
title: TerraBase concepts
---

# TerraBase concepts

TerraBase is a SQL database. Databases are scoped to your project, and an API key or access token selects what you can reach. A route with no listener metadata is closed, and admin routes are explicit Internal.

The [SQL](/concepts/sql) page covers the query interface, [regions](/concepts/regions) and [replication and consistency](/concepts/replication) explain active-active writes, fast and strong modes and bookmarks, [rules](/concepts/rules) cover access policies, and [usage accounting](/concepts/pricing-philosophy) explains how operations and storage are metered.

Around the database, the platform runs [functions](/guides/functions) (JavaScript handlers invoked over HTTP or on a cron schedule), [file storage](/guides/file-storage) (buckets with presigned uploads and access rules) and live shapes (rule-filtered query results served and cached at the edge, updated as rows change). These run on their own stateless capacity, so a busy function never slows a database.
