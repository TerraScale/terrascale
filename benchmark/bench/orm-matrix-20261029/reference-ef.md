# EF Core PostgreSQL reference control, 2026-10-29

Measured on mariogk-dev through `ts-heavy-any`, using a disposable PostgreSQL 17.6 process. EF Core 10.0.14, Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3, and Npgsql 10.0.3 are pinned with a locked dependency graph in `benchmark/orm-matrix/ef/`. The executable targets the repository's .NET 11 SDK and built with zero warnings/errors under its analyzers.

| Connect | Introspect | Table migration | FK migration | Index migration | CRUD | Commit/rollback | JSONB | Prepared | SQLSTATE |
|---|---|---|---|---|---|---|---|---|---|
| 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 | 3/3 |

Each repeat owns ULID-named tables and its own migration history. Three real EF migrations are applied through the provider's migrator; the final model matches its snapshot without warning suppression. Introspection uses Npgsql's reverse-engineering services and asserts PK, FK, indexes, and the jsonb column. CRUD uses tracked changes, relationship queries, bulk updates/deletes and cascade deletion. JSON checks round-trip and server-side containment; preparation covers explicit Npgsql preparation and LINQ under automatic preparation. Error checks require `23505`, `23503`, and `42P01`.

After adding repeat-count validation, the executable rejects 0 and -1 with exit status 2 and no JSONL output. Repeated all checks on PostgreSQL 18.6: 30/30 passed again; [current raw measurements](ef-postgres-reference-final.jsonl.gz).

Raw reference measurements: [ef-postgres-reference-initial.jsonl.gz](ef-postgres-reference-initial.jsonl.gz), 30 unique passing rows. These controls validate the runner; the final W0-1 report separately records both target frontends. The original remote artifacts were `mariogk-dev:~/orm-matrix-ef-20261029T161858Z/`.

With a private `DATABASE_URL` and `MATRIX_SURFACE` supplied inside an admitted heavy job:

```sh
MATRIX_REPEAT=3 nice -n 19 dotnet run --project benchmark/orm-matrix/ef/OrmMatrix.Ef.csproj -c Release
```
