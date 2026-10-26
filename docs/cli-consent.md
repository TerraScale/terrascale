# Destructive operations and import confirmation

`collection drop` and `functions delete` require a terminal and the exact resource
name, or `-yes` before the command for automation. The prompt identifies the
database and project selector. Refusal, EOF, a wrong name or a failed prompt sends
no deletion request. Function deletion also removes its invocation logs.

`import` first creates or reuses its target database and stages the source file.
Those requests happen before confirmation. The CLI displays the preview, then
asks you to type the destination database name. `-yes` before `import` explicitly
consents to starting the current preview in an unattended command. Blocking
preview issues prevent starting even with `-yes`.

Refusing import confirmation leaves the staged database, job and source available
for inspection with `ts import status <db>` or cancellation with
`ts import cancel <db>`; it does not undo staging or delete the database. An
identical rerun resumes the same job. A completed import or one already confirmed
and queued/running/validating reattaches without asking again. If reanalysis
produces an unconfirmed preview, confirmation is required again.

The start request names the exact preview presented for confirmation. A changed
preview is rejected by the server. Output failures before confirmation prevent
starting; output failures after a mutation do not undo a committed operation.
