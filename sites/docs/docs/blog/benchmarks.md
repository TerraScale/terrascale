---
title: Benchmark availability
---

# Benchmark availability

No reproducible production benchmark is published here. Do not infer latency, capacity, concurrency or cost from a local example. Measure your own workload against a test database.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
