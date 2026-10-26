# Hrana parameter binding compatibility

Hrana v2/v3 execute requests, batch steps and v3 cursor steps validate bindings before execution, including stored `sql_id` statements. Binding failures are statement errors with code `ARGS_INVALID` inside HTTP 200 responses (or cursor `step_error` entries). Describe requests require no bindings.

- Explicit names retain their prefix: `SELECT :x,@x,$x` with `:x=1`, `@x=2`, `$x=3` returns `[1,2,3]`.
- A bare name supplies matching `:`, `@` and `$` parameters. Exact prefixed names take precedence over bare aliases regardless of argument order: `x=1,$x=3` supplies `:x=1,@x=1,$x=3`.
- Positional arguments address SQLite parameter slots starting at 1, including named slots. Named bindings override positional values for the same slot. Repeated occurrences of a named parameter share one slot.
- Every slot through the highest SQLite parameter number requires a value. `SELECT ?3` takes three positional values, including fillers for unused slots 1 and 2. `?NNN` is positional; use `args`, not `named_args`, for it.
- Missing values, excess positional values, unknown named keys and repeated identical `named_args` names are errors. Explicit SQL NULL is a supplied value. A bare alias and its prefixed name are distinct keys, with the precedence above.
- Values remain bound data; SQL-looking strings do not become statement text. Quoted literals and comments do not introduce parameters.

This intentionally corrects earlier Hrana behavior that collapsed explicit prefixes, accepted missing bindings as NULL, ignored unknown keys and silently overwrote duplicate names. Clients relying on those behaviors must supply valid bindings. There is no package or SDK signature change and no storage change.

The separate HTTP `/v1/projects/{project}/databases/{database}/sql/execute` contract retains its documented unbound-NULL behavior; Hrana validation does not change the shared SQL binder. Parameterless Hrana sequence requests retain their existing script behavior.

Upstream evidence reviewed 2026-10-26:

- [Hrana 3 Statements](https://github.com/tursodatabase/libsql/blob/d8bc862d20bedc72945a1da27c8b7045414c0261/docs/HRANA_3_SPEC.md#statements) requires missing/unexpected argument errors, prefix identity and named-over-positional precedence, while allowing servers to reject mixed arguments. TerraBase supports mixed arguments.
- The [Rust server conversion](https://github.com/tursodatabase/libsql/blob/d8bc862d20bedc72945a1da27c8b7045414c0261/libsql-server/src/hrana/stmt.rs) rejects mixed arguments and collects duplicate names into a map. Its [binder and regression cases](https://github.com/tursodatabase/libsql/blob/d8bc862d20bedc72945a1da27c8b7045414c0261/libsql-server/src/query.rs) require numbered slots, support bare aliases and prefer exact names. TerraBase explicitly rejects duplicate wire names instead of silently overwriting them.
- The official [JS client conversion](https://github.com/tursodatabase/libsql-client-ts/blob/9496c782af413f6380235c7f5d0c97b5371f49b7/packages/libsql-client/src/hrana.ts#L357) forwards array arguments by index and object keys through `bindName` without stripping prefixes. These are source observations, not a claim of a newly run official-client conformance suite.
