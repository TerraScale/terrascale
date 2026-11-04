# Destructive operations

`functions delete` requires a terminal and the exact function name, or `-yes`
before the command for automation. The prompt identifies the database and
project selector. Refusal, EOF, a wrong name or a failed prompt sends no deletion
request. Function deletion also removes its invocation logs.

Output failures after a mutation do not undo a committed operation.
