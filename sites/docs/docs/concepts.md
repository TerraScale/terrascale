---
title: TerraBase concepts
---

# TerraBase concepts

TerraBase is a SQL database. Databases are scoped to your project, and an API key or access token selects what you can reach. A route with no listener metadata is closed, and admin routes are explicit Internal.

The [SQL](/concepts/sql) page covers the query interface, and [usage accounting](/concepts/pricing-philosophy) explains how operations and storage are metered.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
