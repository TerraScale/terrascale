# Rules commands

The source CLI supports pulling, deploying and simulating database rules. See
[installation and releases](cli-releases.md) for published CLI availability.

```sh
ts rules pull app
ts rules deploy app
ts rules deploy app --yes --json
ts rules test app --operation read --table todos \
  --auth '{"uid":"alice"}' --row '{"owner_id":"alice"}'
ts rules test app --operation update --table todos \
  --auth '{"uid":"alice"}' --row '{"owner_id":"bob"}' \
  --new '{"title":"changed"}' --draft --json
```

Pull writes `terrascale.rules.json` as pretty JSON with sorted object keys. It
replaces the local file, so save local edits before pulling again. The file
contains only the rules set (`tables`), without response metadata.
`.terrascale.rules.state.json` records the project, database selector and version.
Keep that state beside the rules file; use the same database selector on deploy.
A database without rules pulls an empty tables object and a null version.

Deploy reads the current rules and prints a diff against the local file. A saved
version that differs from the current version refuses deployment: pull again and
reapply the intended changes. After showing the diff, interactive deployment asks
you to type the database selector. Unattended deployment requires `--yes`.
The write uses `If-Match` with the inspected version, or `If-None-Match: *`
for the first deployment. HTTP 412 reports that rules changed since pull, including
a change made while confirmation was pending. Success updates the saved version.
Without a state file, deploy uses the current version it inspected and confirmed.

Deploy validates against the database schema. Problems print as
`table/field: code: message` and fail the command; warnings print after success.
An unchanged diff still deploys and receives server validation.

Test sends the identity, existing row and incoming body or patch as JSON objects
to the simulator. `--operation`, `--table` and `--auth` are required;
`--row` and `--new` default to empty objects. `--draft` uses the local rules
file instead of deployed rules. The simulator reads or writes no application rows.

Text output includes `allowed`, `decidedBy`, `clause`, `columns` and any
problems. `--json` returns the simulator response unchanged, including
`results` and `problems`. A denied simulation prints its result and exits 1.
Invalid command arguments exit 2.

With `--json`, pull returns `{path,version,rules}`; deploy returns the deployed
rules response. Deploy diffs and confirmation go to stderr so stdout contains one
JSON value. Deployment failures use the normal error envelope, with
`error.problems` for schema validation and `error.status: 412` for stale versions.
