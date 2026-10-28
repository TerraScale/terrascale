# SQL table name reuse after an atomic drop

A `/sql/execute` transaction may create a table, insert rows and drop it. After
commit, the name is available for another CREATE, locally and after replication.
DROP followed by CREATE in the same transaction also replaces the definition and
clears the old rows, including rows already present on another replica.

Collection metadata advances its local clock stamp between writes within a
transaction. A drop therefore follows rows inserted earlier in that transaction,
and a recreation follows the drop. The final replicated delta retains the drop's
clear marker alongside the new definition; otherwise a receiver could restore
the name while retaining old rows.

The stored state and replication encodings are unchanged. Existing nodes can
merge the corrected stamps and clear markers. During a rolling deployment,
transactions executed on an old node can still produce the original defect.
This change does not repair previously retained collections automatically or
change the merge rule for a genuinely concurrent write newer than a drop.

Listener regressions cover next-request reuse, same-transaction recreation and
rollback. Three-region listener tests cover reuse from each region and atomic
recreation with both new and previously replicated rows. The existing collection
drop convergence tests retain coverage of late delivery and concurrent writes.
