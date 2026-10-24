# Slow query shapes in database Logs

The Logs page lists TerraBase query shapes last seen within the last 24 hours,
ordered by p95 latency descending. A shape without p95 sorts by maximum latency
and displays an em dash for p95. The SDK describes p95 as a histogram bucket
upper bound. The query counts, latency totals and scanned-row totals are
best-effort cumulative observations, not a rolling 24-hour aggregation.

The table is independent of the request-log HTTP status filter. It shows average
latency and scanned rows per query; a zero-query shape displays an em dash for
both averages. Shape text is truncated visually with the full encoded text in
its title. Use `"explain": true` when executing the query to inspect its plan and
suggested index.

Reads use the existing user's project-scoped `databases:read` authority and the
resolved database name through TerraScale.DotNet 0.0.7. Shape failures display an
unavailable message without failing an otherwise healthy Logs page. There are
no storage changes or new public dashboard APIs.
