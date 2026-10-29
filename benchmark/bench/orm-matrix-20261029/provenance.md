# Final matrix provenance — 2026-10-29

All runs used admitted `ts-heavy-any` jobs on mariogk-dev. Every target/client pair got a new server process and disposable database. Each probe then performed three repeats. Primary results contain 540 unique measurements (180 reference, 180 TerraBase, 180 upstream); 66 additional cleanup measurements are retained, including cleanup failures. Target command exit codes were 1 as expected; the reference command exited 0.

The combined artifacts select complete client runs, never individual favorable cells:

| Artifact | Selected runs | Rows |
|---|---|---|
| `postgres-reference.jsonl.gz` | Patched dependency graph/control for Prisma, Drizzle, SQLAlchemy, psql and pgcli; final EF repeat-guard control | 204 |
| `terrabase.jsonl.gz` | Isolated Python clients; patched JS clients; final EF runner; final verbose psql runner | 201 |
| `turso-postgres.jsonl.gz` | Same selection as TerraBase | 201 |

The final psql target rerun and control use the verbose SQLSTATE parser published here. The EF target/control reruns use the exact final repeat guard. JS target/control reruns use the exact esbuild override and lock. Earlier reference-only reports remain as development history and are not inputs to the final matrix. Original mixed-client target runs are excluded because upstream shares connection state.

Backend source was TerraBase `35917df6c66fefd5385f6ecaee3b38f598def153` and upstream Turso `5c168be5ef6a32ac966daf5983502a4f694b37ad`. Runner files were staged into managed checkouts independently of their branch baseline. The SHA-256 fingerprints below identify the published runner inputs; final source hashes were compared with the corresponding managed checkouts. The supervisor and clients wrapper match on both checkouts. Python/pgcli probe and requirement files are unchanged between their isolated target run and control. EF and JS each match their own final rerun. psql matches its final rerun.

| Runner input (relative to `benchmark/orm-matrix/`) | SHA-256 |
|---|---|
| `serve.sh` | `25be64b4173594d649234ff33d96cb705f241a8ae5dfb9232b679e6631f02449` |
| `clients.sh` | `0cb01b1ad5f92a90d98eaf53bbab26740c03b5eda155f1ff0bb9714778bdd232` |
| `reference.py` | `22a53df0a6109fd55038792fbf584b45dd009ef9ae325eca57308e4620c0bece` |
| `js/probe.mjs` | `6806dfb434f5339891593034f05f0a433956f7fd19eadda6d83284ffee891f74` |
| `js/package.json` | `199b18ff534b27450271e5abedefd109993855ea430bc7dcdb746ec063f1c20e` |
| `js/package-lock.json` | `fd04e9d5a5fe05d7892e94c6977f9a4f8e3225cebfd3458e7ef6d77cbca5923a` |
| `ef/Program.cs` | `9c0a615f98939bb4b054f7e68089ec2b6dec97e547d87f5319999a13dac3b03c` |
| `ef/Directory.Packages.props` | `ac0ad2429de5f10f5b358f85c8f538a8e0d053311137393850d5a757291f207b` |
| `ef/packages.lock.json` | `47132e359371021c8aca2bafa2e92cf05f636f5fd4fa9ad59f53544e5a15e069` |
| `python/probe.py` | `9e5962d4cd5e7b5e9d2ba62610c65d276c7ec16044aff35a49056f2743005d55` |
| `python/requirements.lock` | `7d7bdbfbd96f6b8cebeadefc8836e64c07a2de0475297b3a7fb18278585f8d2c` |
| `psql/probe.py` | `20b14f02a03495dfd49c6eb5f820b742b94c3006a0e8bf8c94545d7879c0d36d` |
| `psql/run.sh` | `397b4d9d0e92e8b0c51498f60bb2d6806df705ad0833590312d652d2476f1ee3` |

PostgreSQL 18.6 and psql use `github:NixOS/nixpkgs/31ecbaca1bdefa45dffc409f0250bd8039ea0010#postgresql_18`. Upstream tursopg was built with its own locked Nix shell, Cargo lock and libclang from its locked Nixpkgs revision `cf3f5c4def3c7b5f1fc012b3d839575dbe552d43`; binary SHA-256: `c216ba8a3c375e12c00c74a18fdf0fb1b81a93ce2033fb2ed30b009cac8ee097`.

`npm-audit.json` is the actual zero-vulnerability audit of the patched JS dependency graph. No credentials are included. The target failures and prerequisite blockers are preserved in the compressed JSONL, including SQLSTATEs and redacted native-client diagnostic text. The EF failed-command log identified `LOCK TABLE "<migration-history>" IN ACCESS EXCLUSIVE MODE`; this explains the migration syntax failure rather than attributing it to CREATE TABLE.
