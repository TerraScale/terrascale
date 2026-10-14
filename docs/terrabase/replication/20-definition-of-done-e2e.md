# 20. Definition of done (e2e)

Run by `tests/TerraBase.E2E` (PR #6) against Fly after every deploy, pinned with `fly-prefer-region` (60 s
convergence deadline).

| Id | Scenario | PR #6 test |
| --- | --- | --- |
| E1 | write in gru is readable in sjc, same content | `Write_in_gru_is_readable_in_sjc_with_the_same_content` |
| E2 | write in sjc is readable in gru | `Write_in_sjc_is_readable_in_gru_with_the_same_content` |
| E3 | concurrent writes, same field → one identical value | `Concurrent_writes_to_the_same_field_converge…` |
| E4 | concurrent writes, different fields → both survive | `Concurrent_writes_to_different_fields_both_survive_expected_to_fail…` (flip) |
| E5 | delete in one region, edit in the other → same state | `Delete_in_one_region_and_edit_in_the_other…` |
| E6 | bulk: 1000 documents split across regions, counts and checksums match | `Bulk_1000_documents…` |
| E7 | sjc machine stopped, writes in gru, started → catches up | `FlyOutageE2ETests.Sjc_machine_stopped_then_started…` |
| E8 | concurrent `inc` in both regions → sum; `set 0` resets | new |
| E9 | concurrent `add` and `remove` of one element → add wins | new |
| E10 | changes feed: cursor taken in gru, resumed in sjc → no missing event, per-origin order | new |
| E11 | SQL `UPDATE` in gru vs PATCH of another field in sjc → both; `SET n = n + 1` in both → +2 | new |
| E12 | database created in sjc is usable from gru; same name created in both → exactly one survives | new |
| E13 | move gru → sjc under write load → no lost write, operation `succeeded` | new |
| E14 | delete → GC → recreate → no ghost fields; region back after GC → no resurrection | new |
| E15 | ts-auth refresh token reused in gru and sjc within 1 s → family revoked in both | new (auth) |
