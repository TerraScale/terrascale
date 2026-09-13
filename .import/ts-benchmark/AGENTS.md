AI IS NOT ALLOWED TO EDIT THIS FILE ONLY HUMANS CAN!

# RULES

- This repo must use C# only
- This is terrabase a multi region database with a really efficient and fast sync engine
- When interacting with forgejo use the "fj" cli
- We need to expose metrics to prometheus compatible
- We should use crates that are tested and popular whenever possible instead of creating our own solutions
- When building rust always
  - Lower priority (recommended, yields to other work)
  - When running make and using the Makefile always use the lowest cpu priority
- All of all layers for example the storage layer must use an interface and be abstract so we can either use SQLite, or ZoneTree or w/e

# Libraries
- We should use MagincOnion for C# <-> C# comunnication
- We should use TUnit for tests

# Benchmark
- We need to save all benchmarks in an easy to see .md tables so we can compare from the very beginning the difference and improvements
- Benchmarks land as `.md` tables in `bench/`.
