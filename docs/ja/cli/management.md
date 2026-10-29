---
title: CLI management reference
---

<!-- i18n:fallback:ja -->
> **翻訳レビュー待ち。** このページはまだ日本語の人力レビューがなく、英語の原文を表示しています。

<div lang="en" dir="ltr">

# CLI management reference

Check `ts help <command>` for your installed release. Select a project with `-project` before the command. New management commands return typed metadata; JSON automation follows the [shared contract](./automation).

| Command or option | Contract |
| --- | --- |
| `orgs create <name>`, `orgs info <selector>`, `orgs rename <selector> <new-name>` | Sign-in only; IDs take precedence over names. `orgs` includes organizations without projects. |
| `projects info <selector>`, `projects rename <selector> <new-name>` | Sign-in only; use an ID or `-org` to disambiguate names. `projects create <name>` uses your selected/only organization, creating an initial organization when needed. |
| `orgs delete <selector>`, `projects delete <selector>` | Resolve identity first; type its ID in a terminal or explicitly consent with `-yes`. Server permissions remain required. |
| `-home-region <region>`, `-replica-regions <comma-list-or-none>`, `-placement all` before `db create <db>` | Atomic create-time placement intent. All placement conflicts with an explicit replica list. Omitted placement is server-selected; invalid/home-only placement may be refused. |
| `backup policy <db>`, `backup policy-set <db> <cron-or-none> <days>` | Five-field UTC cron; retention 1–35 days. Quote cron. `none` removes the custom schedule. Policy metadata is not proof of usable backup history. |
| `backup window <db> [destination]`, `backup plan <db> <RFC3339> [destination]` | Destination is `platform` (default) or `customer`. Window reports available history; plan inspects metadata, not object integrity. |
| `backup restore-at <db> <RFC3339> <new-db> [destination]` | Restores independently into a new database; requires permitted history and spare database capacity. |
| `backup destination get/set/delete` | Project-wide customer destination metadata. Set reads one JSON object from stdin and requires `-yes`; delete requires consent. |

Project deletion refuses a nonempty or unreadable database inventory before deletion. This preflight does not serialize concurrent database creation and does not promise tenant teardown or billing cleanup. Organization deletion requires an empty organization and appropriate role.

For destination set, redirect a private file into `ts -yes backup destination set`. Input is at most 16 KiB: `endpoint`, `region`, `bucket`, `mode` (`both` or `customer`), `accessKeyId`, `secretKey`. Never put credentials in argv/logs; output contains only public metadata. Database-restricted keys cannot administer project destinations. Removing the destination preserves customer objects and resumes platform shipping.

</div>
