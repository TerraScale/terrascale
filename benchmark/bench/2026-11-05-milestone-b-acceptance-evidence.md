# Milestone B acceptance: retained evidence

Companion to [the acceptance report](2026-11-05-milestone-b-acceptance.md). Product access uses public interfaces only. Credential, bearer-token and epoch-key values are excluded. These are observations, not prior-thread claims. Repeated Server-Timing names on forwarded responses use the last endpoint-attributed entry in corrected summaries/samples. The historical raw summary.json retains its initial first-entry analysis; complete raw headers remain authoritative.

## Raw file manifest

Retained on beta under `/home/mariogk/.t3/worktrees/terrascale/t3-b1c4ae61/benchmark/bench/milestone-b-acceptance-20261105/`. SHA-256 below identifies each captured file. Request sample timings are also shipped in [the samples document](2026-11-05-milestone-b-acceptance-samples.md). Temporary probe scripts are not part of this documentation change. Raw JSONL containers can include launcher banner lines and cleanup objects; request analyses use JSON event objects with a kind field, preserving every request event.

| File | Bytes | SHA-256 |
|---|---:|---|
| `account-closure-denial.json` | 2790 | `ca29b45c0680ee6c46662144234803ca0fcfc5a5ff46644d09d27b6d259860dd` |
| `account-closure.json` | 229 | `31a18a407350b067f3d6950e220a27da6fb5dd0b57826f8390bceb6aae623766` |
| `cdn-after-420s.json` | 13781 | `9459997b5529576c1b8801be2e0c917a32e3bc4a2cc76a0d410d871fc68e06d4` |
| `cdn-after-delete.json` | 2430 | `2ec00997ffa11b85c54056223a05b04764ee7c40d06ebfcec2a027d658deb41e` |
| `cdn-regions.json` | 3067 | `0c62f7d84c57a657e9b646bc077b81e21c987e100c051796f3f6ccd27de7de53` |
| `cdn-retest-after-delete.json` | 2613 | `af35915918e00f383abc4aaafea2af6bb02c54efe50f4655f5631941b200acf8` |
| `cdn-retest-before-delete.json` | 4523 | `4f38ea0cde580e13309fbe04a81fc155e0e0dd561d50596431707b0becd24cd1` |
| `cdn-retest-create.json` | 2418 | `ebb010e190785d206b7e4ff752111c38e1d96c5700d18dd0e1ee3c973a6525a3` |
| `cdn-retest-deletion.json` | 8632 | `0e71d95ee63957764ae6c3a5d717777f1df8f0b9f1ff4c8cdf2b2a382a76da4c` |
| `cdn-retest-shapes.json` | 15371 | `62056540ff4bc6b2780bb84a0c285645619e165aa5cea5cee46e9e09aaf92d71` |
| `confirm-measurements.jsonl` | 2678490 | `d00ef47dfd6be840dda7ff39c9e406f4a78cdb4846e5c1d7f6b8e0555a2e1263` |
| `confirm-summary.json` | 3585 | `12413c2755a705f2e253d7bc7698ecfdbc7465e248e5295e2766351e0c86013c` |
| `deletion.json` | 5532 | `cf828cc0ff1c12a650641ef3312ac69571c85d21c6619cd2407546d56d7ee087` |
| `final-cdn-after-delete.json` | 2613 | `44bb84bcfef9ad110e0c7e8dc3cd48cfc3c0f82080cbf2e7fde1b13b6173626b` |
| `final-cdn-before-delete.json` | 4540 | `3116ce502aa573988a7188ceaa198c6bf04c18af3b48abbffb7769242083e481` |
| `final-database-cleanup.json` | 18870 | `44d084da11b4388ed12739cad8ae7494a2b875e4e6e2786a4130b184d74ee3b6` |
| `final-shapes.json` | 12408 | `34cf388af090b4cce82ee19b1ebbab41d292f65b3d8abeb02629ccf8521c6300` |
| `free-measurements.jsonl` | 1706941 | `08b14c5dad9a17c24114fc6ef8c52b33e42d6060746c28e9b1f4efc1602e6504` |
| `home-durable-functional.jsonl` | 11598 | `690524debaac31017a5fc2caaeefe464ff2e7f1c977d754509e9a810794004bc` |
| `move-approx-times.json` | 1706 | `80806463389c50aebd222a908b64f00d2d09b24fa6cf043c4df1e9e320185cdf` |
| `move-followup.json` | 5044 | `df075acfcc72eb75e0e4aab2bc68e5bf13027c5f4d0701d27874b09f3ca7576b` |
| `move-retest.jsonl` | 1003277 | `55706a7d9e876f3e7de5ed27f2f6982d571fdb3b95e084407b0171accb403f04` |
| `move.jsonl` | 374247 | `d8c90f6405f07e178a992e9e971aff5ba848d76a3ccaec28c73cf1a4a23dc80c` |
| `postfix-measurements.jsonl` | 2670808 | `667fca92822429d656a4ae84f772ca69d8d8845367fbe50f212385e0f8815c5f` |
| `postfix-sql-error-followup.json` | 370 | `084b3f0fc4755261d3561f21a876daacc15eaa499f51d8c2ccb4dbbd930b7dfa` |
| `postfix-summary.json` | 2097 | `0c1ce08d271861705fc361428ba43a6e488ee70c7d2f04f175b0cafd992c94cc` |
| `pre-delete-placement.json` | 341 | `d2e7c2a33f26d8ece8f717cbd46d995fe8ea9a37043f2940a45adccbdb6fe78f` |
| `pro-measurements.jsonl` | 1704048 | `e7ed1d44d93b086b3265b43a652561dd53ee686599211c76ef3294e18948917d` |
| `pro-read-only.jsonl` | 144670 | `16c8de8bd99ace08c11ea591d9acb188d245cba46f4bc3bb954206866d4a3a20` |
| `pro-shapes.json` | 22078 | `9cf492206904e5a09b0869819081f3d03f108d295028ab43af81375801a0e350` |
| `psql-pro.json` | 521 | `ff70d1523dbd2ac642f0c8c4f471a14e531be09acf0c6833a96f39baa0f06254` |
| `psql-r9-retest.json` | 663 | `2ffc552506ebecb2114acc0416c3f8aae98a0856750af91eca9e5cd2674778e4` |
| `psql.json` | 564 | `55652f6028b2e8b54dd7798deeaca301b5f2fa5b0b6ad319673d78e7370540b5` |
| `public-versions.json` | 870 | `166ecd4b007d15f96b6f7e5f027c68d5dc680a20705b414801a4bca39dcb5e0e` |
| `shapes.json` | 43328 | `063498be532b062b233d26cba566878e5cfaa00b68c163a6aff035b939c49277` |
| `summary.json` | 960 | `eea659fb8a1b42b119cc8607922458f808db31881051c16c479c7210c9121a97` |
| `credential-cleanup.json` | 451 | `671b4340d6dbfbcfaa49f02d2052ecd4465000440854b842411d86d00d1df85d` |

## Screenshot manifest

Captured through preview tools; placement uses supported light and dark appearances. Files are unchanged PNG captures.

| Screenshot | Pixels | SHA-256 |
|---|---|---|
| [billing-free.png](../../docs/qa/milestone-b-acceptance-20261105/billing-free.png) | 1280 × 800 | `68bf34855c371d43105bf6cd3d66e36b80b5e16f79cef80e344f423aecf4f512` |
| [billing-pro-after-reads.png](../../docs/qa/milestone-b-acceptance-20261105/billing-pro-after-reads.png) | 1280 × 800 | `e215832922b7a86d1cfabd2a74397256aba2c158c75e3c940e7404be3c3fd042` |
| [billing-pro-before.png](../../docs/qa/milestone-b-acceptance-20261105/billing-pro-before.png) | 1280 × 800 | `e215832922b7a86d1cfabd2a74397256aba2c158c75e3c940e7404be3c3fd042` |
| [placement-free-dark.png](../../docs/qa/milestone-b-acceptance-20261105/placement-free-dark.png) | 1280 × 800 | `88815231fbf6777edc103c07ab14e65c458111b4a503f33de85f59251b180c3e` |
| [placement-free-light.png](../../docs/qa/milestone-b-acceptance-20261105/placement-free-light.png) | 1280 × 800 | `1c6bf089f9e006876a3d4319107cf9ef720f4a5ebcfde2f7159917857cce5ce8` |
| [placement-pro-after-home-move-dark.png](../../docs/qa/milestone-b-acceptance-20261105/placement-pro-after-home-move-dark.png) | 1280 × 800 | `822009a742cd0ac81d4981807dfe4e794984c4f9b5f82a3a3561bedf0d1edee4` |
| [placement-pro-after-home-move-light.png](../../docs/qa/milestone-b-acceptance-20261105/placement-pro-after-home-move-light.png) | 1280 × 800 | `64f1bb11084c6886a515531a749cceb3d9b2279852c71f5a31f8a07c597b3860` |
| [placement-pro-dark.png](../../docs/qa/milestone-b-acceptance-20261105/placement-pro-dark.png) | 1280 × 800 | `4d1ecde846fb64362b037166dcf2dadb3c7c4692030b695fa7157e91bbe75655` |
| [placement-pro-light.png](../../docs/qa/milestone-b-acceptance-20261105/placement-pro-light.png) | 1280 × 800 | `587b751c1abe14c1c4d1ab6a5a491c7f5ef1b824571aa3a49482f23e18fa605c` |
| [usage-free-before.png](../../docs/qa/milestone-b-acceptance-20261105/usage-free-before.png) | 1280 × 800 | `dbec6574d05ecfb3b5d2ffdcc9c832aa53fb7b58ceafd5d7afdcbffbd1984145` |
| [usage-free-during.png](../../docs/qa/milestone-b-acceptance-20261105/usage-free-during.png) | 1280 × 800 | `72b23b106e9fe930e44717a207d7ff808aee411793f59408b7258564f8fec8f2` |
| [usage-pro-after-reads-light.png](../../docs/qa/milestone-b-acceptance-20261105/usage-pro-after-reads-light.png) | 1280 × 800 | `e8b2032d0777b10c2b6cbf9e6efc5f1ac1013985878e1e682dc536339c1d83de` |
| [usage-pro-after-writes-dark.png](../../docs/qa/milestone-b-acceptance-20261105/usage-pro-after-writes-dark.png) | 1280 × 800 | `7dc14976318d8c61d75b6c65d70abe148db32f7e552b15451311340d402a9c82` |
| [usage-pro-before.png](../../docs/qa/milestone-b-acceptance-20261105/usage-pro-before.png) | 1280 × 800 | `2d20552c113a757b70937835bd19d42855554f078470c75d24685b35fdda0631` |

## Public health/version

```json
[
  {
    "host": "dash.terrascale.tech",
    "status": 200,
    "body": null,
    "headers": {}
  },
  {
    "host": "gru.api.terrascale.tech",
    "status": 200,
    "body": {
      "status": "ok",
      "version": "0.1.0",
      "region": "gru"
    },
    "headers": {
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "host": "sjc.api.terrascale.tech",
    "status": 200,
    "body": {
      "status": "ok",
      "version": "0.1.0",
      "region": "sjc"
    },
    "headers": {
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    }
  },
  {
    "host": "fra.api.terrascale.tech",
    "status": 200,
    "body": {
      "status": "ok",
      "version": "0.1.0",
      "region": "fra"
    },
    "headers": {
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    }
  }
]
```

## Read-only workload usage counters

Regional database usage is distinct from the dashboard aggregate billing counter. Each regional write counter remains 343 throughout 100 SELECTs per region. Dashboard screenshots show the aggregate billable write units remain 981 and usage charges remain $0.00. Empty hourly buckets are omitted below.

```json
[
  {
    "kind": "usage",
    "label": "before-read-only",
    "region": "gru",
    "status": 200,
    "body": {
      "databaseId": "01M6PER4PDC8FM24ZYKX1PJ6JE",
      "storageBytes": 4083440,
      "logicalBytes": 0,
      "reads": 606,
      "writes": 343,
      "period": {
        "from": "2026-11-04T19:00:00+00:00",
        "to": "2026-11-05T19:00:00+00:00",
        "granularity": "hour",
        "reads": 606,
        "writes": 343
      },
      "series": [
        {
          "start": "2026-11-05T18:00:00+00:00",
          "reads": 606,
          "writes": 343
        }
      ]
    }
  },
  {
    "kind": "usage",
    "label": "before-read-only",
    "region": "sjc",
    "status": 200,
    "body": {
      "databaseId": "01M6PER4PDC8FM24ZYKX1PJ6JE",
      "storageBytes": 4083440,
      "logicalBytes": 0,
      "reads": 606,
      "writes": 343,
      "period": {
        "from": "2026-11-04T19:00:00+00:00",
        "to": "2026-11-05T19:00:00+00:00",
        "granularity": "hour",
        "reads": 606,
        "writes": 343
      },
      "series": [
        {
          "start": "2026-11-05T18:00:00+00:00",
          "reads": 606,
          "writes": 343
        }
      ]
    }
  },
  {
    "kind": "usage",
    "label": "before-read-only",
    "region": "fra",
    "status": 200,
    "body": {
      "databaseId": "01M6PER4PDC8FM24ZYKX1PJ6JE",
      "storageBytes": 4083440,
      "logicalBytes": 0,
      "reads": 606,
      "writes": 343,
      "period": {
        "from": "2026-11-04T19:00:00+00:00",
        "to": "2026-11-05T19:00:00+00:00",
        "granularity": "hour",
        "reads": 606,
        "writes": 343
      },
      "series": [
        {
          "start": "2026-11-05T18:00:00+00:00",
          "reads": 606,
          "writes": 343
        }
      ]
    }
  },
  {
    "kind": "usage",
    "label": "after-read-only",
    "region": "gru",
    "status": 200,
    "body": {
      "databaseId": "01M6PER4PDC8FM24ZYKX1PJ6JE",
      "storageBytes": 4083440,
      "logicalBytes": 0,
      "reads": 706,
      "writes": 343,
      "period": {
        "from": "2026-11-04T19:00:00+00:00",
        "to": "2026-11-05T19:00:00+00:00",
        "granularity": "hour",
        "reads": 706,
        "writes": 343
      },
      "series": [
        {
          "start": "2026-11-05T18:00:00+00:00",
          "reads": 706,
          "writes": 343
        }
      ]
    }
  },
  {
    "kind": "usage",
    "label": "after-read-only",
    "region": "sjc",
    "status": 200,
    "body": {
      "databaseId": "01M6PER4PDC8FM24ZYKX1PJ6JE",
      "storageBytes": 4083440,
      "logicalBytes": 0,
      "reads": 706,
      "writes": 343,
      "period": {
        "from": "2026-11-04T19:00:00+00:00",
        "to": "2026-11-05T19:00:00+00:00",
        "granularity": "hour",
        "reads": 706,
        "writes": 343
      },
      "series": [
        {
          "start": "2026-11-05T18:00:00+00:00",
          "reads": 706,
          "writes": 343
        }
      ]
    }
  },
  {
    "kind": "usage",
    "label": "after-read-only",
    "region": "fra",
    "status": 200,
    "body": {
      "databaseId": "01M6PER4PDC8FM24ZYKX1PJ6JE",
      "storageBytes": 4083440,
      "logicalBytes": 0,
      "reads": 706,
      "writes": 343,
      "period": {
        "from": "2026-11-04T19:00:00+00:00",
        "to": "2026-11-05T19:00:00+00:00",
        "granularity": "hour",
        "reads": 706,
        "writes": 343
      },
      "series": [
        {
          "start": "2026-11-05T18:00:00+00:00",
          "reads": 706,
          "writes": 343
        }
      ]
    }
  }
]
```

## Home-durable functional check

Nine writes with `TerraScale-Durability: home`; each followed by its first home SELECT.

```json
[
  {
    "kind": "durable-functional-write",
    "region": "gru",
    "ok": true,
    "ms": 236.826,
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=5.95, home_lookup;dur=3.40, qos;dur=1.61, project;dur=0.03, placement;dur=0.02, tenant;dur=1.83, execute;dur=8.96, app;dur=21.91;desc=\"gru\"",
      "x-request-id": "01M6PFWF804VGXNHW15EEVKR80",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "id": "01M6PFWF1KF205M7GEFQJX7DMQ",
    "n": 0,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": 1
    }
  },
  {
    "kind": "durable-immediate-home-read",
    "region": "gru",
    "ok": true,
    "ms": 240.126,
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.11, auth;dur=0.47, home_lookup;dur=0.63, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.10, execute;dur=0.69, app;dur=2.06;desc=\"gru\"",
      "x-request-id": "01M6PFWFG2R6YR31T5SRQ9MNZR",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "source": "gru",
    "id": "01M6PFWF1KF205M7GEFQJX7DMQ",
    "rows": [
      {
        "id": "01M6PFWF1KF205M7GEFQJX7DMQ",
        "writer": "gru"
      }
    ]
  },
  {
    "kind": "durable-functional-write",
    "region": "gru",
    "ok": true,
    "ms": 32.011,
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.11, home_lookup;dur=1.40, qos;dur=0.04, project;dur=0.02, placement;dur=0.01, tenant;dur=0.08, execute;dur=9.42, app;dur=11.20;desc=\"gru\"",
      "x-request-id": "01M6PFWFGVNBT7QKM1CFXFDZ6S",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "id": "01M6PFWFGG7THA1053T8CKS6C2",
    "n": 1,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": 2
    }
  },
  {
    "kind": "durable-immediate-home-read",
    "region": "gru",
    "ok": true,
    "ms": 24.452,
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.11, auth;dur=0.11, home_lookup;dur=0.74, qos;dur=0.05, project;dur=0.02, placement;dur=0.01, tenant;dur=0.08, execute;dur=0.62, app;dur=1.74;desc=\"gru\"",
      "x-request-id": "01M6PFWFHVB7FHG2GRVYQZSPET",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "source": "gru",
    "id": "01M6PFWFGG7THA1053T8CKS6C2",
    "rows": [
      {
        "id": "01M6PFWFGG7THA1053T8CKS6C2",
        "writer": "gru"
      }
    ]
  },
  {
    "kind": "durable-functional-write",
    "region": "gru",
    "ok": true,
    "ms": 30.066,
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.08, auth;dur=0.09, home_lookup;dur=0.59, qos;dur=0.03, project;dur=0.01, placement;dur=0.01, tenant;dur=0.06, execute;dur=9.02, app;dur=9.89;desc=\"gru\"",
      "x-request-id": "01M6PFWFJKNAZGPYZARJFJ6T7W",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "id": "01M6PFWFJ9EBAHT2JA549GVJF6",
    "n": 2,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": 3
    }
  },
  {
    "kind": "durable-immediate-home-read",
    "region": "gru",
    "ok": true,
    "ms": 25.86,
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.12, home_lookup;dur=0.86, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.07, execute;dur=0.62, app;dur=1.87;desc=\"gru\"",
      "x-request-id": "01M6PFWFKJG6CRPBQE3A2NJES0",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "source": "gru",
    "id": "01M6PFWFJ9EBAHT2JA549GVJF6",
    "rows": [
      {
        "id": "01M6PFWFJ9EBAHT2JA549GVJF6",
        "writer": "gru"
      }
    ]
  },
  {
    "kind": "durable-functional-write",
    "region": "sjc",
    "ok": true,
    "ms": 1934.303,
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.14, auth;dur=194.95, home_lookup;dur=3.66, qos;dur=1.23, project;dur=0.03, placement;dur=0.01, tenant;dur=3.63, replica_open;dur=1.27, route;dur=0.82, local_begin;dur=8.03, local_execute;dur=0.29, ack;dur=949.17, local_capture;dur=0.28, local_hlc;dur=3.57, local_seal;dur=744.97, push_publish;dur=1.65, push_hold;dur=7.51, local_commit;dur=3.77, home_wait;dur=187.42, execute;dur=0.10, app;dur=1163.31;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PFWG9FZPSPMC04VZCCWBEH",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PFWFMBMR6NYHVSJP2PY38V",
    "n": 0,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "kind": "durable-immediate-home-read",
    "region": "gru",
    "ok": true,
    "ms": 42.302,
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.13, auth;dur=9.58, home_lookup;dur=3.94, qos;dur=1.67, project;dur=0.03, placement;dur=0.02, tenant;dur=2.11, execute;dur=0.78, app;dur=18.28;desc=\"gru\"",
      "x-request-id": "01M6PFWHH5EB8ZY8CAQ22SDWJR",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "source": "sjc",
    "id": "01M6PFWFMBMR6NYHVSJP2PY38V",
    "rows": [
      {
        "id": "01M6PFWFMBMR6NYHVSJP2PY38V",
        "writer": "sjc"
      }
    ]
  },
  {
    "kind": "durable-functional-write",
    "region": "sjc",
    "ok": true,
    "ms": 611.548,
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=197.19, home_lookup;dur=3.55, qos;dur=1.24, project;dur=0.02, placement;dur=0.01, tenant;dur=3.53, replica_open;dur=1.77, route;dur=0.75, local_begin;dur=6.96, local_execute;dur=0.20, ack;dur=202.02, local_capture;dur=0.26, local_hlc;dur=0.00, local_seal;dur=0.81, push_publish;dur=0.53, push_hold;dur=6.11, local_commit;dur=2.94, home_wait;dur=191.36, execute;dur=0.10, app;dur=417.47;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PFWHN8NQQPQPW1BBDZX0YA",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PFWHJ41G4GFTQ0TBHB4673",
    "n": 1,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "kind": "durable-immediate-home-read",
    "region": "gru",
    "ok": true,
    "ms": 38.329,
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.42, home_lookup;dur=3.78, qos;dur=3.33, project;dur=0.03, placement;dur=0.02, tenant;dur=3.77, execute;dur=0.67, app;dur=12.14;desc=\"gru\"",
      "x-request-id": "01M6PFWJ5NN7YC443J8V5WSR4B",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "source": "sjc",
    "id": "01M6PFWHJ41G4GFTQ0TBHB4673",
    "rows": [
      {
        "id": "01M6PFWHJ41G4GFTQ0TBHB4673",
        "writer": "sjc"
      }
    ]
  },
  {
    "kind": "durable-functional-write",
    "region": "sjc",
    "ok": true,
    "ms": 405.779,
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.19, auth;dur=0.36, home_lookup;dur=0.80, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.12, replica_open;dur=0.15, route;dur=0.55, local_begin;dur=6.42, local_execute;dur=0.19, ack;dur=201.90, local_capture;dur=0.33, local_hlc;dur=0.00, local_seal;dur=0.09, push_publish;dur=0.47, push_hold;dur=6.39, local_commit;dur=2.85, home_wait;dur=191.76, execute;dur=0.09, app;dur=210.82;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PFWJ9JC58KMMZFQKGKVVHV",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PFWJ6E1QYDG2K9VW3R9PFA",
    "n": 2,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "kind": "durable-immediate-home-read",
    "region": "gru",
    "ok": true,
    "ms": 25.586,
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.13, auth;dur=0.12, home_lookup;dur=0.84, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.08, execute;dur=0.66, app;dur=1.89;desc=\"gru\"",
      "x-request-id": "01M6PFWJKFZXHVQND521RNZ9MR",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "source": "sjc",
    "id": "01M6PFWJ6E1QYDG2K9VW3R9PFA",
    "rows": [
      {
        "id": "01M6PFWJ6E1QYDG2K9VW3R9PFA",
        "writer": "sjc"
      }
    ]
  },
  {
    "kind": "durable-functional-write",
    "region": "fra",
    "ok": true,
    "ms": 1645.567,
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.11, auth;dur=193.15, home_lookup;dur=3.55, qos;dur=1.30, project;dur=0.02, placement;dur=0.01, tenant;dur=5.27, replica_open;dur=1.25, route;dur=0.66, local_begin;dur=7.10, local_execute;dur=0.20, ack;dur=607.89, local_capture;dur=0.25, local_hlc;dur=3.63, local_seal;dur=399.09, push_publish;dur=0.50, push_hold;dur=7.25, local_commit;dur=3.19, home_wait;dur=193.97, execute;dur=0.13, app;dur=820.64;desc=\"fra\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PFWKAW12K9F2M5TTVF825X",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "id": "01M6PFWJMETR0DYHNRVMPWCNRJ",
    "n": 0,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "kind": "durable-immediate-home-read",
    "region": "gru",
    "ok": true,
    "ms": 39.678,
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.08, auth;dur=7.59, home_lookup;dur=6.64, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.10, execute;dur=1.15, app;dur=15.61;desc=\"gru\"",
      "x-request-id": "01M6PFWM87AFPHK9WXSEFCHDQX",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "source": "fra",
    "id": "01M6PFWJMETR0DYHNRVMPWCNRJ",
    "rows": [
      {
        "id": "01M6PFWJMETR0DYHNRVMPWCNRJ",
        "writer": "fra"
      }
    ]
  },
  {
    "kind": "durable-functional-write",
    "region": "fra",
    "ok": true,
    "ms": 633.764,
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.11, auth;dur=193.36, home_lookup;dur=3.08, qos;dur=1.10, project;dur=0.02, placement;dur=0.01, tenant;dur=3.41, replica_open;dur=1.05, route;dur=0.50, local_begin;dur=6.25, local_execute;dur=0.18, ack;dur=214.75, local_capture;dur=0.22, local_hlc;dur=0.00, local_seal;dur=0.09, push_publish;dur=0.60, push_hold;dur=5.68, local_commit;dur=2.67, home_wait;dur=205.49, execute;dur=0.11, app;dur=423.92;desc=\"fra\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PFWMCB5HSHJMCDTFFKTPGY",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "id": "01M6PFWM93989B58J3QAXVTZ02",
    "n": 1,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "kind": "durable-immediate-home-read",
    "region": "gru",
    "ok": true,
    "ms": 26.588,
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.33, home_lookup;dur=1.40, qos;dur=0.02, project;dur=0.01, placement;dur=0.01, tenant;dur=0.06, execute;dur=0.93, app;dur=2.89;desc=\"gru\"",
      "x-request-id": "01M6PFWMX90Y9YSQA5N50JNT5F",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "source": "fra",
    "id": "01M6PFWM93989B58J3QAXVTZ02",
    "rows": [
      {
        "id": "01M6PFWM93989B58J3QAXVTZ02",
        "writer": "fra"
      }
    ]
  },
  {
    "kind": "durable-functional-write",
    "region": "fra",
    "ok": true,
    "ms": 438.604,
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.11, auth;dur=0.28, home_lookup;dur=0.78, qos;dur=0.03, project;dur=0.01, placement;dur=0.01, tenant;dur=0.12, replica_open;dur=0.26, route;dur=0.50, local_begin;dur=8.28, local_execute;dur=0.32, ack;dur=216.64, local_capture;dur=0.47, local_hlc;dur=0.00, local_seal;dur=0.12, push_publish;dur=1.26, push_hold;dur=6.15, local_commit;dur=2.83, home_wait;dur=205.80, execute;dur=0.10, app;dur=227.44;desc=\"fra\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PFWN0ZE7ZVKAZ085BJ8KCZ",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "id": "01M6PFWMXRPQD1XVCE35GGRMMC",
    "n": 2,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "kind": "durable-immediate-home-read",
    "region": "gru",
    "ok": true,
    "ms": 24.928,
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.13, auth;dur=0.12, home_lookup;dur=0.50, qos;dur=0.03, project;dur=0.01, placement;dur=0.01, tenant;dur=0.06, execute;dur=0.82, app;dur=1.69;desc=\"gru\"",
      "x-request-id": "01M6PFWNBT2Y05VJ6W3PH2VKKQ",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "source": "fra",
    "id": "01M6PFWMXRPQD1XVCE35GGRMMC",
    "rows": [
      {
        "id": "01M6PFWMXRPQD1XVCE35GGRMMC",
        "writer": "fra"
      }
    ]
  }
]
```

## R9 home move (#1106)

A single SJC writer waits 500 ms between attempts; the move begins after three accepted writes. No automatic write retry. Exact ack wall-clock timestamps and internal JetStream sequences were not captured. The following approximate times derive from client-generated row ULIDs plus monotonic request durations and must not be treated as exact server commit times.

```json
[
  {
    "sequence": 0,
    "id": "01M6PFHCBE7FNKT99HSNRG4P5S",
    "ok": true,
    "id_wall_time": "2026-11-05T18:29:01.934+00:00",
    "approx_client_completion": "2026-11-05T18:29:03.530+00:00",
    "client_duration_ms": 1596.75,
    "request_id": "01M6PFHD1H1X8SEFZECJ6931GG"
  },
  {
    "sequence": 1,
    "id": "01M6PFHECZHEDJ2N08KJ9JGK97",
    "ok": true,
    "id_wall_time": "2026-11-05T18:29:04.031+00:00",
    "approx_client_completion": "2026-11-05T18:29:04.439+00:00",
    "client_duration_ms": 408.287,
    "request_id": "01M6PFHEG20ZR8GCNW0T0B2141"
  },
  {
    "sequence": 2,
    "id": "01M6PFHF9C75EQ0RVVK9H4EFHS",
    "ok": true,
    "id_wall_time": "2026-11-05T18:29:04.940+00:00",
    "approx_client_completion": "2026-11-05T18:29:05.736+00:00",
    "client_duration_ms": 796.072,
    "request_id": "01M6PFHFCDXN8B3P2G646665PF"
  },
  {
    "sequence": 3,
    "id": "01M6PFHGHWM3KE8YC88SDDDP89",
    "ok": true,
    "id_wall_time": "2026-11-05T18:29:06.236+00:00",
    "approx_client_completion": "2026-11-05T18:29:06.655+00:00",
    "client_duration_ms": 419.99,
    "request_id": "01M6PFHGMYBSSVKFTC0NQ6BYVM"
  },
  {
    "sequence": 4,
    "id": "01M6PFHHENZT9C54C8RFWFZVWZ",
    "ok": true,
    "id_wall_time": "2026-11-05T18:29:07.157+00:00",
    "approx_client_completion": "2026-11-05T18:29:07.376+00:00",
    "client_duration_ms": 219.012,
    "request_id": "01M6PFHHHQR3Z76451BKJJ495N"
  },
  {
    "sequence": 5,
    "id": "01M6PFHJ54GRXAHF9FF4MXRZEF",
    "ok": false,
    "id_wall_time": "2026-11-05T18:29:07.876+00:00",
    "approx_client_completion": "2026-11-05T18:29:08.567+00:00",
    "client_duration_ms": 691.726,
    "request_id": "01M6PFHJ85FS6Q3DTPK30QTM4J"
  }
]
```

Sanitized request responses and exact-ID comparisons (large row inventories replaced by count and canonical SHA-256):

```json
[
  {
    "kind": "move_baseline",
    "count": 1923,
    "missing_acknowledged": [],
    "unexpected": []
  },
  {
    "kind": "move_write",
    "region": "sjc",
    "ok": true,
    "ms": 1596.75,
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.11, auth;dur=192.68, home_lookup;dur=6.50, qos;dur=1.34, project;dur=0.02, placement;dur=0.03, tenant;dur=6.31, replica_open;dur=1.35, route;dur=0.47, local_begin;dur=241.32, local_execute;dur=0.22, ack;dur=339.93, local_capture;dur=0.27, local_hlc;dur=0.00, local_seal;dur=327.03, push_publish;dur=1.81, push_hold;dur=6.51, local_commit;dur=3.96, execute;dur=0.32, app;dur=790.62;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PFHD1H1X8SEFZECJ6931GG",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PFHCBE7FNKT99HSNRG4P5S",
    "n": 0,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "kind": "move_write",
    "region": "sjc",
    "ok": true,
    "ms": 408.287,
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=193.10, home_lookup;dur=0.85, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.12, replica_open;dur=0.18, route;dur=0.61, local_begin;dur=7.50, local_execute;dur=0.24, ack;dur=10.25, local_capture;dur=0.34, local_hlc;dur=0.00, local_seal;dur=0.11, push_publish;dur=0.57, push_hold;dur=6.44, local_commit;dur=2.68, execute;dur=0.10, app;dur=213.13;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PFHEG20ZR8GCNW0T0B2141",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PFHECZHEDJ2N08KJ9JGK97",
    "n": 1,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "kind": "move_write",
    "region": "sjc",
    "ok": true,
    "ms": 796.072,
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.15, auth;dur=0.35, home_lookup;dur=0.81, qos;dur=0.03, project;dur=0.01, placement;dur=0.01, tenant;dur=0.12, replica_open;dur=0.21, route;dur=0.56, local_begin;dur=8.34, local_execute;dur=0.25, ack;dur=11.39, local_capture;dur=0.27, local_hlc;dur=0.00, local_seal;dur=0.09, push_publish;dur=0.44, push_hold;dur=7.63, local_commit;dur=2.85, execute;dur=0.32, app;dur=22.57;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PFHFCDXN8B3P2G646665PF",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PFHF9C75EQ0RVVK9H4EFHS",
    "n": 2,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "kind": "move_write",
    "region": "sjc",
    "ok": true,
    "ms": 419.99,
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.17, auth;dur=197.02, home_lookup;dur=4.54, qos;dur=1.19, project;dur=0.03, placement;dur=0.01, tenant;dur=3.91, replica_open;dur=1.55, route;dur=1.05, local_begin;dur=6.16, local_execute;dur=0.20, ack;dur=9.35, local_capture;dur=0.27, local_hlc;dur=0.00, local_seal;dur=0.13, push_publish;dur=0.67, push_hold;dur=5.57, local_commit;dur=2.59, execute;dur=0.09, app;dur=225.27;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PFHGMYBSSVKFTC0NQ6BYVM",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PFHGHWM3KE8YC88SDDDP89",
    "n": 3,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "kind": "move_write",
    "region": "sjc",
    "ok": true,
    "ms": 219.012,
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.28, home_lookup;dur=5.02, qos;dur=1.18, project;dur=0.04, placement;dur=0.01, tenant;dur=3.62, replica_open;dur=1.18, route;dur=0.91, local_begin;dur=0.73, local_execute;dur=0.20, ack;dur=10.57, local_capture;dur=0.50, local_hlc;dur=0.00, local_seal;dur=0.12, push_publish;dur=0.60, push_hold;dur=6.04, local_commit;dur=2.92, execute;dur=0.10, app;dur=23.98;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PFHHHQR3Z76451BKJJ495N",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PFHHENZT9C54C8RFWFZVWZ",
    "n": 4,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "kind": "move_home",
    "region": "gru",
    "ok": true,
    "ms": 2261.791,
    "status": 200,
    "headers": {
      "x-request-id": "01M6PFHG9B0EGGAQYKM5ZHBE7C",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "value": {
      "id": "01M6PER4PDC8FM24ZYKX1PJ6JE",
      "project_id": "01M6PENV06CRPSJYDDRZTF75R4",
      "name": "milestone-b-acceptance",
      "created_at": "2026-11-05T18:15:14.8938153+00:00",
      "region": "gru",
      "home_region": "fra",
      "branched_from": null,
      "raw": {
        "id": "01M6PER4PDC8FM24ZYKX1PJ6JE",
        "projectId": "01M6PENV06CRPSJYDDRZTF75R4",
        "name": "milestone-b-acceptance",
        "branchedFrom": null,
        "createdAt": "2026-11-05T18:15:14.8938153+00:00",
        "region": "gru",
        "homeRegion": "fra",
        "replicaRegions": [
          "gru",
          "sjc"
        ],
        "allRegions": true,
        "previousName": null,
        "conflictOf": null,
        "expiresAt": null
      },
      "previous_name": null,
      "conflict_of": null,
      "expires_at": null,
      "replica_regions": [
        "gru",
        "sjc"
      ],
      "all_regions": true
    }
  },
  {
    "kind": "move_write",
    "region": "sjc",
    "ok": false,
    "ms": 691.726,
    "status": 421,
    "headers": {
      "server-timing": "execute;dur=1.89, app;dur=1.90;desc=\"fra\"",
      "x-request-id": "01M6PFHJ85FS6Q3DTPK30QTM4J",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "id": "01M6PFHJ54GRXAHF9FF4MXRZEF",
    "n": 5,
    "error": {
      "type": "APIError",
      "status": 421,
      "code": "region_pin_mismatch",
      "request_id": "01M6PFHJ85FS6Q3DTPK30QTM4J"
    }
  },
  {
    "kind": "move_inventory",
    "region": "gru",
    "ok": true,
    "ms": 1525.459,
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=6.72, home_lookup;dur=3.68, qos;dur=1.69, project;dur=0.03, placement;dur=0.13, tenant;dur=7.27, replica_open;dur=1192.95, execute;dur=6.15, app;dur=1218.75;desc=\"gru\"",
      "x-request-id": "01M6PFHK2B45NNWQXPNEKANCSE",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "row_count": 1926,
    "distinct_ids": 1926,
    "canonical_inventory_sha256": "f78423111810b6221d1d24cb877ab133bf840f3f89e1294dc3c8f6a07453a712"
  },
  {
    "kind": "move_comparison",
    "region": "gru",
    "before": 1923,
    "accepted_writes": 5,
    "attempted_writes": 6,
    "rows": 1926,
    "distinct": 1926,
    "missing": [
      "01M6PFHGHWM3KE8YC88SDDDP89",
      "01M6PFHHENZT9C54C8RFWFZVWZ"
    ],
    "unexpected": [],
    "changed": [],
    "info": {
      "id": "01M6PER4PDC8FM24ZYKX1PJ6JE",
      "project_id": "01M6PENV06CRPSJYDDRZTF75R4",
      "name": "milestone-b-acceptance",
      "created_at": "2026-11-05T18:15:14.8938153+00:00",
      "region": "gru",
      "home_region": "fra",
      "branched_from": null,
      "raw": {
        "id": "01M6PER4PDC8FM24ZYKX1PJ6JE",
        "projectId": "01M6PENV06CRPSJYDDRZTF75R4",
        "name": "milestone-b-acceptance",
        "branchedFrom": null,
        "createdAt": "2026-11-05T18:15:14.8938153+00:00",
        "region": "gru",
        "homeRegion": "fra",
        "replicaRegions": [
          "gru",
          "sjc"
        ],
        "allRegions": true,
        "previousName": null,
        "conflictOf": null,
        "expiresAt": null
      },
      "previous_name": null,
      "conflict_of": null,
      "expires_at": null,
      "replica_regions": [
        "gru",
        "sjc"
      ],
      "all_regions": true
    }
  },
  {
    "kind": "move_inventory",
    "region": "sjc",
    "ok": false,
    "ms": 1102.09,
    "status": 421,
    "headers": {
      "server-timing": "execute;dur=0.59, app;dur=0.60;desc=\"fra\"",
      "x-request-id": "01M6PFHN0ZJWSXQR4SKEK2N77H",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "error": {
      "type": "APIError",
      "status": 421,
      "code": "region_pin_mismatch",
      "request_id": "01M6PFHN0ZJWSXQR4SKEK2N77H"
    }
  },
  {
    "kind": "move_inventory",
    "region": "fra",
    "ok": true,
    "ms": 1666.339,
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.11, auth;dur=196.61, home_lookup;dur=11.30, qos;dur=1.57, project;dur=0.02, placement;dur=0.01, tenant;dur=6.02, execute;dur=13.53, app;dur=229.20;desc=\"fra\"",
      "x-request-id": "01M6PFHP5TYXMFMJA4DDTWE7WQ",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "row_count": 1926,
    "distinct_ids": 1926,
    "canonical_inventory_sha256": "f78423111810b6221d1d24cb877ab133bf840f3f89e1294dc3c8f6a07453a712"
  },
  {
    "kind": "move_comparison",
    "region": "fra",
    "before": 1923,
    "accepted_writes": 5,
    "attempted_writes": 6,
    "rows": 1926,
    "distinct": 1926,
    "missing": [
      "01M6PFHGHWM3KE8YC88SDDDP89",
      "01M6PFHHENZT9C54C8RFWFZVWZ"
    ],
    "unexpected": [],
    "changed": [],
    "info": {
      "id": "01M6PER4PDC8FM24ZYKX1PJ6JE",
      "project_id": "01M6PENV06CRPSJYDDRZTF75R4",
      "name": "milestone-b-acceptance",
      "created_at": "2026-11-05T18:15:16.0147053+00:00",
      "region": "fra",
      "home_region": "fra",
      "branched_from": null,
      "raw": {
        "id": "01M6PER4PDC8FM24ZYKX1PJ6JE",
        "projectId": "01M6PENV06CRPSJYDDRZTF75R4",
        "name": "milestone-b-acceptance",
        "branchedFrom": null,
        "createdAt": "2026-11-05T18:15:16.0147053+00:00",
        "region": "fra",
        "homeRegion": "fra",
        "replicaRegions": [
          "gru",
          "sjc"
        ],
        "allRegions": true,
        "previousName": null,
        "conflictOf": null,
        "expiresAt": null
      },
      "previous_name": null,
      "conflict_of": null,
      "expires_at": null,
      "replica_regions": [
        "gru",
        "sjc"
      ],
      "all_regions": true
    }
  }
]
```

Later public SQL reconciliation and region pinning:

```json
[
  {
    "ok": true,
    "rows": [
      {
        "id": "01M6PFHCBE7FNKT99HSNRG4P5S",
        "writer": "sjc",
        "sequence": 0,
        "body": "move-load"
      },
      {
        "id": "01M6PFHECZHEDJ2N08KJ9JGK97",
        "writer": "sjc",
        "sequence": 1,
        "body": "move-load"
      },
      {
        "id": "01M6PFHF9C75EQ0RVVK9H4EFHS",
        "writer": "sjc",
        "sequence": 2,
        "body": "move-load"
      }
    ],
    "region": "gru",
    "pin": true,
    "ms": 266.379,
    "at": "2026-11-05T18:29:49Z",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.11, auth;dur=8.61, home_lookup;dur=5.77, qos;dur=1.54, project;dur=0.03, placement;dur=0.02, tenant;dur=6.33, replica_open;dur=1.67, execute;dur=1.00, app;dur=25.07;desc=\"gru\"",
      "x-request-id": "01M6PFJTF6CFC8TKEME6Q74PCK",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "ok": true,
    "rows": [
      {
        "id": "01M6PFHCBE7FNKT99HSNRG4P5S",
        "writer": "sjc",
        "sequence": 0,
        "body": "move-load"
      },
      {
        "id": "01M6PFHECZHEDJ2N08KJ9JGK97",
        "writer": "sjc",
        "sequence": 1,
        "body": "move-load"
      },
      {
        "id": "01M6PFHF9C75EQ0RVVK9H4EFHS",
        "writer": "sjc",
        "sequence": 2,
        "body": "move-load"
      }
    ],
    "region": "gru",
    "pin": false,
    "ms": 205.927,
    "at": "2026-11-05T18:29:49Z",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.11, auth;dur=0.40, home_lookup;dur=0.85, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.12, replica_open;dur=0.06, execute;dur=0.51, app;dur=2.13;desc=\"gru\"",
      "x-request-id": "01M6PFJTPZFGHKBBEWW2X3R8DV",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "ok": false,
    "error": "APIError",
    "code": "region_pin_mismatch",
    "region": "sjc",
    "pin": true,
    "ms": 1150.211,
    "at": "2026-11-05T18:29:50Z",
    "status": 421,
    "headers": {
      "server-timing": "execute;dur=0.49, app;dur=0.49;desc=\"fra\"",
      "x-request-id": "01M6PFJVDN0ZX3CP5V0994Z199",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    }
  },
  {
    "ok": true,
    "rows": [
      {
        "id": "01M6PFHCBE7FNKT99HSNRG4P5S",
        "writer": "sjc",
        "sequence": 0,
        "body": "move-load"
      },
      {
        "id": "01M6PFHECZHEDJ2N08KJ9JGK97",
        "writer": "sjc",
        "sequence": 1,
        "body": "move-load"
      },
      {
        "id": "01M6PFHF9C75EQ0RVVK9H4EFHS",
        "writer": "sjc",
        "sequence": 2,
        "body": "move-load"
      }
    ],
    "region": "sjc",
    "pin": false,
    "ms": 1323.881,
    "at": "2026-11-05T18:29:51Z",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.11, auth;dur=196.25, home_lookup;dur=4.24, qos;dur=2.42, project;dur=0.02, placement;dur=0.01, tenant;dur=4.82, execute;dur=1.06, app;dur=208.93;desc=\"fra\"",
      "x-request-id": "01M6PFJWGKE511X8AGP0R59JVV",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    }
  },
  {
    "ok": true,
    "rows": [
      {
        "id": "01M6PFHCBE7FNKT99HSNRG4P5S",
        "writer": "sjc",
        "sequence": 0,
        "body": "move-load"
      },
      {
        "id": "01M6PFHECZHEDJ2N08KJ9JGK97",
        "writer": "sjc",
        "sequence": 1,
        "body": "move-load"
      },
      {
        "id": "01M6PFHF9C75EQ0RVVK9H4EFHS",
        "writer": "sjc",
        "sequence": 2,
        "body": "move-load"
      }
    ],
    "region": "fra",
    "pin": true,
    "ms": 1070.688,
    "at": "2026-11-05T18:29:53Z",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.22, auth;dur=194.38, home_lookup;dur=3.04, qos;dur=1.02, project;dur=0.02, placement;dur=0.01, tenant;dur=1.32, execute;dur=1.01, app;dur=201.02;desc=\"fra\"",
      "x-request-id": "01M6PFJXY7J6DPD6B72R9E0XNZ",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    }
  },
  {
    "ok": true,
    "rows": [
      {
        "id": "01M6PFHCBE7FNKT99HSNRG4P5S",
        "writer": "sjc",
        "sequence": 0,
        "body": "move-load"
      },
      {
        "id": "01M6PFHECZHEDJ2N08KJ9JGK97",
        "writer": "sjc",
        "sequence": 1,
        "body": "move-load"
      },
      {
        "id": "01M6PFHF9C75EQ0RVVK9H4EFHS",
        "writer": "sjc",
        "sequence": 2,
        "body": "move-load"
      }
    ],
    "region": "fra",
    "pin": false,
    "ms": 1031.949,
    "at": "2026-11-05T18:29:54Z",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.10, auth;dur=196.67, home_lookup;dur=1.82, qos;dur=0.09, project;dur=0.12, placement;dur=0.01, tenant;dur=0.45, execute;dur=1.35, app;dur=200.60;desc=\"fra\"",
      "x-request-id": "01M6PFJYZ2QKEKC8V47C2WQRNH",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    }
  }
]
```

Placement before deletion:

```json
[
  {
    "region": "gru",
    "home": "fra",
    "all_regions": true,
    "replicas": [
      "gru",
      "sjc"
    ]
  },
  {
    "region": "sjc",
    "home": "fra",
    "all_regions": true,
    "replicas": []
  },
  {
    "region": "fra",
    "home": "fra",
    "all_regions": true,
    "replicas": [
      "gru",
      "sjc"
    ]
  }
]
```

## Stock psql

Free-plan insert plus same-session read:

```json
[
  {
    "region": "gru",
    "id": "01M6PEZ46E1767XRDH15GYQZNT",
    "exit": 0,
    "ms": 291.4,
    "stdout": "INSERT 0 1\n01M6PEZ46E1767XRDH15GYQZNT|gru|psql\n",
    "stderr": ""
  },
  {
    "region": "sjc",
    "id": "01M6PEZ4FJKXJDWN7RW3DFF1DC",
    "exit": 0,
    "ms": 2286.54,
    "stdout": "INSERT 0 1\n01M6PEZ4FJKXJDWN7RW3DFF1DC|sjc|psql\n",
    "stderr": ""
  },
  {
    "region": "fra",
    "id": "01M6PEZ6Q00N1DZ3K4XNKZ6FD0",
    "exit": 0,
    "ms": 2393.65,
    "stdout": "INSERT 0 1\n01M6PEZ6Q00N1DZ3K4XNKZ6FD0|fra|psql\n",
    "stderr": ""
  }
]
```

Pro transport check after the move; the SJC timeout was not replayed and its uncertain ID was later reconciled absent:

```json
[
  {
    "region": "gru",
    "id": "01M6PFPSFB83SC7TWH0S0QYRPE",
    "ms": 1155.7,
    "exit": 0,
    "stdout": "INSERT 0 1\n01M6PFPSFB83SC7TWH0S0QYRPE|gru|psql-pro\n",
    "stderr": ""
  },
  {
    "region": "sjc",
    "id": "01M6PFPTKF992CKSYX87W84853",
    "ms": 30033.15,
    "exit": null,
    "error": "30s deadline"
  },
  {
    "region": "fra",
    "id": "01M6PFQQY0XAC07W833DHSXENB",
    "ms": 2015.57,
    "exit": 0,
    "stdout": "INSERT 0 1\n01M6PFQQY0XAC07W833DHSXENB|fra|psql-pro\n",
    "stderr": ""
  }
]
```

## Shape subscriptions and CDN cache

Official JavaScript SDK subscriptions used the supported fetch adapter with User-Agent `terrascale-js/0.0.19`. The snapshot counts below describe when each subscription completed; the Free GRU snapshot was earlier than the final inventory and is not the exact-ID reconciliation. Pro snapshots independently corroborate the missing-row count after the move.

`shapes.json`:

```json
[
  {
    "kind": "shape_result",
    "region": "gru",
    "ok": true,
    "rows": 929,
    "provisional": 0,
    "offset": "3660"
  },
  {
    "kind": "shape_result",
    "region": "sjc",
    "ok": true,
    "rows": 963,
    "provisional": 0,
    "offset": "3786"
  },
  {
    "kind": "shape_result",
    "region": "fra",
    "ok": true,
    "rows": 963,
    "provisional": 0,
    "offset": "3786"
  }
]
```

`pro-shapes.json`:

```json
[
  {
    "kind": "shape_result",
    "region": "gru",
    "ok": true,
    "rows": 1926,
    "provisional": 0,
    "offset": "0"
  },
  {
    "kind": "shape_result",
    "region": "sjc",
    "ok": true,
    "rows": 1926,
    "provisional": 0,
    "offset": "0"
  },
  {
    "kind": "shape_result",
    "region": "fra",
    "ok": true,
    "rows": 1926,
    "provisional": 0,
    "offset": "0"
  }
]
```

Regional public CDN cache requests (same sealed object, same user agent):

```json
[
  {
    "region": "gru",
    "host": "ts-gru-1",
    "results": [
      {
        "n": 0,
        "status": 200,
        "ms": 217.476,
        "bytes": 112046,
        "sha256": "4aec2cd346945d5a5799160029e12f26b94fdedbbb931c13fe033b9aa9bb0c26",
        "headers": {
          "cf-cache-status": "HIT",
          "CF-RAY": "a55de819adcac377-GRU"
        }
      },
      {
        "n": 1,
        "status": 200,
        "ms": 37.127,
        "bytes": 112046,
        "sha256": "4aec2cd346945d5a5799160029e12f26b94fdedbbb931c13fe033b9aa9bb0c26",
        "headers": {
          "Age": "360",
          "cf-cache-status": "HIT",
          "CF-RAY": "a55de81acb6bc250-GRU"
        }
      },
      {
        "n": 2,
        "status": 200,
        "ms": 49.479,
        "bytes": 112046,
        "sha256": "4aec2cd346945d5a5799160029e12f26b94fdedbbb931c13fe033b9aa9bb0c26",
        "headers": {
          "Age": "360",
          "cf-cache-status": "HIT",
          "CF-RAY": "a55de81b0b451acb-GRU"
        }
      }
    ]
  },
  {
    "region": "sjc",
    "host": "ts-sjc-1",
    "results": [
      {
        "n": 0,
        "status": 200,
        "ms": 189.878,
        "bytes": 112046,
        "sha256": "4aec2cd346945d5a5799160029e12f26b94fdedbbb931c13fe033b9aa9bb0c26",
        "headers": {
          "cf-cache-status": "HIT",
          "CF-RAY": "a55de8260eecdf82-SJC"
        }
      },
      {
        "n": 1,
        "status": 200,
        "ms": 58.85,
        "bytes": 112046,
        "sha256": "4aec2cd346945d5a5799160029e12f26b94fdedbbb931c13fe033b9aa9bb0c26",
        "headers": {
          "Age": "362",
          "cf-cache-status": "HIT",
          "CF-RAY": "a55de826ed47f31f-SJC"
        }
      },
      {
        "n": 2,
        "status": 200,
        "ms": 51.725,
        "bytes": 112046,
        "sha256": "4aec2cd346945d5a5799160029e12f26b94fdedbbb931c13fe033b9aa9bb0c26",
        "headers": {
          "Age": "362",
          "cf-cache-status": "HIT",
          "CF-RAY": "a55de8274dfedc9b-SJC"
        }
      }
    ]
  },
  {
    "region": "fra",
    "host": "ts-fra-1",
    "results": [
      {
        "n": 0,
        "status": 200,
        "ms": 217.593,
        "bytes": 112046,
        "sha256": "4aec2cd346945d5a5799160029e12f26b94fdedbbb931c13fe033b9aa9bb0c26",
        "headers": {
          "cf-cache-status": "HIT",
          "CF-RAY": "a55de8270818d2be-FRA"
        }
      },
      {
        "n": 1,
        "status": 200,
        "ms": 66.151,
        "bytes": 112046,
        "sha256": "4aec2cd346945d5a5799160029e12f26b94fdedbbb931c13fe033b9aa9bb0c26",
        "headers": {
          "Age": "362",
          "cf-cache-status": "HIT",
          "CF-RAY": "a55de8282b62d350-FRA"
        }
      },
      {
        "n": 2,
        "status": 200,
        "ms": 71.583,
        "bytes": 112046,
        "sha256": "4aec2cd346945d5a5799160029e12f26b94fdedbbb931c13fe033b9aa9bb0c26",
        "headers": {
          "Age": "362",
          "cf-cache-status": "HIT",
          "CF-RAY": "a55de8289db49763-FRA"
        }
      }
    ]
  }
]
```

## Original database deletion and key denial

Prior successful key-release requests are repeated after deletion; only status/code and response headers are recorded. Key response values are never included.

```json
[
  {
    "ok": true,
    "rows": [],
    "kind": "reconcile-psql-timeout",
    "region": "fra",
    "ms": 838.401,
    "at": "2026-11-05T18:33:39Z",
    "headers": {
      "server-timing": "admission;dur=0.10, auth;dur=192.96, home_lookup;dur=3.66, qos;dur=1.26, project;dur=0.03, placement;dur=0.01, tenant;dur=1.69, execute;dur=0.59, app;dur=200.31;desc=\"fra\"",
      "x-request-id": "01M6PFSV526HF46FQPGF5XY6ZZ",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    }
  },
  {
    "ok": true,
    "status": 200,
    "kind": "key-before-delete",
    "region": "gru",
    "ms": 276.651,
    "at": "2026-11-05T18:33:39Z",
    "headers": {
      "server-timing": "home;dur=194.2;desc=\"fra\"",
      "x-request-id": "01M6PFSVGF0SG446X7EASNKTH2",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    }
  },
  {
    "ok": true,
    "status": 200,
    "kind": "key-before-delete",
    "region": "sjc",
    "ms": 1126.124,
    "at": "2026-11-05T18:33:40Z",
    "headers": {
      "server-timing": "home;dur=351.8;desc=\"fra\"",
      "x-request-id": "01M6PFSW6EY0VFJ7YT8XZ4V9HB",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    }
  },
  {
    "ok": true,
    "status": 200,
    "kind": "key-before-delete",
    "region": "fra",
    "ms": 213.611,
    "at": "2026-11-05T18:33:41Z",
    "headers": {
      "x-request-id": "01M6PFSWXT6NKPMFEDQG9MEEP6",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    }
  },
  {
    "ok": true,
    "kind": "delete-database",
    "region": "fra",
    "ms": 3080.026,
    "at": "2026-11-05T18:33:44Z",
    "headers": {
      "x-request-id": "01M6PFSX4GBNX4KZ8RADRN7HWW",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    }
  },
  {
    "ok": false,
    "status": 404,
    "code": "unknown_database",
    "type": "NotFoundError",
    "kind": "sql-after-delete",
    "region": "gru",
    "ms": 226.377,
    "at": "2026-11-05T18:33:44Z",
    "headers": {
      "server-timing": "admission;dur=0.13, auth;dur=8.81, home_lookup;dur=192.54, qos;dur=1.72, project;dur=0.03, placement;dur=0.03, execute;dur=1.15, app;dur=204.41;desc=\"gru\"",
      "x-request-id": "01M6PFT01SRJT1TXVP5V87TG6T",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "ok": false,
    "status": 404,
    "code": "unknown_database",
    "type": "NotFoundError",
    "kind": "database-after-delete",
    "region": "gru",
    "ms": 212.156,
    "at": "2026-11-05T18:33:44Z",
    "headers": {
      "x-request-id": "01M6PFT08VTBY0VV66VBY3FVAS",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "ok": true,
    "status": 404,
    "code": "unknown_database",
    "kind": "key-after-delete",
    "region": "gru",
    "ms": 216.038,
    "at": "2026-11-05T18:33:44Z",
    "headers": {
      "x-request-id": "01M6PFT0FFEWSRS83C12BH7P3Y",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "ok": false,
    "status": 404,
    "code": "unknown_database",
    "type": "NotFoundError",
    "kind": "sql-after-delete",
    "region": "sjc",
    "ms": 580.35,
    "at": "2026-11-05T18:33:45Z",
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=194.45, home_lookup;dur=190.12, qos;dur=2.02, project;dur=0.02, placement;dur=0.01, execute;dur=1.09, app;dur=387.84;desc=\"sjc\"",
      "x-request-id": "01M6PFT0RZJER4EW1M6QJCVZYV",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    }
  },
  {
    "ok": false,
    "status": 404,
    "code": "unknown_database",
    "type": "NotFoundError",
    "kind": "database-after-delete",
    "region": "sjc",
    "ms": 382.912,
    "at": "2026-11-05T18:33:45Z",
    "headers": {
      "x-request-id": "01M6PFT1B3ERKWD9W655N8KCF3",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    }
  },
  {
    "ok": true,
    "status": 404,
    "code": "unknown_database",
    "kind": "key-after-delete",
    "region": "sjc",
    "ms": 387.71,
    "at": "2026-11-05T18:33:46Z",
    "headers": {
      "x-request-id": "01M6PFT1Q1YC06JHBPAG074JFB",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    }
  },
  {
    "ok": false,
    "status": 404,
    "code": "unknown_database",
    "type": "NotFoundError",
    "kind": "sql-after-delete",
    "region": "fra",
    "ms": 599.119,
    "at": "2026-11-05T18:33:46Z",
    "headers": {
      "server-timing": "admission;dur=0.11, auth;dur=193.24, home_lookup;dur=190.40, qos;dur=1.83, project;dur=0.03, placement;dur=0.01, execute;dur=1.38, app;dur=387.01;desc=\"fra\"",
      "x-request-id": "01M6PFT23FGGQQDH03HXX9A7RH",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    }
  },
  {
    "ok": false,
    "status": 404,
    "code": "unknown_database",
    "type": "NotFoundError",
    "kind": "database-after-delete",
    "region": "fra",
    "ms": 402.578,
    "at": "2026-11-05T18:33:47Z",
    "headers": {
      "x-request-id": "01M6PFT2P51DZTP8J77JAZAJXK",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    }
  },
  {
    "ok": true,
    "status": 404,
    "code": "unknown_database",
    "kind": "key-after-delete",
    "region": "fra",
    "ms": 403.327,
    "at": "2026-11-05T18:33:47Z",
    "headers": {
      "x-request-id": "01M6PFT32RMT635K0NQ6F6SR3P",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    }
  }
]
```

Initial CDN observation, inside the 420-second allowance (check complete by 18:34:33.168464Z, at most 49.168464 s after deletion):

```json
[
  {
    "region": "gru",
    "results": [
      {
        "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-f1f3db08a628b1dd/006eeca090c2d73853681ebf36f29cc59a833c763d1a276965fd980d4c7dc2c2",
        "status": 200,
        "ms": 81.688,
        "bytes": 112046,
        "headers": {
          "Age": "846",
          "cf-cache-status": "HIT",
          "CF-RAY": "a55df3f66d6a1b12-GRU"
        }
      },
      {
        "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-f379f47f1be80432/9cb35d40547def5ac02bf5739637daa03b3becb22533a21a6dd865e7d38dea2a",
        "status": 200,
        "ms": 195.355,
        "bytes": 113146,
        "headers": {
          "cf-cache-status": "HIT",
          "CF-RAY": "a55df3f6ab1975cf-GRU"
        }
      }
    ]
  },
  {
    "region": "sjc",
    "results": [
      {
        "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-f1f3db08a628b1dd/006eeca090c2d73853681ebf36f29cc59a833c763d1a276965fd980d4c7dc2c2",
        "status": 200,
        "ms": 100.695,
        "bytes": 112046,
        "headers": {
          "Age": "848",
          "cf-cache-status": "HIT",
          "CF-RAY": "a55df4023bbcebe7-SJC"
        }
      },
      {
        "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-f379f47f1be80432/9cb35d40547def5ac02bf5739637daa03b3becb22533a21a6dd865e7d38dea2a",
        "status": 200,
        "ms": 131.16,
        "bytes": 113146,
        "headers": {
          "cf-cache-status": "HIT",
          "CF-RAY": "a55df402985ac12c-SJC"
        }
      }
    ]
  },
  {
    "region": "fra",
    "results": [
      {
        "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-f1f3db08a628b1dd/006eeca090c2d73853681ebf36f29cc59a833c763d1a276965fd980d4c7dc2c2",
        "status": 200,
        "ms": 113.624,
        "bytes": 112046,
        "headers": {
          "Age": "848",
          "cf-cache-status": "HIT",
          "CF-RAY": "a55df4033ed2dcb9-FRA"
        }
      },
      {
        "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-f379f47f1be80432/9cb35d40547def5ac02bf5739637daa03b3becb22533a21a6dd865e7d38dea2a",
        "status": 200,
        "ms": 154.519,
        "bytes": 113146,
        "headers": {
          "cf-cache-status": "HIT",
          "CF-RAY": "a55df403ad553656-FRA"
        }
      }
    ]
  }
]
```

Final timed recheck: **21/21 HTTP 200 / HIT after 527–530 seconds**, which fails the 420-second purge deadline (#1107). All returned bytes are ciphertext; this does not establish plaintext access.

```json
{
  "deleted_at": "2026-11-05T18:33:44Z",
  "coordinator_check_started_at": "2026-11-05T18:42:30.071786+00:00",
  "coordinator_elapsed_seconds": 526.072,
  "url_count": 7,
  "regions": [
    {
      "region": "gru",
      "results": [
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-f1f3db08a628b1dd/006eeca090c2d73853681ebf36f29cc59a833c763d1a276965fd980d4c7dc2c2",
          "status": 200,
          "started_at": "2026-11-05T18:42:31.137470+00:00",
          "completed_at": "2026-11-05T18:42:31.220413+00:00",
          "elapsed_since_delete_s": 527.137,
          "request_ms": 83.01,
          "bytes": 112046,
          "headers": {
            "age": "1326",
            "cf-cache-status": "HIT",
            "cf-ray": "a55dffb0ed050107-GRU",
            "date": "Thu, 05 Nov 2026 18:42:31 GMT"
          }
        },
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-f1f3db08a628b1dd/18278ae206dc20d3abbe78b22ffae19895663f6ea14a95bd1b80b8e18f2b733c",
          "status": 200,
          "started_at": "2026-11-05T18:42:31.220499+00:00",
          "completed_at": "2026-11-05T18:42:31.433273+00:00",
          "elapsed_since_delete_s": 527.22,
          "request_ms": 212.827,
          "bytes": 444,
          "headers": {
            "age": null,
            "cf-cache-status": "HIT",
            "cf-ray": "a55dffb13c3b721c-GRU",
            "date": "Thu, 05 Nov 2026 18:42:31 GMT"
          }
        },
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-f1f3db08a628b1dd/24bf1bff8ae9ad50c21a944b55a08c6eaa977708a10293b8270bea6b0dbfbb35",
          "status": 200,
          "started_at": "2026-11-05T18:42:31.433339+00:00",
          "completed_at": "2026-11-05T18:42:31.609919+00:00",
          "elapsed_since_delete_s": 527.433,
          "request_ms": 176.623,
          "bytes": 444,
          "headers": {
            "age": null,
            "cf-cache-status": "HIT",
            "cf-ray": "a55dffb29fc4b92f-GRU",
            "date": "Thu, 05 Nov 2026 18:42:31 GMT"
          }
        },
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-f1f3db08a628b1dd/677fcee96bbfb843cc31b90b2741d0356d165958a4e78699483fe91324370bb4",
          "status": 200,
          "started_at": "2026-11-05T18:42:31.609972+00:00",
          "completed_at": "2026-11-05T18:42:31.821446+00:00",
          "elapsed_since_delete_s": 527.61,
          "request_ms": 211.512,
          "bytes": 444,
          "headers": {
            "age": null,
            "cf-cache-status": "HIT",
            "cf-ray": "a55dffb3afae2bf9-GRU",
            "date": "Thu, 05 Nov 2026 18:42:31 GMT"
          }
        },
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-f1f3db08a628b1dd/93497c359a18eceaf0e6e5ea2dc35e9833ae2f4ccadfd7b301cb79e1d308d4b8",
          "status": 200,
          "started_at": "2026-11-05T18:42:31.821494+00:00",
          "completed_at": "2026-11-05T18:42:32.016122+00:00",
          "elapsed_since_delete_s": 527.821,
          "request_ms": 194.682,
          "bytes": 444,
          "headers": {
            "age": null,
            "cf-cache-status": "HIT",
            "cf-ray": "a55dffb4ff788bdd-GRU",
            "date": "Thu, 05 Nov 2026 18:42:32 GMT"
          }
        },
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-f379f47f1be80432/9cb35d40547def5ac02bf5739637daa03b3becb22533a21a6dd865e7d38dea2a",
          "status": 200,
          "started_at": "2026-11-05T18:42:32.016189+00:00",
          "completed_at": "2026-11-05T18:42:32.072359+00:00",
          "elapsed_since_delete_s": 528.016,
          "request_ms": 56.224,
          "bytes": 113146,
          "headers": {
            "age": "782",
            "cf-cache-status": "HIT",
            "cf-ray": "a55dffb63849af68-GRU",
            "date": "Thu, 05 Nov 2026 18:42:32 GMT"
          }
        },
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-f379f47f1be80432/0c7508a975e1c74c1b8361ee407c24f5d75aaeb6bf93cfed02a35241333e96ec",
          "status": 200,
          "started_at": "2026-11-05T18:42:32.072426+00:00",
          "completed_at": "2026-11-05T18:42:32.256292+00:00",
          "elapsed_since_delete_s": 528.072,
          "request_ms": 183.917,
          "bytes": 121059,
          "headers": {
            "age": null,
            "cf-cache-status": "HIT",
            "cf-ray": "a55dffb6899ff2e4-GRU",
            "date": "Thu, 05 Nov 2026 18:42:32 GMT"
          }
        }
      ]
    },
    {
      "region": "sjc",
      "results": [
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-f1f3db08a628b1dd/006eeca090c2d73853681ebf36f29cc59a833c763d1a276965fd980d4c7dc2c2",
          "status": 200,
          "started_at": "2026-11-05T18:42:33.008913+00:00",
          "completed_at": "2026-11-05T18:42:33.216069+00:00",
          "elapsed_since_delete_s": 529.009,
          "request_ms": 207.203,
          "bytes": 112046,
          "headers": {
            "age": "1328",
            "cf-cache-status": "HIT",
            "cf-ray": "a55dffbcac62ed38-SJC",
            "date": "Thu, 05 Nov 2026 18:42:33 GMT"
          }
        },
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-f1f3db08a628b1dd/18278ae206dc20d3abbe78b22ffae19895663f6ea14a95bd1b80b8e18f2b733c",
          "status": 200,
          "started_at": "2026-11-05T18:42:33.216137+00:00",
          "completed_at": "2026-11-05T18:42:33.345494+00:00",
          "elapsed_since_delete_s": 529.216,
          "request_ms": 129.399,
          "bytes": 444,
          "headers": {
            "age": null,
            "cf-cache-status": "HIT",
            "cf-ray": "a55dffbdb9cca2db-SJC",
            "date": "Thu, 05 Nov 2026 18:42:33 GMT"
          }
        },
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-f1f3db08a628b1dd/24bf1bff8ae9ad50c21a944b55a08c6eaa977708a10293b8270bea6b0dbfbb35",
          "status": 200,
          "started_at": "2026-11-05T18:42:33.345548+00:00",
          "completed_at": "2026-11-05T18:42:33.475925+00:00",
          "elapsed_since_delete_s": 529.346,
          "request_ms": 130.417,
          "bytes": 444,
          "headers": {
            "age": null,
            "cf-cache-status": "HIT",
            "cf-ray": "a55dffbe7ac560d0-SJC",
            "date": "Thu, 05 Nov 2026 18:42:33 GMT"
          }
        },
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-f1f3db08a628b1dd/677fcee96bbfb843cc31b90b2741d0356d165958a4e78699483fe91324370bb4",
          "status": 200,
          "started_at": "2026-11-05T18:42:33.475975+00:00",
          "completed_at": "2026-11-05T18:42:33.705798+00:00",
          "elapsed_since_delete_s": 529.476,
          "request_ms": 229.863,
          "bytes": 444,
          "headers": {
            "age": null,
            "cf-cache-status": "HIT",
            "cf-ray": "a55dffbf5b096798-SJC",
            "date": "Thu, 05 Nov 2026 18:42:33 GMT"
          }
        },
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-f1f3db08a628b1dd/93497c359a18eceaf0e6e5ea2dc35e9833ae2f4ccadfd7b301cb79e1d308d4b8",
          "status": 200,
          "started_at": "2026-11-05T18:42:33.705849+00:00",
          "completed_at": "2026-11-05T18:42:33.857731+00:00",
          "elapsed_since_delete_s": 529.706,
          "request_ms": 151.921,
          "bytes": 444,
          "headers": {
            "age": null,
            "cf-cache-status": "HIT",
            "cf-ray": "a55dffc0cee22523-SJC",
            "date": "Thu, 05 Nov 2026 18:42:33 GMT"
          }
        },
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-f379f47f1be80432/9cb35d40547def5ac02bf5739637daa03b3becb22533a21a6dd865e7d38dea2a",
          "status": 200,
          "started_at": "2026-11-05T18:42:33.857782+00:00",
          "completed_at": "2026-11-05T18:42:33.929599+00:00",
          "elapsed_since_delete_s": 529.858,
          "request_ms": 71.862,
          "bytes": 113146,
          "headers": {
            "age": "784",
            "cf-cache-status": "HIT",
            "cf-ray": "a55dffc1bd1967ad-SJC",
            "date": "Thu, 05 Nov 2026 18:42:33 GMT"
          }
        },
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-f379f47f1be80432/0c7508a975e1c74c1b8361ee407c24f5d75aaeb6bf93cfed02a35241333e96ec",
          "status": 200,
          "started_at": "2026-11-05T18:42:33.929655+00:00",
          "completed_at": "2026-11-05T18:42:34.096712+00:00",
          "elapsed_since_delete_s": 529.93,
          "request_ms": 167.114,
          "bytes": 121059,
          "headers": {
            "age": null,
            "cf-cache-status": "HIT",
            "cf-ray": "a55dffc22d74303f-SJC",
            "date": "Thu, 05 Nov 2026 18:42:34 GMT"
          }
        }
      ]
    },
    {
      "region": "fra",
      "results": [
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-f1f3db08a628b1dd/006eeca090c2d73853681ebf36f29cc59a833c763d1a276965fd980d4c7dc2c2",
          "status": 200,
          "started_at": "2026-11-05T18:42:32.831309+00:00",
          "completed_at": "2026-11-05T18:42:32.928521+00:00",
          "elapsed_since_delete_s": 528.831,
          "request_ms": 97.247,
          "bytes": 112046,
          "headers": {
            "age": "1328",
            "cf-cache-status": "HIT",
            "cf-ray": "a55dffbb8e1836e0-FRA",
            "date": "Thu, 05 Nov 2026 18:42:32 GMT"
          }
        },
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-f1f3db08a628b1dd/18278ae206dc20d3abbe78b22ffae19895663f6ea14a95bd1b80b8e18f2b733c",
          "status": 200,
          "started_at": "2026-11-05T18:42:32.928575+00:00",
          "completed_at": "2026-11-05T18:42:32.979485+00:00",
          "elapsed_since_delete_s": 528.929,
          "request_ms": 50.947,
          "bytes": 444,
          "headers": {
            "age": "1327",
            "cf-cache-status": "HIT",
            "cf-ray": "a55dffbbea9ad286-FRA",
            "date": "Thu, 05 Nov 2026 18:42:32 GMT"
          }
        },
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-f1f3db08a628b1dd/24bf1bff8ae9ad50c21a944b55a08c6eaa977708a10293b8270bea6b0dbfbb35",
          "status": 200,
          "started_at": "2026-11-05T18:42:32.979533+00:00",
          "completed_at": "2026-11-05T18:42:33.134545+00:00",
          "elapsed_since_delete_s": 528.98,
          "request_ms": 155.049,
          "bytes": 444,
          "headers": {
            "age": null,
            "cf-cache-status": "HIT",
            "cf-ray": "a55dffbc3db62eec-FRA",
            "date": "Thu, 05 Nov 2026 18:42:33 GMT"
          }
        },
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-f1f3db08a628b1dd/677fcee96bbfb843cc31b90b2741d0356d165958a4e78699483fe91324370bb4",
          "status": 200,
          "started_at": "2026-11-05T18:42:33.134592+00:00",
          "completed_at": "2026-11-05T18:42:33.288094+00:00",
          "elapsed_since_delete_s": 529.135,
          "request_ms": 153.538,
          "bytes": 444,
          "headers": {
            "age": null,
            "cf-cache-status": "HIT",
            "cf-ray": "a55dffbd2b46d2f2-FRA",
            "date": "Thu, 05 Nov 2026 18:42:33 GMT"
          }
        },
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-f1f3db08a628b1dd/93497c359a18eceaf0e6e5ea2dc35e9833ae2f4ccadfd7b301cb79e1d308d4b8",
          "status": 200,
          "started_at": "2026-11-05T18:42:33.288140+00:00",
          "completed_at": "2026-11-05T18:42:33.354025+00:00",
          "elapsed_since_delete_s": 529.288,
          "request_ms": 65.923,
          "bytes": 444,
          "headers": {
            "age": "1326",
            "cf-cache-status": "HIT",
            "cf-ray": "a55dffbe3999d212-FRA",
            "date": "Thu, 05 Nov 2026 18:42:33 GMT"
          }
        },
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-f379f47f1be80432/9cb35d40547def5ac02bf5739637daa03b3becb22533a21a6dd865e7d38dea2a",
          "status": 200,
          "started_at": "2026-11-05T18:42:33.354075+00:00",
          "completed_at": "2026-11-05T18:42:33.406198+00:00",
          "elapsed_since_delete_s": 529.354,
          "request_ms": 52.16,
          "bytes": 113146,
          "headers": {
            "age": "783",
            "cf-cache-status": "HIT",
            "cf-ray": "a55dffbe9e2164b2-FRA",
            "date": "Thu, 05 Nov 2026 18:42:33 GMT"
          }
        },
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-f379f47f1be80432/0c7508a975e1c74c1b8361ee407c24f5d75aaeb6bf93cfed02a35241333e96ec",
          "status": 200,
          "started_at": "2026-11-05T18:42:33.406245+00:00",
          "completed_at": "2026-11-05T18:42:33.490576+00:00",
          "elapsed_since_delete_s": 529.406,
          "request_ms": 84.395,
          "bytes": 121059,
          "headers": {
            "age": "780",
            "cf-cache-status": "HIT",
            "cf-ray": "a55dffbeffef5d8e-FRA",
            "date": "Thu, 05 Nov 2026 18:42:33 GMT"
          }
        }
      ]
    }
  ]
}
```

## Independent post-fix run during rollout

Client delta, SDK ddbc3a87d; the path baseline follows writes in this retained run. Full write/read samples, including every failure, are in the samples document. Network-baseline estimates use successful SELECT1 responses with origin timing; write latency percentiles include all attempts. This run is not silently substituted for the requested baseline-first confirmation.

```json
[
  {
    "kind": "environment",
    "host": "delta",
    "sdk_revision": "ddbc3a87d7c16cedf918cb3754bad364f7c068eb",
    "at": "2026-11-05T19:33:23Z"
  },
  {
    "kind": "postfix_database_created",
    "at": "2026-11-05T19:33:24.371687+00:00",
    "value": {
      "id": "01M6PK77TX4H0B1D3GQZ73XZCF",
      "project_id": "01M6PENV06CRPSJYDDRZTF75R4",
      "name": "milestone-b-postfix",
      "created_at": "2026-11-05T19:33:23.9337227+00:00",
      "region": "gru",
      "home_region": "gru",
      "branched_from": null,
      "raw": {
        "id": "01M6PK77TX4H0B1D3GQZ73XZCF",
        "projectId": "01M6PENV06CRPSJYDDRZTF75R4",
        "name": "milestone-b-postfix",
        "branchedFrom": null,
        "createdAt": "2026-11-05T19:33:23.9337227+00:00",
        "region": "gru",
        "homeRegion": "gru",
        "replicaRegions": [
          "fra",
          "sjc"
        ],
        "allRegions": true,
        "previousName": null,
        "conflictOf": null,
        "expiresAt": null
      },
      "previous_name": null,
      "conflict_of": null,
      "expires_at": null,
      "replica_regions": [
        "fra",
        "sjc"
      ],
      "all_regions": true
    }
  },
  {
    "kind": "exact_comparison",
    "region": "gru",
    "expected": 958,
    "rows": 958,
    "distinct": 958,
    "missing": [],
    "unexpected": [],
    "changed": []
  },
  {
    "kind": "exact_comparison",
    "region": "sjc",
    "expected": 958,
    "rows": 958,
    "distinct": 958,
    "missing": [],
    "unexpected": [],
    "changed": []
  },
  {
    "kind": "exact_comparison",
    "region": "fra",
    "expected": 958,
    "rows": 958,
    "distinct": 958,
    "missing": [],
    "unexpected": [],
    "changed": []
  }
]
```

All-attempt write percentile results:

```json
[
  {
    "region": "gru",
    "repeat": 1,
    "server_pass": true,
    "client_pass": true,
    "server_p99": 55.92,
    "client_p99": 77.578,
    "baseline": 21.54
  },
  {
    "region": "gru",
    "repeat": 2,
    "server_pass": true,
    "client_pass": true,
    "server_p99": 79.31,
    "client_p99": 102.271,
    "baseline": 21.54
  },
  {
    "region": "gru",
    "repeat": 3,
    "server_pass": true,
    "client_pass": true,
    "server_p99": 34.09,
    "client_p99": 57.661,
    "baseline": 21.54
  },
  {
    "region": "gru",
    "repeat": null,
    "server_pass": true,
    "client_pass": true,
    "server_p99": 55.92,
    "client_p99": 92.188,
    "baseline": 21.54
  },
  {
    "region": "sjc",
    "repeat": 1,
    "server_pass": true,
    "client_pass": true,
    "server_p99": 44.24,
    "client_p99": 237.259,
    "baseline": 195.447
  },
  {
    "region": "sjc",
    "repeat": 2,
    "server_pass": false,
    "client_pass": false,
    "server_p99": 881.77,
    "client_p99": 2211.595,
    "baseline": 195.447
  },
  {
    "region": "sjc",
    "repeat": 3,
    "server_pass": false,
    "client_pass": false,
    "server_p99": 218.11,
    "client_p99": 816.145,
    "baseline": 195.447
  },
  {
    "region": "sjc",
    "repeat": null,
    "server_pass": false,
    "client_pass": false,
    "server_p99": 726.52,
    "client_p99": 1077.745,
    "baseline": 195.447
  },
  {
    "region": "fra",
    "repeat": 1,
    "server_pass": true,
    "client_pass": true,
    "server_p99": 53.97,
    "client_p99": 267.299,
    "baseline": 211.806
  },
  {
    "region": "fra",
    "repeat": 2,
    "server_pass": false,
    "client_pass": false,
    "server_p99": 293.02,
    "client_p99": 877.049,
    "baseline": 211.806
  },
  {
    "region": "fra",
    "repeat": 3,
    "server_pass": false,
    "client_pass": false,
    "server_p99": 749.63,
    "client_p99": 1928.428,
    "baseline": 211.806
  },
  {
    "region": "fra",
    "repeat": null,
    "server_pass": false,
    "client_pass": false,
    "server_p99": 620.04,
    "client_p99": 968.217,
    "baseline": 211.806
  }
]
```

Every failed public API call (original messages were not retained):

```json
[
  {
    "started_at": "2026-11-05T19:34:36.760149+00:00",
    "completed_at": "2026-11-05T19:34:55.045867+00:00",
    "kind": "cross_read",
    "region": "sjc",
    "ok": false,
    "ms": 18285.706,
    "response_received_at": "2026-11-05T19:34:55.044961+00:00",
    "status": 400,
    "headers": {
      "server-timing": "admission;dur=0.70, auth;dur=204.15, home_lookup;dur=7.65, qos;dur=22.64, project;dur=0.05, placement;dur=2.10, tenant;dur=31.45, execute;dur=352.64, app;dur=621.50;desc=\"sjc\"",
      "x-request-id": "01M6PK9F21PT0F2BYT8EPJ90ZR",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "source": "gru",
    "repeat": 2,
    "n": 45,
    "id": "01M6PK9EXKEGAYBJNMP2V06M0V",
    "error": {
      "type": "SqlError",
      "status": 400,
      "code": "sql_error",
      "request_id": "01M6PK9F21PT0F2BYT8EPJ90ZR"
    }
  },
  {
    "started_at": "2026-11-05T19:34:36.609964+00:00",
    "completed_at": "2026-11-05T19:34:55.049019+00:00",
    "kind": "write",
    "region": "sjc",
    "ok": false,
    "ms": 18439.042,
    "response_received_at": "2026-11-05T19:34:55.048168+00:00",
    "status": 400,
    "headers": {
      "server-timing": "admission;dur=0.21, auth;dur=193.99, home_lookup;dur=1.53, qos;dur=22.43, project;dur=0.12, placement;dur=0.07, tenant;dur=34.26, execute;dur=354.70, app;dur=607.32;desc=\"sjc\"",
      "x-request-id": "01M6PK9EXBFA53ZYT0WSMFG6ZR",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "repeat": 2,
    "n": 41,
    "id": "01M6PK9ET12KERWG5JHFS0S9ZE",
    "error": {
      "type": "SqlError",
      "status": 400,
      "code": "sql_error",
      "request_id": "01M6PK9EXBFA53ZYT0WSMFG6ZR"
    }
  },
  {
    "started_at": "2026-11-05T19:34:36.768559+00:00",
    "completed_at": "2026-11-05T19:34:55.052035+00:00",
    "kind": "cross_read",
    "region": "sjc",
    "ok": false,
    "ms": 18283.467,
    "response_received_at": "2026-11-05T19:34:55.051459+00:00",
    "status": 400,
    "headers": {
      "server-timing": "admission;dur=2.28, auth;dur=200.12, home_lookup;dur=8.16, qos;dur=15.54, project;dur=1.08, placement;dur=0.43, tenant;dur=27.82, execute;dur=377.25, app;dur=632.71;desc=\"sjc\"",
      "x-request-id": "01M6PK9F2B40C0GSWW1XCB14PT",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "source": "fra",
    "repeat": 2,
    "n": 42,
    "id": "01M6PK9EPV9ZR7MHCMQ0A0Q5NB",
    "error": {
      "type": "SqlError",
      "status": 400,
      "code": "sql_error",
      "request_id": "01M6PK9F2B40C0GSWW1XCB14PT"
    }
  },
  {
    "started_at": "2026-11-05T19:35:42.747609+00:00",
    "completed_at": "2026-11-05T19:35:56.083705+00:00",
    "kind": "write",
    "region": "fra",
    "ok": false,
    "ms": 13336.083,
    "response_received_at": "2026-11-05T19:35:56.081608+00:00",
    "status": 400,
    "headers": {
      "server-timing": "admission;dur=4.50, auth;dur=210.01, home_lookup;dur=12.28, qos;dur=17.04, project;dur=0.08, placement;dur=0.05, tenant;dur=31.60, execute;dur=186.35, app;dur=462.11;desc=\"fra\"",
      "x-request-id": "01M6PKBFGAVM92XCMJFJSQQ4VZ",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "repeat": 3,
    "n": 30,
    "id": "01M6PKBFCVQB0V6VWPER7DPAEG",
    "error": {
      "type": "SqlError",
      "status": 400,
      "code": "sql_error",
      "request_id": "01M6PKBFGAVM92XCMJFJSQQ4VZ"
    }
  },
  {
    "started_at": "2026-11-05T19:35:42.762491+00:00",
    "completed_at": "2026-11-05T19:35:56.086794+00:00",
    "kind": "cross_read",
    "region": "fra",
    "ok": false,
    "ms": 13324.294,
    "response_received_at": "2026-11-05T19:35:56.083192+00:00",
    "status": 400,
    "headers": {
      "server-timing": "admission;dur=0.29, auth;dur=199.96, home_lookup;dur=14.83, qos;dur=11.22, project;dur=1.08, placement;dur=0.25, tenant;dur=26.62, execute;dur=197.76, app;dur=452.22;desc=\"fra\"",
      "x-request-id": "01M6PKBFGTNNPXW9N6EPKP19ZA",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "source": "sjc",
    "repeat": 3,
    "n": 16,
    "id": "01M6PKBF5YDXT83P4XHV6QXFM9",
    "error": {
      "type": "SqlError",
      "status": 400,
      "code": "sql_error",
      "request_id": "01M6PKBFGTNNPXW9N6EPKP19ZA"
    }
  },
  {
    "started_at": "2026-11-05T19:35:42.711443+00:00",
    "completed_at": "2026-11-05T19:35:56.088590+00:00",
    "kind": "cross_read",
    "region": "fra",
    "ok": false,
    "ms": 13377.136,
    "response_received_at": "2026-11-05T19:35:56.084733+00:00",
    "status": 400,
    "headers": {
      "server-timing": "admission;dur=0.81, auth;dur=198.16, home_lookup;dur=9.11, qos;dur=19.98, project;dur=0.05, placement;dur=0.03, tenant;dur=30.25, execute;dur=191.19, app;dur=449.59;desc=\"fra\"",
      "x-request-id": "01M6PKBFF70QW51PY72BZ0R20A",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "source": "gru",
    "repeat": 3,
    "n": 33,
    "id": "01M6PKBF4FE43CFNTMMXFTTAAD",
    "error": {
      "type": "SqlError",
      "status": 400,
      "code": "sql_error",
      "request_id": "01M6PKBFF70QW51PY72BZ0R20A"
    }
  },
  {
    "started_at": "2026-11-05T19:37:01.984214+00:00",
    "completed_at": "2026-11-05T19:37:02.013277+00:00",
    "kind": "baseline_path",
    "region": "gru",
    "ok": false,
    "ms": 29.054,
    "response_received_at": "2026-11-05T19:37:02.012902+00:00",
    "status": 502,
    "headers": {
      "x-request-id": "01M6PKDWSHGN5KP9Q5J20FEWZ2"
    },
    "n": 59,
    "error": {
      "type": "CommitUncertainError",
      "status": 502,
      "code": "http_502",
      "request_id": "01M6PKDWSHGN5KP9Q5J20FEWZ2"
    }
  },
  {
    "started_at": "2026-11-05T19:37:02.013382+00:00",
    "completed_at": "2026-11-05T19:37:02.149950+00:00",
    "kind": "baseline_path",
    "region": "gru",
    "ok": false,
    "ms": 136.559,
    "response_received_at": "2026-11-05T19:37:02.149535+00:00",
    "status": 502,
    "headers": {
      "x-request-id": "01M6PKDWTEZ8WCTZTA65HF55CF"
    },
    "n": 60,
    "error": {
      "type": "CommitUncertainError",
      "status": 502,
      "code": "http_502",
      "request_id": "01M6PKDWTEZ8WCTZTA65HF55CF"
    }
  },
  {
    "started_at": "2026-11-05T19:37:02.594721+00:00",
    "completed_at": "2026-11-05T19:37:06.199957+00:00",
    "kind": "baseline_path",
    "region": "sjc",
    "ok": false,
    "ms": 3605.226,
    "response_received_at": "2026-11-05T19:37:06.198900+00:00",
    "status": 503,
    "headers": {
      "server-timing": "admission;dur=0.12, execute;dur=3408.57, app;dur=3408.69;desc=\"sjc\"",
      "x-request-id": "01M6PKDXFDTTFVR328D5TCB4HD",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "n": 7,
    "error": {
      "type": "CommitUncertainError",
      "status": 503,
      "code": "auth_unavailable",
      "request_id": "01M6PKDXFDTTFVR328D5TCB4HD"
    }
  },
  {
    "started_at": "2026-11-05T19:37:02.629145+00:00",
    "completed_at": "2026-11-05T19:37:06.414362+00:00",
    "kind": "baseline_path",
    "region": "fra",
    "ok": false,
    "ms": 3785.204,
    "response_received_at": "2026-11-05T19:37:06.413352+00:00",
    "status": 503,
    "headers": {
      "server-timing": "admission;dur=0.11, execute;dur=3573.09, app;dur=3573.20;desc=\"fra\"",
      "x-request-id": "01M6PKDXGN20XH60EQ398DF469",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "n": 6,
    "error": {
      "type": "CommitUncertainError",
      "status": 503,
      "code": "auth_unavailable",
      "request_id": "01M6PKDXGN20XH60EQ398DF469"
    }
  }
]
```

Slowest twelve write attempts, without filtering errors:

```json
[
  {
    "started_at": "2026-11-05T19:34:36.609964+00:00",
    "completed_at": "2026-11-05T19:34:55.049019+00:00",
    "kind": "write",
    "region": "sjc",
    "ok": false,
    "ms": 18439.042,
    "response_received_at": "2026-11-05T19:34:55.048168+00:00",
    "status": 400,
    "headers": {
      "server-timing": "admission;dur=0.21, auth;dur=193.99, home_lookup;dur=1.53, qos;dur=22.43, project;dur=0.12, placement;dur=0.07, tenant;dur=34.26, execute;dur=354.70, app;dur=607.32;desc=\"sjc\"",
      "x-request-id": "01M6PK9EXBFA53ZYT0WSMFG6ZR",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "repeat": 2,
    "n": 41,
    "id": "01M6PK9ET12KERWG5JHFS0S9ZE",
    "error": {
      "type": "SqlError",
      "status": 400,
      "code": "sql_error",
      "request_id": "01M6PK9EXBFA53ZYT0WSMFG6ZR"
    }
  },
  {
    "started_at": "2026-11-05T19:35:42.747609+00:00",
    "completed_at": "2026-11-05T19:35:56.083705+00:00",
    "kind": "write",
    "region": "fra",
    "ok": false,
    "ms": 13336.083,
    "response_received_at": "2026-11-05T19:35:56.081608+00:00",
    "status": 400,
    "headers": {
      "server-timing": "admission;dur=4.50, auth;dur=210.01, home_lookup;dur=12.28, qos;dur=17.04, project;dur=0.08, placement;dur=0.05, tenant;dur=31.60, execute;dur=186.35, app;dur=462.11;desc=\"fra\"",
      "x-request-id": "01M6PKBFGAVM92XCMJFJSQQ4VZ",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "repeat": 3,
    "n": 30,
    "id": "01M6PKBFCVQB0V6VWPER7DPAEG",
    "error": {
      "type": "SqlError",
      "status": 400,
      "code": "sql_error",
      "request_id": "01M6PKBFGAVM92XCMJFJSQQ4VZ"
    }
  },
  {
    "started_at": "2026-11-05T19:35:00.967729+00:00",
    "completed_at": "2026-11-05T19:35:04.766723+00:00",
    "kind": "write",
    "region": "fra",
    "ok": true,
    "ms": 3798.986,
    "response_received_at": "2026-11-05T19:35:04.766066+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.17, auth;dur=0.10, home_lookup;dur=0.95, qos;dur=0.03, project;dur=0.01, placement;dur=0.01, tenant;dur=0.17, replica_open;dur=3529.00, route;dur=1.04, local_begin;dur=38.73, local_allowance;dur=0.88, local_native;dur=0.17, local_execute;dur=1.07, ack;dur=12.19, local_capture;dur=0.26, local_hlc;dur=3.75, local_seal;dur=0.12, push_publish;dur=0.38, push_hold;dur=4.90, local_commit;dur=2.74, execute;dur=0.10, app;dur=3583.57;desc=\"fra\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PKA6PQQGN7JEJ6ZGV0KZK0",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "repeat": 2,
    "n": 54,
    "id": "01M6PKA6K7ETVN5F9XXA9HPRE9"
  },
  {
    "started_at": "2026-11-05T19:35:36.540590+00:00",
    "completed_at": "2026-11-05T19:35:38.879158+00:00",
    "kind": "write",
    "region": "sjc",
    "ok": true,
    "ms": 2338.557,
    "response_received_at": "2026-11-05T19:35:38.878822+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.39, home_lookup;dur=0.73, qos;dur=0.02, project;dur=0.01, placement;dur=0.01, tenant;dur=0.11, replica_open;dur=2105.47, route;dur=0.61, local_begin;dur=19.81, local_allowance;dur=0.01, local_native;dur=0.24, local_execute;dur=0.27, ack;dur=16.80, local_capture;dur=0.28, local_hlc;dur=5.29, local_seal;dur=0.11, push_publish;dur=1.03, push_hold;dur=7.09, local_commit;dur=2.93, execute;dur=0.63, app;dur=2144.98;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PKB9E6DGS9Z8GPD4PV8V0K",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "repeat": 3,
    "n": 8,
    "id": "01M6PKB9AWGB7MK869DKP53RSK"
  },
  {
    "started_at": "2026-11-05T19:35:28.439855+00:00",
    "completed_at": "2026-11-05T19:35:30.651464+00:00",
    "kind": "write",
    "region": "sjc",
    "ok": true,
    "ms": 2211.595,
    "response_received_at": "2026-11-05T19:35:30.650815+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.13, home_lookup;dur=0.74, qos;dur=0.03, project;dur=0.01, placement;dur=0.01, tenant;dur=0.14, replica_open;dur=0.17, route;dur=0.61, local_begin;dur=5.60, local_allowance;dur=0.01, local_native;dur=0.18, local_execute;dur=0.22, ack;dur=2006.98, local_capture;dur=0.34, local_hlc;dur=0.01, local_seal;dur=0.11, push_publish;dur=0.54, push_hold;dur=2002.43, local_commit;dur=3.47, home_wait;dur=0.06, execute;dur=0.17, app;dur=2014.94;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PKB1H39TTZGSQX6HFJXVTF",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "repeat": 2,
    "n": 95,
    "id": "01M6PKB1DQ8KNVR3X65ZE86297"
  },
  {
    "started_at": "2026-11-05T19:36:35.166599+00:00",
    "completed_at": "2026-11-05T19:36:37.095033+00:00",
    "kind": "write",
    "region": "fra",
    "ok": true,
    "ms": 1928.428,
    "response_received_at": "2026-11-05T19:36:37.094299+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.11, auth;dur=50.89, home_lookup;dur=0.86, qos;dur=0.02, project;dur=0.02, placement;dur=0.01, tenant;dur=0.10, replica_open;dur=0.14, route;dur=0.57, local_begin;dur=6.40, local_allowance;dur=0.01, local_native;dur=0.44, local_execute;dur=0.46, ack;dur=1653.86, local_capture;dur=0.32, local_hlc;dur=0.01, local_seal;dur=0.11, push_publish;dur=0.74, push_hold;dur=1648.87, local_commit;dur=3.78, execute;dur=0.49, app;dur=1713.95;desc=\"fra\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PKD2PDCKJK0BHFYQK6XWQF",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "repeat": 3,
    "n": 99,
    "id": "01M6PKD2JYNHAK2T29T35XT1JH"
  },
  {
    "started_at": "2026-11-05T19:34:28.331555+00:00",
    "completed_at": "2026-11-05T19:34:29.409312+00:00",
    "kind": "write",
    "region": "sjc",
    "ok": true,
    "ms": 1077.745,
    "response_received_at": "2026-11-05T19:34:29.408723+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.15, auth;dur=0.12, home_lookup;dur=1.36, qos;dur=0.04, project;dur=0.02, placement;dur=0.01, tenant;dur=0.12, replica_open;dur=0.16, route;dur=0.66, local_begin;dur=866.54, local_allowance;dur=0.01, local_native;dur=0.21, local_execute;dur=0.24, ack;dur=12.26, local_capture;dur=0.40, local_hlc;dur=0.00, local_seal;dur=0.17, push_publish;dur=1.37, push_hold;dur=7.35, local_commit;dur=2.92, execute;dur=0.09, app;dur=881.77;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PK96TPTS5G8VH8YS04ZWGJ",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "repeat": 2,
    "n": 25,
    "id": "01M6PK96QBHKDBHQ5D6M19RTKC"
  },
  {
    "started_at": "2026-11-05T19:35:56.084941+00:00",
    "completed_at": "2026-11-05T19:35:57.053169+00:00",
    "kind": "write",
    "region": "fra",
    "ok": true,
    "ms": 968.217,
    "response_received_at": "2026-11-05T19:35:57.052391+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.09, auth;dur=0.74, home_lookup;dur=0.00, qos;dur=4.37, project;dur=0.04, placement;dur=3.74, tenant;dur=0.10, execute;dur=11.56, app;dur=20.65;desc=\"gru\", home;dur=209.4;desc=\"gru\", admission;dur=4.16, auth;dur=1.74, home_lookup;dur=1.20, qos;dur=0.06, project;dur=0.04, placement;dur=0.02, tenant;dur=0.25, replica_open;dur=519.22, route;dur=11.39, execute;dur=211.53, app;dur=749.63;desc=\"fra\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=home",
      "x-request-id": "01M6PKBWH45QZDFRGPENBM9P76",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "repeat": 3,
    "n": 31,
    "id": "01M6PKBWDMPFR5W4TYDS3V9AZP"
  },
  {
    "started_at": "2026-11-05T19:34:55.049150+00:00",
    "completed_at": "2026-11-05T19:34:55.969677+00:00",
    "kind": "write",
    "region": "sjc",
    "ok": true,
    "ms": 920.517,
    "response_received_at": "2026-11-05T19:34:55.969028+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.27, auth;dur=6.01, home_lookup;dur=0.01, qos;dur=1.57, project;dur=0.05, placement;dur=2.71, tenant;dur=0.11, execute;dur=29.69, app;dur=40.44;desc=\"gru\", home;dur=213.2;desc=\"gru\", admission;dur=6.92, auth;dur=0.68, home_lookup;dur=11.66, qos;dur=4.71, project;dur=0.09, placement;dur=0.03, tenant;dur=8.10, replica_open;dur=466.67, route;dur=12.10, execute;dur=215.49, app;dur=726.52;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=home",
      "x-request-id": "01M6PKA0XJAXV17S0DV8JNAVRE",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "repeat": 2,
    "n": 42,
    "id": "01M6PKA0T9DBVQ8XSC5EHA63HY"
  },
  {
    "started_at": "2026-11-05T19:34:55.052147+00:00",
    "completed_at": "2026-11-05T19:34:55.929205+00:00",
    "kind": "write",
    "region": "fra",
    "ok": true,
    "ms": 877.049,
    "response_received_at": "2026-11-05T19:34:55.928436+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.06, auth;dur=185.15, home_lookup;dur=4.41, qos;dur=0.83, project;dur=0.01, placement;dur=0.00, tenant;dur=7.77, replica_open;dur=0.14, route;dur=1.63, local_begin;dur=16.01, local_allowance;dur=0.01, local_native;dur=0.25, local_execute;dur=0.29, ack;dur=19.38, local_capture;dur=0.27, local_hlc;dur=7.71, local_seal;dur=0.10, push_publish;dur=2.19, push_hold;dur=6.03, local_commit;dur=3.02, execute;dur=0.09, app;dur=236.08;desc=\"fra\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PKA1B6KJ979WGW1C2FSZT5",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "repeat": 2,
    "n": 43,
    "id": "01M6PKA0TCE7226BJGT44WJ05X"
  },
  {
    "started_at": "2026-11-05T19:36:15.858466+00:00",
    "completed_at": "2026-11-05T19:36:16.693414+00:00",
    "kind": "write",
    "region": "fra",
    "ok": true,
    "ms": 834.935,
    "response_received_at": "2026-11-05T19:36:16.692676+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.16, auth;dur=0.20, home_lookup;dur=0.81, qos;dur=0.04, project;dur=0.03, placement;dur=0.02, tenant;dur=0.19, replica_open;dur=0.20, route;dur=0.64, local_begin;dur=567.10, local_allowance;dur=0.02, local_native;dur=0.20, local_execute;dur=1.75, ack;dur=48.27, local_capture;dur=3.07, local_hlc;dur=5.73, local_seal;dur=0.39, push_publish;dur=1.26, push_hold;dur=34.18, local_commit;dur=3.25, execute;dur=0.62, app;dur=620.04;desc=\"fra\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PKCFV21KW1K9XGVKXJ31R8",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "repeat": 3,
    "n": 60,
    "id": "01M6PKCFQJ6JKW18A7DVA0M591"
  },
  {
    "started_at": "2026-11-05T19:35:56.087397+00:00",
    "completed_at": "2026-11-05T19:35:56.903560+00:00",
    "kind": "write",
    "region": "sjc",
    "ok": true,
    "ms": 816.145,
    "response_received_at": "2026-11-05T19:35:56.902952+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.11, auth;dur=183.38, home_lookup;dur=3.44, qos;dur=0.69, project;dur=0.02, placement;dur=0.01, tenant;dur=4.61, replica_open;dur=0.14, route;dur=1.29, local_begin;dur=6.18, local_allowance;dur=0.01, local_native;dur=0.25, local_execute;dur=0.27, ack;dur=17.87, local_capture;dur=0.24, local_hlc;dur=3.23, local_seal;dur=0.14, push_publish;dur=2.38, push_hold;dur=8.86, local_commit;dur=2.97, execute;dur=0.09, app;dur=218.11;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PKBWXJNAMZVMRRXRBM5W80",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "repeat": 3,
    "n": 17,
    "id": "01M6PKBWDPRNW42D0VQSJKNC03"
  }
]
```

All thirty immediate home checks following home-durable acknowledgement:

```json
[
  {
    "source": "gru",
    "id": "01M6PKD5BHX5BTWRE7D852FXC3",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T19:36:38.071559+00:00",
    "headers": {
      "server-timing": "admission;dur=0.11, auth;dur=0.13, home_lookup;dur=4.06, qos;dur=1.79, project;dur=0.03, placement;dur=0.01, tenant;dur=2.04, execute;dur=0.72, app;dur=8.89;desc=\"gru\"",
      "x-request-id": "01M6PKD5D7RPSSF1JDGMKEX0EQ",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "gru",
    "id": "01M6PKD5DQREGXEB3G0SJE9ZB9",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T19:36:38.129770+00:00",
    "headers": {
      "server-timing": "admission;dur=0.13, auth;dur=0.12, home_lookup;dur=0.89, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.11, execute;dur=0.75, app;dur=2.06;desc=\"gru\"",
      "x-request-id": "01M6PKD5F9JQ0PXBZBZJ3QNANH",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "gru",
    "id": "01M6PKD5FHJ06M4GVYRHBQ53KW",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T19:36:38.197232+00:00",
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.11, home_lookup;dur=0.50, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.08, execute;dur=0.62, app;dur=1.50;desc=\"gru\"",
      "x-request-id": "01M6PKD5HCBFXN82685MXSQAAE",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "gru",
    "id": "01M6PKD5HN7H0XPT1ZHRNBWMFK",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T19:36:38.265810+00:00",
    "headers": {
      "server-timing": "admission;dur=0.11, auth;dur=0.31, home_lookup;dur=5.48, qos;dur=0.09, project;dur=0.02, placement;dur=0.01, tenant;dur=0.08, execute;dur=0.97, app;dur=7.08;desc=\"gru\"",
      "x-request-id": "01M6PKD5KCRQ2E509NBDN8PTQJ",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "gru",
    "id": "01M6PKD5KSCDPTV6Z8HKGWMKTH",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T19:36:38.324231+00:00",
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.16, home_lookup;dur=1.02, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.10, execute;dur=0.73, app;dur=2.19;desc=\"gru\"",
      "x-request-id": "01M6PKD5NB6JN93F2C50F1G6N4",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "gru",
    "id": "01M6PKD5NMHAW20E07RRNFWZN4",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T19:36:38.380768+00:00",
    "headers": {
      "server-timing": "admission;dur=0.13, auth;dur=0.15, home_lookup;dur=0.82, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.10, execute;dur=0.66, app;dur=1.92;desc=\"gru\"",
      "x-request-id": "01M6PKD5Q42YANM3QCM3W4SEN1",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "gru",
    "id": "01M6PKD5QCTAAVRXDBD2QFBC3J",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T19:36:38.437860+00:00",
    "headers": {
      "server-timing": "admission;dur=0.13, auth;dur=0.15, home_lookup;dur=0.85, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.10, execute;dur=0.62, app;dur=1.90;desc=\"gru\"",
      "x-request-id": "01M6PKD5RXT35P84271WQWK4B8",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "gru",
    "id": "01M6PKD5S6G3NE8F2FE7TXNY1V",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T19:36:38.496308+00:00",
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.14, home_lookup;dur=0.84, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.08, execute;dur=0.71, app;dur=1.95;desc=\"gru\"",
      "x-request-id": "01M6PKD5TQZB23FANJNZ1JYD7B",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "gru",
    "id": "01M6PKD5V0PE41VSG8ST0QXH9M",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T19:36:38.553833+00:00",
    "headers": {
      "server-timing": "admission;dur=0.13, auth;dur=0.12, home_lookup;dur=0.90, qos;dur=0.03, project;dur=0.01, placement;dur=0.01, tenant;dur=0.08, execute;dur=0.75, app;dur=2.03;desc=\"gru\"",
      "x-request-id": "01M6PKD5WGQSXMDEBZQ98GD26R",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "gru",
    "id": "01M6PKD5WSNYR4DPJX0TGXTX67",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T19:36:38.610116+00:00",
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.14, home_lookup;dur=0.86, qos;dur=0.05, project;dur=0.02, placement;dur=0.01, tenant;dur=0.08, execute;dur=0.66, app;dur=1.95;desc=\"gru\"",
      "x-request-id": "01M6PKD5Y9A5ENYY37B098C58G",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "sjc",
    "id": "01M6PKD7F451F0JY6JB8FTJEXS",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T19:36:40.600572+00:00",
    "headers": {
      "server-timing": "admission;dur=0.14, auth;dur=8.54, home_lookup;dur=3.61, qos;dur=1.86, project;dur=0.03, placement;dur=0.01, tenant;dur=1.86, execute;dur=0.85, app;dur=16.90;desc=\"gru\"",
      "x-request-id": "01M6PKD7W0WG3HSET9X1SK9PXH",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "sjc",
    "id": "01M6PKD7WR1TVDW3VP5GMEMK6Y",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T19:36:41.025209+00:00",
    "headers": {
      "server-timing": "admission;dur=0.14, auth;dur=0.12, home_lookup;dur=0.87, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.08, execute;dur=0.73, app;dur=2.00;desc=\"gru\"",
      "x-request-id": "01M6PKD89RNRNTQQ5THMH98M7S",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "sjc",
    "id": "01M6PKD8A1ECDMSEHS5HZS3STP",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T19:36:41.443287+00:00",
    "headers": {
      "server-timing": "admission;dur=0.16, auth;dur=0.58, home_lookup;dur=0.75, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.10, execute;dur=0.68, app;dur=2.34;desc=\"gru\"",
      "x-request-id": "01M6PKD8PT28518BCEPKAV6YPW",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "sjc",
    "id": "01M6PKD8Q36J5FC0BKBP6A0SZ4",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T19:36:41.855161+00:00",
    "headers": {
      "server-timing": "admission;dur=0.16, auth;dur=0.14, home_lookup;dur=1.00, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.10, execute;dur=0.68, app;dur=2.15;desc=\"gru\"",
      "x-request-id": "01M6PKD93PXP6QK0SZQHFZN6TA",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "sjc",
    "id": "01M6PKD93ZR74M7Z62ER4V9C9F",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T19:36:42.267335+00:00",
    "headers": {
      "server-timing": "admission;dur=0.09, auth;dur=0.26, home_lookup;dur=2.38, qos;dur=0.03, project;dur=0.01, placement;dur=0.01, tenant;dur=0.05, execute;dur=0.88, app;dur=3.71;desc=\"gru\"",
      "x-request-id": "01M6PKD9GH5BDV6RXN8905VCNJ",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "sjc",
    "id": "01M6PKD9GV8GRFSZF3WWWWF477",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T19:36:42.682887+00:00",
    "headers": {
      "server-timing": "admission;dur=0.28, auth;dur=0.14, home_lookup;dur=0.98, qos;dur=0.04, project;dur=0.02, placement;dur=0.01, tenant;dur=0.08, execute;dur=0.68, app;dur=2.23;desc=\"gru\"",
      "x-request-id": "01M6PKD9XH6M5NBFY7EP29279B",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "sjc",
    "id": "01M6PKD9XVPSKK4TNY0SB2T7VE",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T19:36:43.096516+00:00",
    "headers": {
      "server-timing": "admission;dur=0.13, auth;dur=2.41, home_lookup;dur=1.21, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.08, execute;dur=1.67, app;dur=5.56;desc=\"gru\"",
      "x-request-id": "01M6PKDAABQ0TCK2KHGCHCYFHR",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "sjc",
    "id": "01M6PKDAARG7TBJ3KTV23NSXS2",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T19:36:43.549661+00:00",
    "headers": {
      "server-timing": "admission;dur=0.14, auth;dur=0.12, home_lookup;dur=3.44, qos;dur=1.42, project;dur=0.03, placement;dur=0.01, tenant;dur=1.67, execute;dur=0.65, app;dur=7.48;desc=\"gru\"",
      "x-request-id": "01M6PKDARFQP6F5BZKJF98Y2QV",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "sjc",
    "id": "01M6PKDARX93D4411ZWQ4V0AKW",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T19:36:43.967849+00:00",
    "headers": {
      "server-timing": "admission;dur=0.09, auth;dur=0.49, home_lookup;dur=0.81, qos;dur=0.03, project;dur=0.01, placement;dur=0.01, tenant;dur=0.06, execute;dur=0.79, app;dur=2.29;desc=\"gru\"",
      "x-request-id": "01M6PKDB5P0X6FAX5HKVH4W296",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "sjc",
    "id": "01M6PKDB60XGQ2D211M6TSNA4W",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T19:36:44.393080+00:00",
    "headers": {
      "server-timing": "admission;dur=0.17, auth;dur=0.16, home_lookup;dur=1.86, qos;dur=0.04, project;dur=0.02, placement;dur=0.01, tenant;dur=0.08, execute;dur=1.12, app;dur=3.47;desc=\"gru\"",
      "x-request-id": "01M6PKDBJWKC654D3V6RJ3DP21",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "fra",
    "id": "01M6PKDH79VDYF2HG1YQBVBG0Z",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T19:36:50.676205+00:00",
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=6.11, home_lookup;dur=3.64, qos;dur=1.73, project;dur=0.03, placement;dur=0.03, tenant;dur=1.98, execute;dur=2.33, app;dur=15.98;desc=\"gru\"",
      "x-request-id": "01M6PKDHPWHQTJ0ZBHKS9EGAD1",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "fra",
    "id": "01M6PKDHQMAVXVSH6WSPMCK2RV",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T19:36:51.134288+00:00",
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.14, home_lookup;dur=0.83, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.13, execute;dur=0.74, app;dur=2.03;desc=\"gru\"",
      "x-request-id": "01M6PKDJ5NG054ZXNGKBSF90HJ",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "fra",
    "id": "01M6PKDJ5YQQ0TQQSYAS0KZXTP",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T19:36:51.595314+00:00",
    "headers": {
      "server-timing": "admission;dur=0.09, auth;dur=0.24, home_lookup;dur=1.32, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.06, execute;dur=1.14, app;dur=2.90;desc=\"gru\"",
      "x-request-id": "01M6PKDJM1E4HYEQKSQF5FYSB3",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "fra",
    "id": "01M6PKDJMBYYJR4ACMHD23MRQQ",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T19:36:52.048014+00:00",
    "headers": {
      "server-timing": "admission;dur=0.11, auth;dur=0.13, home_lookup;dur=0.52, qos;dur=0.03, project;dur=0.01, placement;dur=0.01, tenant;dur=0.05, execute;dur=0.60, app;dur=1.49;desc=\"gru\"",
      "x-request-id": "01M6PKDK27HGDYVMKNEAWSFB1B",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "fra",
    "id": "01M6PKDK2GDRZCD33HTZ240F65",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T19:36:52.508805+00:00",
    "headers": {
      "server-timing": "admission;dur=0.13, auth;dur=0.27, home_lookup;dur=1.03, qos;dur=0.03, project;dur=0.01, placement;dur=0.01, tenant;dur=0.10, execute;dur=2.89, app;dur=4.49;desc=\"gru\"",
      "x-request-id": "01M6PKDKGHMR7D5ZEZGEFKWN80",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "fra",
    "id": "01M6PKDKGW1RW1MEJHQZBQHHHP",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T19:36:52.962549+00:00",
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.16, home_lookup;dur=0.85, qos;dur=0.04, project;dur=0.02, placement;dur=0.01, tenant;dur=0.08, execute;dur=0.68, app;dur=1.96;desc=\"gru\"",
      "x-request-id": "01M6PKDKYSCFDK50BZMPZ32JEK",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "fra",
    "id": "01M6PKDKZ2M0AEE788DB7XKK63",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T19:36:53.414716+00:00",
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.27, home_lookup;dur=1.12, qos;dur=0.05, project;dur=0.02, placement;dur=0.01, tenant;dur=0.07, execute;dur=1.68, app;dur=3.34;desc=\"gru\"",
      "x-request-id": "01M6PKDMCWD0KA29N8S8Y83Q68",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "fra",
    "id": "01M6PKDMD6J0WJ1YESNZPDG5HX",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T19:36:53.880466+00:00",
    "headers": {
      "server-timing": "admission;dur=0.10, auth;dur=0.13, home_lookup;dur=5.37, qos;dur=2.08, project;dur=0.03, placement;dur=0.01, tenant;dur=2.20, execute;dur=0.91, app;dur=10.83;desc=\"gru\"",
      "x-request-id": "01M6PKDMV6HHZNP16GYK4APK58",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "fra",
    "id": "01M6PKDMVRWYT88VXQ3ZT3H6T5",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T19:36:54.330592+00:00",
    "headers": {
      "server-timing": "admission;dur=0.09, auth;dur=0.29, home_lookup;dur=1.08, qos;dur=0.03, project;dur=0.01, placement;dur=0.01, tenant;dur=0.06, execute;dur=1.17, app;dur=2.76;desc=\"gru\"",
      "x-request-id": "01M6PKDN9G6PJNFF353ZCQJD25",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "fra",
    "id": "01M6PKDN9T9CZ799WJEE57PPP9",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T19:36:54.779922+00:00",
    "headers": {
      "server-timing": "admission;dur=0.14, auth;dur=0.14, home_lookup;dur=0.91, qos;dur=0.03, project;dur=0.01, placement;dur=0.01, tenant;dur=0.08, execute;dur=0.72, app;dur=2.05;desc=\"gru\"",
      "x-request-id": "01M6PKDNQJKAJ7NQKQ3EQK2YZH",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  }
]
```

SJC follow-up reconciliation of failed/acknowledged IDs:

```json
{
  "at": "2026-11-05T19:37:10.039557+00:00",
  "region": "sjc",
  "ok": true,
  "rows": [
    {
      "id": "01M6PK9EPV9ZR7MHCMQ0A0Q5NB",
      "writer": "fra",
      "sequence": 2042,
      "body": "acceptance"
    },
    {
      "id": "01M6PK9EXKEGAYBJNMP2V06M0V",
      "writer": "gru",
      "sequence": 2045,
      "body": "acceptance"
    }
  ],
  "ms": 888.835
}
```

## Independent fresh CDN deletion after afa0171 fix

One fresh Pro database, nine rows, one SDK-used sealed URL warmed twice at every POP with the SDK user agent. No manual purge by acceptance. DELETE204 completes 19:43:00.385929Z; all SQL, metadata and prior key requests are404. The CDN URL is404/BYPASS everywhere by19.445038 seconds, within420 seconds.

```json
[
  {
    "kind": "cdn_retest_created",
    "at": "2026-11-05T19:40:21.196930+00:00",
    "value": {
      "id": "01M6PKKYX90SWSYCBYH9304XSK",
      "project_id": "01M6PENV06CRPSJYDDRZTF75R4",
      "name": "milestone-b-cdn-retest",
      "created_at": "2026-11-05T19:40:20.7774085+00:00",
      "region": "gru",
      "home_region": "gru",
      "branched_from": null,
      "raw": {
        "id": "01M6PKKYX90SWSYCBYH9304XSK",
        "projectId": "01M6PENV06CRPSJYDDRZTF75R4",
        "name": "milestone-b-cdn-retest",
        "branchedFrom": null,
        "createdAt": "2026-11-05T19:40:20.7774085+00:00",
        "region": "gru",
        "homeRegion": "gru",
        "replicaRegions": [
          "fra",
          "sjc"
        ],
        "allRegions": true,
        "previousName": null,
        "conflictOf": null,
        "expiresAt": null
      },
      "previous_name": null,
      "conflict_of": null,
      "expires_at": null,
      "replica_regions": [
        "fra",
        "sjc"
      ],
      "all_regions": true
    }
  },
  {
    "kind": "cdn_retest_seed",
    "id": "01M6PKKZBV5AB1YZM74C7GCF8N",
    "n": 0,
    "ok": true,
    "at": "2026-11-05T19:40:21.276554+00:00"
  },
  {
    "kind": "cdn_retest_seed",
    "id": "01M6PKKZCWDV4NNEBF9RTNSVTJ",
    "n": 1,
    "ok": true,
    "at": "2026-11-05T19:40:21.318555+00:00"
  },
  {
    "kind": "cdn_retest_seed",
    "id": "01M6PKKZE63EVF59DTQ612TPMV",
    "n": 2,
    "ok": true,
    "at": "2026-11-05T19:40:21.350526+00:00"
  },
  {
    "kind": "cdn_retest_seed",
    "id": "01M6PKKZF6S5CG6S0HTSXGXBD2",
    "n": 3,
    "ok": true,
    "at": "2026-11-05T19:40:21.382319+00:00"
  },
  {
    "kind": "cdn_retest_seed",
    "id": "01M6PKKZG6XJ49EV6T59WPY9X8",
    "n": 4,
    "ok": true,
    "at": "2026-11-05T19:40:21.413040+00:00"
  },
  {
    "kind": "cdn_retest_seed",
    "id": "01M6PKKZH57GVYR3NXWNK6GGM1",
    "n": 5,
    "ok": true,
    "at": "2026-11-05T19:40:21.455786+00:00"
  },
  {
    "kind": "cdn_retest_seed",
    "id": "01M6PKKZJFCEJEFSXVZWKA6S4Z",
    "n": 6,
    "ok": true,
    "at": "2026-11-05T19:40:21.485618+00:00"
  },
  {
    "kind": "cdn_retest_seed",
    "id": "01M6PKKZKDAE5VFYENBQ5GV0FT",
    "n": 7,
    "ok": true,
    "at": "2026-11-05T19:40:21.516899+00:00"
  },
  {
    "kind": "cdn_retest_seed",
    "id": "01M6PKKZMC1XE3T5MNHTQ18B90",
    "n": 8,
    "ok": true,
    "at": "2026-11-05T19:40:21.550268+00:00"
  }
]
```

SDK snapshots:

```json
[
  {
    "kind": "shape_result",
    "region": "gru",
    "ok": true,
    "rows": 9,
    "provisional": 0,
    "offset": "35"
  },
  {
    "kind": "shape_result",
    "region": "sjc",
    "ok": true,
    "rows": 9,
    "provisional": 0,
    "offset": "35"
  },
  {
    "kind": "shape_result",
    "region": "fra",
    "ok": true,
    "rows": 9,
    "provisional": 0,
    "offset": "35"
  }
]
```

`cdn-retest-before-delete.json`:

```json
{
  "deleted_at": null,
  "coordinator_check_started_at": "2026-11-05T19:41:48.762849+00:00",
  "url_count": 1,
  "repeats": 2,
  "regions": [
    {
      "region": "gru",
      "results": [
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-ed1798ce23044c51/aac57f1b86d1a2e4b240dbf8823a0904918cf7c9cb3fdd3050ad738f2e047fe7",
          "n": 0,
          "status": 200,
          "started_at": "2026-11-05T19:41:49.744390+00:00",
          "completed_at": "2026-11-05T19:41:49.957656+00:00",
          "request_ms": 213.266,
          "bytes": 1405,
          "sha256": "07adbf475402a7eff9d29c2c7317cc46b81737666ac11cb31b8fbb68149f885b",
          "headers": {
            "age": null,
            "cf-cache-status": "HIT",
            "cf-ray": "a55e56923a47d7d4-GRU",
            "date": "Thu, 05 Nov 2026 19:41:49 GMT"
          }
        },
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-ed1798ce23044c51/aac57f1b86d1a2e4b240dbf8823a0904918cf7c9cb3fdd3050ad738f2e047fe7",
          "n": 1,
          "status": 200,
          "started_at": "2026-11-05T19:41:49.957701+00:00",
          "completed_at": "2026-11-05T19:41:50.011649+00:00",
          "request_ms": 53.954,
          "bytes": 1405,
          "sha256": "07adbf475402a7eff9d29c2c7317cc46b81737666ac11cb31b8fbb68149f885b",
          "headers": {
            "age": "39",
            "cf-cache-status": "HIT",
            "cf-ray": "a55e56935ef8a4c9-GRU",
            "date": "Thu, 05 Nov 2026 19:41:50 GMT"
          }
        }
      ]
    },
    {
      "region": "sjc",
      "results": [
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-ed1798ce23044c51/aac57f1b86d1a2e4b240dbf8823a0904918cf7c9cb3fdd3050ad738f2e047fe7",
          "n": 0,
          "status": 200,
          "started_at": "2026-11-05T19:41:51.283407+00:00",
          "completed_at": "2026-11-05T19:41:51.516259+00:00",
          "request_ms": 232.849,
          "bytes": 1405,
          "sha256": "07adbf475402a7eff9d29c2c7317cc46b81737666ac11cb31b8fbb68149f885b",
          "headers": {
            "age": null,
            "cf-cache-status": "HIT",
            "cf-ray": "a55e569bf8b415da-SJC",
            "date": "Thu, 05 Nov 2026 19:41:51 GMT"
          }
        },
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-ed1798ce23044c51/aac57f1b86d1a2e4b240dbf8823a0904918cf7c9cb3fdd3050ad738f2e047fe7",
          "n": 1,
          "status": 200,
          "started_at": "2026-11-05T19:41:51.516304+00:00",
          "completed_at": "2026-11-05T19:41:51.547428+00:00",
          "request_ms": 31.13,
          "bytes": 1405,
          "sha256": "07adbf475402a7eff9d29c2c7317cc46b81737666ac11cb31b8fbb68149f885b",
          "headers": {
            "age": "41",
            "cf-cache-status": "HIT",
            "cf-ray": "a55e569d19adcf11-SJC",
            "date": "Thu, 05 Nov 2026 19:41:51 GMT"
          }
        }
      ]
    },
    {
      "region": "fra",
      "results": [
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-ed1798ce23044c51/aac57f1b86d1a2e4b240dbf8823a0904918cf7c9cb3fdd3050ad738f2e047fe7",
          "n": 0,
          "status": 200,
          "started_at": "2026-11-05T19:41:51.815963+00:00",
          "completed_at": "2026-11-05T19:41:51.994265+00:00",
          "request_ms": 178.299,
          "bytes": 1405,
          "sha256": "07adbf475402a7eff9d29c2c7317cc46b81737666ac11cb31b8fbb68149f885b",
          "headers": {
            "age": null,
            "cf-cache-status": "HIT",
            "cf-ray": "a55e569f2dc2dc90-FRA",
            "date": "Thu, 05 Nov 2026 19:41:51 GMT"
          }
        },
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-ed1798ce23044c51/aac57f1b86d1a2e4b240dbf8823a0904918cf7c9cb3fdd3050ad738f2e047fe7",
          "n": 1,
          "status": 200,
          "started_at": "2026-11-05T19:41:51.994310+00:00",
          "completed_at": "2026-11-05T19:41:52.068768+00:00",
          "request_ms": 74.464,
          "bytes": 1405,
          "sha256": "07adbf475402a7eff9d29c2c7317cc46b81737666ac11cb31b8fbb68149f885b",
          "headers": {
            "age": "41",
            "cf-cache-status": "HIT",
            "cf-ray": "a55e56a0184f1e10-FRA",
            "date": "Thu, 05 Nov 2026 19:41:52 GMT"
          }
        }
      ]
    }
  ]
}
```

`cdn-retest-deletion.json`:

```json
[
  {
    "status": 200,
    "received_at": "2026-11-05T19:42:54.318922+00:00",
    "headers": {
      "x-request-id": "01M6PKRMCE3ZB5QJ60WE2F5HXE",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "ok": true,
    "kind": "cdn-retest-key-before-delete",
    "region": "gru",
    "started_at": "2026-11-05T19:42:53.829362+00:00",
    "completed_at": "2026-11-05T19:42:54.319351+00:00",
    "ms": 490.0
  },
  {
    "status": 200,
    "received_at": "2026-11-05T19:42:55.249122+00:00",
    "headers": {
      "server-timing": "home;dur=176.1;desc=\"gru\"",
      "x-request-id": "01M6PKRNAGNB323KKGT7PJV536",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "ok": true,
    "kind": "cdn-retest-key-before-delete",
    "region": "sjc",
    "started_at": "2026-11-05T19:42:54.319526+00:00",
    "completed_at": "2026-11-05T19:42:55.249570+00:00",
    "ms": 930.054
  },
  {
    "status": 200,
    "received_at": "2026-11-05T19:42:56.255540+00:00",
    "headers": {
      "server-timing": "home;dur=182.4;desc=\"gru\"",
      "x-request-id": "01M6PKRP8ZZKKAFCCWZ035YV1M",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "ok": true,
    "kind": "cdn-retest-key-before-delete",
    "region": "fra",
    "started_at": "2026-11-05T19:42:55.249664+00:00",
    "completed_at": "2026-11-05T19:42:56.256008+00:00",
    "ms": 1006.354
  },
  {
    "status": 204,
    "received_at": "2026-11-05T19:43:00.385467+00:00",
    "headers": {
      "x-request-id": "01M6PKRPR96EB7238JEVMA7ZR8",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "ok": true,
    "kind": "cdn-retest-delete-database",
    "region": "gru",
    "started_at": "2026-11-05T19:42:56.256131+00:00",
    "completed_at": "2026-11-05T19:43:00.385929+00:00",
    "ms": 4129.805
  },
  {
    "status": 404,
    "received_at": "2026-11-05T19:43:00.601757+00:00",
    "headers": {
      "server-timing": "admission;dur=0.08, auth;dur=0.27, home_lookup;dur=186.22, qos;dur=3.46, project;dur=0.03, placement;dur=0.01, execute;dur=3.69, app;dur=193.78;desc=\"gru\"",
      "x-request-id": "01M6PKRTSBQDC7475P3F4T7ZHF",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "ok": false,
    "code": "unknown_database",
    "error_type": "NotFoundError",
    "kind": "cdn-retest-sql-after-delete",
    "region": "gru",
    "started_at": "2026-11-05T19:43:00.386001+00:00",
    "completed_at": "2026-11-05T19:43:00.602095+00:00",
    "ms": 216.1
  },
  {
    "status": 404,
    "received_at": "2026-11-05T19:43:00.809692+00:00",
    "headers": {
      "x-request-id": "01M6PKRV03VWTETPNSVWVAAV0H",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "ok": false,
    "code": "unknown_database",
    "error_type": "NotFoundError",
    "kind": "cdn-retest-metadata-after-delete",
    "region": "gru",
    "started_at": "2026-11-05T19:43:00.602158+00:00",
    "completed_at": "2026-11-05T19:43:00.810118+00:00",
    "ms": 207.967
  },
  {
    "status": 404,
    "received_at": "2026-11-05T19:43:01.018387+00:00",
    "headers": {
      "x-request-id": "01M6PKRV6K4MAHG88PMPTZCE7E",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "ok": true,
    "code": "unknown_database",
    "kind": "cdn-retest-key-after-delete",
    "region": "gru",
    "started_at": "2026-11-05T19:43:00.810207+00:00",
    "completed_at": "2026-11-05T19:43:01.018676+00:00",
    "ms": 208.475
  },
  {
    "status": 200,
    "received_at": "2026-11-05T19:43:01.426300+00:00",
    "headers": {
      "x-request-id": "01M6PKRVD3QJYWSWM2JBYEBA36",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "ok": true,
    "database_ids": [
      "01M6PFQFV240ECH0BCQ5AKFYHX",
      "01M6PK77TX4H0B1D3GQZ73XZCF"
    ],
    "next_cursor": null,
    "kind": "cdn-retest-list-after-delete",
    "region": "gru",
    "started_at": "2026-11-05T19:43:01.018765+00:00",
    "completed_at": "2026-11-05T19:43:01.426894+00:00",
    "ms": 408.14
  },
  {
    "status": 404,
    "received_at": "2026-11-05T19:43:02.369672+00:00",
    "headers": {
      "server-timing": "admission;dur=0.11, auth;dur=175.11, home_lookup;dur=173.93, qos;dur=2.44, project;dur=0.02, placement;dur=0.01, execute;dur=1.04, app;dur=352.65;desc=\"sjc\"",
      "x-request-id": "01M6PKRW8Z0KZXKX5K06K44V2X",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "ok": false,
    "code": "unknown_database",
    "error_type": "NotFoundError",
    "kind": "cdn-retest-sql-after-delete",
    "region": "sjc",
    "started_at": "2026-11-05T19:43:01.427000+00:00",
    "completed_at": "2026-11-05T19:43:02.370131+00:00",
    "ms": 943.138
  },
  {
    "status": 404,
    "received_at": "2026-11-05T19:43:02.737571+00:00",
    "headers": {
      "x-request-id": "01M6PKRWT4WM2F7GE4H7CYB72K",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "ok": false,
    "code": "unknown_database",
    "error_type": "NotFoundError",
    "kind": "cdn-retest-metadata-after-delete",
    "region": "sjc",
    "started_at": "2026-11-05T19:43:02.370255+00:00",
    "completed_at": "2026-11-05T19:43:02.737987+00:00",
    "ms": 367.738
  },
  {
    "status": 404,
    "received_at": "2026-11-05T19:43:03.107189+00:00",
    "headers": {
      "x-request-id": "01M6PKRX5M8C4G1YV4T743WKYX",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "ok": true,
    "code": "unknown_database",
    "kind": "cdn-retest-key-after-delete",
    "region": "sjc",
    "started_at": "2026-11-05T19:43:02.738065+00:00",
    "completed_at": "2026-11-05T19:43:03.107591+00:00",
    "ms": 369.532
  },
  {
    "status": 200,
    "received_at": "2026-11-05T19:43:03.658232+00:00",
    "headers": {
      "x-request-id": "01M6PKRXH68YYN59HTKA395M17",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "ok": true,
    "database_ids": [
      "01M6PFQFV240ECH0BCQ5AKFYHX",
      "01M6PK77TX4H0B1D3GQZ73XZCF"
    ],
    "next_cursor": null,
    "kind": "cdn-retest-list-after-delete",
    "region": "sjc",
    "started_at": "2026-11-05T19:43:03.107653+00:00",
    "completed_at": "2026-11-05T19:43:03.658549+00:00",
    "ms": 550.902
  },
  {
    "status": 404,
    "received_at": "2026-11-05T19:43:04.667262+00:00",
    "headers": {
      "server-timing": "admission;dur=0.11, auth;dur=185.43, home_lookup;dur=182.88, qos;dur=1.72, project;dur=0.02, placement;dur=0.96, execute;dur=0.81, app;dur=371.94;desc=\"fra\"",
      "x-request-id": "01M6PKRYFVE6EG4BER3KF1FT8E",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "ok": false,
    "code": "unknown_database",
    "error_type": "NotFoundError",
    "kind": "cdn-retest-sql-after-delete",
    "region": "fra",
    "started_at": "2026-11-05T19:43:03.658616+00:00",
    "completed_at": "2026-11-05T19:43:04.668133+00:00",
    "ms": 1009.523
  },
  {
    "status": 404,
    "received_at": "2026-11-05T19:43:05.064322+00:00",
    "headers": {
      "x-request-id": "01M6PKRZ23JBQJ9ENPACFWVS44",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "ok": false,
    "code": "unknown_database",
    "error_type": "NotFoundError",
    "kind": "cdn-retest-metadata-after-delete",
    "region": "fra",
    "started_at": "2026-11-05T19:43:04.668194+00:00",
    "completed_at": "2026-11-05T19:43:05.064792+00:00",
    "ms": 396.604
  },
  {
    "status": 404,
    "received_at": "2026-11-05T19:43:05.461009+00:00",
    "headers": {
      "x-request-id": "01M6PKRZEG7PFM1R16P2X55BNG",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "ok": true,
    "code": "unknown_database",
    "kind": "cdn-retest-key-after-delete",
    "region": "fra",
    "started_at": "2026-11-05T19:43:05.064866+00:00",
    "completed_at": "2026-11-05T19:43:05.461542+00:00",
    "ms": 396.687
  },
  {
    "status": 200,
    "received_at": "2026-11-05T19:43:06.050972+00:00",
    "headers": {
      "x-request-id": "01M6PKRZTXETQBZCCE68DT0GKR",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "ok": true,
    "database_ids": [
      "01M6PFQFV240ECH0BCQ5AKFYHX",
      "01M6PK77TX4H0B1D3GQZ73XZCF"
    ],
    "next_cursor": null,
    "kind": "cdn-retest-list-after-delete",
    "region": "fra",
    "started_at": "2026-11-05T19:43:05.461663+00:00",
    "completed_at": "2026-11-05T19:43:06.051334+00:00",
    "ms": 589.676
  }
]
```

`cdn-retest-after-delete.json`:

```json
{
  "deleted_at": "2026-11-05T19:43:00.385929+00:00",
  "coordinator_check_started_at": "2026-11-05T19:43:16.736079+00:00",
  "url_count": 1,
  "repeats": 1,
  "regions": [
    {
      "region": "gru",
      "results": [
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-ed1798ce23044c51/aac57f1b86d1a2e4b240dbf8823a0904918cf7c9cb3fdd3050ad738f2e047fe7",
          "n": 0,
          "status": 404,
          "started_at": "2026-11-05T19:43:17.727000+00:00",
          "completed_at": "2026-11-05T19:43:18.089607+00:00",
          "request_ms": 362.606,
          "bytes": 27150,
          "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
          "headers": {
            "age": null,
            "cf-cache-status": "BYPASS",
            "cf-ray": "a55e58b80c6f864b-GRU",
            "date": "Thu, 05 Nov 2026 19:43:18 GMT"
          },
          "elapsed_since_delete_s": 17.341071
        }
      ]
    },
    {
      "region": "sjc",
      "results": [
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-ed1798ce23044c51/aac57f1b86d1a2e4b240dbf8823a0904918cf7c9cb3fdd3050ad738f2e047fe7",
          "n": 0,
          "status": 404,
          "started_at": "2026-11-05T19:43:19.322472+00:00",
          "completed_at": "2026-11-05T19:43:19.542667+00:00",
          "request_ms": 220.192,
          "bytes": 27150,
          "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
          "headers": {
            "age": null,
            "cf-cache-status": "BYPASS",
            "cf-ray": "a55e58c21df5f31f-SJC",
            "date": "Thu, 05 Nov 2026 19:43:19 GMT"
          },
          "elapsed_since_delete_s": 18.936543
        }
      ]
    },
    {
      "region": "fra",
      "results": [
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-ed1798ce23044c51/aac57f1b86d1a2e4b240dbf8823a0904918cf7c9cb3fdd3050ad738f2e047fe7",
          "n": 0,
          "status": 404,
          "started_at": "2026-11-05T19:43:19.830967+00:00",
          "completed_at": "2026-11-05T19:43:20.096936+00:00",
          "request_ms": 265.967,
          "bytes": 27150,
          "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
          "headers": {
            "age": null,
            "cf-cache-status": "BYPASS",
            "cf-ray": "a55e58c53a1fdc9c-FRA",
            "date": "Thu, 05 Nov 2026 19:43:20 GMT"
          },
          "elapsed_since_delete_s": 19.445038
        }
      ]
    }
  ]
}
```

## Independent R9 rerun after b06bd58a8

Three public home moves under500ms-spaced SJC writes:45/46 writes accepted, all1003 acknowledged rows reconcile exactly in all3 regions. All9 inventory responses identify their pinned target region. One refused insert retains sanitized SQL error message and exact client UTC timestamps. Large inventories are represented by count and canonical SHA-256; full sanitized raw inventories remain in the manifest path.

```json
[
  {
    "kind": "environment",
    "host": "delta",
    "sdk_revision": "ddbc3a87d7c16cedf918cb3754bad364f7c068eb",
    "at": "2026-11-05T19:56:36.996959+00:00"
  },
  {
    "kind": "move_rerun_baseline",
    "home": "gru",
    "row_count": 958,
    "distinct_ids": 958,
    "canonical_inventory_sha256": "7221ebe5a1b3e03770c1e27861d344c00c28aab1bea57c676e11541f3663841f"
  },
  {
    "started_at": "2026-11-05T19:56:37.846669+00:00",
    "completed_at": "2026-11-05T19:56:38.865541+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 1018.856,
    "response_received_at": "2026-11-05T19:56:38.864603+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.13, auth;dur=175.66, home_lookup;dur=7.45, qos;dur=2.66, project;dur=0.02, placement;dur=0.01, tenant;dur=5.32, replica_open;dur=1.61, route;dur=0.77, local_begin;dur=8.11, local_allowance;dur=0.01, local_native;dur=0.15, local_execute;dur=0.18, ack;dur=14.87, local_capture;dur=1.12, local_hlc;dur=0.00, local_seal;dur=0.07, push_publish;dur=1.33, push_hold;dur=8.13, local_commit;dur=3.40, execute;dur=0.08, app;dur=216.89;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMHSS05P9907Y5Q75B6T9Y",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMHS2PJA3H69YKVHK9ZSR6",
    "n": 0,
    "repeat": 1,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:56:39.365884+00:00",
    "completed_at": "2026-11-05T19:56:39.597584+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 231.619,
    "response_received_at": "2026-11-05T19:56:39.597351+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.09, auth;dur=0.24, home_lookup;dur=3.21, qos;dur=1.54, project;dur=0.03, placement;dur=0.01, tenant;dur=4.73, replica_open;dur=2.25, route;dur=0.92, local_begin;dur=9.08, local_allowance;dur=0.01, local_native;dur=2.05, local_execute;dur=2.08, ack;dur=10.97, local_capture;dur=0.30, local_hlc;dur=0.00, local_seal;dur=0.09, push_publish;dur=1.12, push_hold;dur=5.64, local_commit;dur=3.17, execute;dur=0.71, app;dur=35.90;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMHTNK5ZVCXXB74P8F95Y6",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMHTJ557QYRGMFRWTF0N3M",
    "n": 1,
    "repeat": 1,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:56:40.097853+00:00",
    "completed_at": "2026-11-05T19:56:40.323524+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 225.654,
    "response_received_at": "2026-11-05T19:56:40.323318+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.06, auth;dur=0.29, home_lookup;dur=2.67, qos;dur=0.02, project;dur=0.01, placement;dur=0.01, tenant;dur=0.09, replica_open;dur=0.24, route;dur=0.59, local_begin;dur=7.48, local_allowance;dur=0.01, local_native;dur=0.16, local_execute;dur=0.18, ack;dur=17.49, local_capture;dur=0.33, local_hlc;dur=0.00, local_seal;dur=0.08, push_publish;dur=2.03, push_hold;dur=11.59, local_commit;dur=3.37, execute;dur=0.07, app;dur=29.20;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMHVCFDVSZ73APABA9EF8D",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMHV91W95QFSEZMTCSM67W",
    "n": 2,
    "repeat": 1,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:56:40.823796+00:00",
    "completed_at": "2026-11-05T19:56:41.043793+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 219.981,
    "response_received_at": "2026-11-05T19:56:41.043598+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.13, auth;dur=0.40, home_lookup;dur=1.43, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.14, replica_open;dur=0.15, route;dur=0.72, local_begin;dur=9.01, local_allowance;dur=0.01, local_native;dur=0.17, local_execute;dur=0.22, ack;dur=11.60, local_capture;dur=0.32, local_hlc;dur=0.00, local_seal;dur=0.06, push_publish;dur=0.30, push_hold;dur=8.11, local_commit;dur=2.37, execute;dur=0.10, app;dur=23.96;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMHW35XWHXY6ZR677091G1",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMHVZQGFGY0ACEWAMKKQZQ",
    "n": 3,
    "repeat": 1,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:56:41.544072+00:00",
    "completed_at": "2026-11-05T19:56:41.816335+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 272.244,
    "response_received_at": "2026-11-05T19:56:41.816095+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.17, auth;dur=0.31, home_lookup;dur=13.77, qos;dur=3.16, project;dur=0.02, placement;dur=0.01, tenant;dur=9.12, replica_open;dur=4.13, route;dur=0.93, local_begin;dur=13.20, local_allowance;dur=0.01, local_native;dur=0.17, local_execute;dur=0.20, ack;dur=26.81, local_capture;dur=0.24, local_hlc;dur=0.00, local_seal;dur=0.08, push_publish;dur=2.47, push_hold;dur=17.07, local_commit;dur=5.61, execute;dur=0.09, app;dur=71.91;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMHWSQH3JN6M31QRCDGMR7",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMHWP8RPJB6MNFXN8DPVNZ",
    "n": 4,
    "repeat": 1,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:56:42.316608+00:00",
    "completed_at": "2026-11-05T19:56:42.544552+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 227.927,
    "response_received_at": "2026-11-05T19:56:42.544165+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.07, auth;dur=0.24, home_lookup;dur=4.67, qos;dur=1.60, project;dur=0.02, placement;dur=0.01, tenant;dur=5.29, replica_open;dur=3.12, route;dur=0.69, local_begin;dur=1.29, local_allowance;dur=0.01, local_native;dur=0.10, local_execute;dur=0.13, ack;dur=13.59, local_capture;dur=0.27, local_hlc;dur=0.00, local_seal;dur=0.09, push_publish;dur=1.37, push_hold;dur=7.58, local_commit;dur=4.19, execute;dur=0.13, app;dur=30.85;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMHXHT9ZPJC26MWDNSQ1D1",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMHXECMJCRJPX30N4179GK",
    "n": 5,
    "repeat": 1,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:56:43.044828+00:00",
    "completed_at": "2026-11-05T19:56:44.533898+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 1489.052,
    "response_received_at": "2026-11-05T19:56:44.533577+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.34, home_lookup;dur=0.84, qos;dur=0.03, project;dur=0.01, placement;dur=0.01, tenant;dur=0.14, replica_open;dur=1265.87, route;dur=1.70, local_begin;dur=11.67, local_allowance;dur=1.20, local_native;dur=0.24, local_execute;dur=1.45, ack;dur=10.62, local_capture;dur=0.29, local_hlc;dur=0.00, local_seal;dur=0.07, push_publish;dur=0.69, push_hold;dur=6.20, local_commit;dur=3.06, execute;dur=0.10, app;dur=1292.92;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMHY8J6DYVKBEJ67G1EMTM",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMHY540G60K4P5PKDD4Q94",
    "n": 6,
    "repeat": 1,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:56:45.034129+00:00",
    "completed_at": "2026-11-05T19:56:45.266712+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 232.564,
    "response_received_at": "2026-11-05T19:56:45.266508+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.18, auth;dur=0.39, home_lookup;dur=4.01, qos;dur=0.67, project;dur=0.02, placement;dur=0.01, tenant;dur=6.76, replica_open;dur=0.15, route;dur=0.64, local_begin;dur=9.53, local_allowance;dur=0.01, local_native;dur=0.17, local_execute;dur=0.20, ack;dur=14.81, local_capture;dur=0.28, local_hlc;dur=0.00, local_seal;dur=0.09, push_publish;dur=0.73, push_hold;dur=7.18, local_commit;dur=5.77, execute;dur=0.09, app;dur=37.47;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMJ06Q0KRWF7CSN4N9JES1",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMJ03A6KWW51Y6SNY8QCAZ",
    "n": 7,
    "repeat": 1,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:56:45.766928+00:00",
    "completed_at": "2026-11-05T19:56:45.984667+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 217.717,
    "response_received_at": "2026-11-05T19:56:45.984473+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.11, auth;dur=0.35, home_lookup;dur=0.93, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.53, replica_open;dur=0.15, route;dur=0.78, local_begin;dur=6.47, local_allowance;dur=0.01, local_native;dur=0.18, local_execute;dur=0.23, ack;dur=10.68, local_capture;dur=0.31, local_hlc;dur=0.00, local_seal;dur=0.09, push_publish;dur=1.51, push_hold;dur=5.53, local_commit;dur=3.16, execute;dur=0.09, app;dur=20.38;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMJ0XMS961XWSF945MR4NW",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMJ0T6MGE11YN5VZ7JN1TV",
    "n": 8,
    "repeat": 1,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:56:46.484923+00:00",
    "completed_at": "2026-11-05T19:56:46.700713+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 215.774,
    "response_received_at": "2026-11-05T19:56:46.700501+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.40, home_lookup;dur=0.86, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.13, replica_open;dur=0.16, route;dur=0.56, local_begin;dur=6.15, local_allowance;dur=0.01, local_native;dur=0.19, local_execute;dur=0.23, ack;dur=11.43, local_capture;dur=0.85, local_hlc;dur=0.00, local_seal;dur=0.36, push_publish;dur=0.72, push_hold;dur=6.41, local_commit;dur=2.91, execute;dur=0.07, app;dur=20.16;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMJ1M26Q65FKCQ3MYSXDGC",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMJ1GMTYGB0SDR6HBKA6F0",
    "n": 9,
    "repeat": 1,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:56:47.201006+00:00",
    "completed_at": "2026-11-05T19:56:47.427562+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 226.538,
    "response_received_at": "2026-11-05T19:56:47.427351+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.08, auth;dur=0.35, home_lookup;dur=3.51, qos;dur=0.02, project;dur=0.01, placement;dur=0.01, tenant;dur=0.12, replica_open;dur=0.13, route;dur=0.69, local_begin;dur=8.17, local_allowance;dur=0.01, local_native;dur=0.24, local_execute;dur=0.26, ack;dur=16.30, local_capture;dur=0.29, local_hlc;dur=0.00, local_seal;dur=0.07, push_publish;dur=0.34, push_hold;dur=11.78, local_commit;dur=3.74, execute;dur=0.10, app;dur=29.76;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMJ2AE93SQ0HGGGQQ7CAX4",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMJ270C4CYTE6BCE6M6K7E",
    "n": 10,
    "repeat": 1,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:56:40.334032+00:00",
    "completed_at": "2026-11-05T19:56:47.737813+00:00",
    "kind": "move_rerun_home",
    "region": "gru",
    "ok": true,
    "ms": 7403.772,
    "response_received_at": "2026-11-05T19:56:47.737468+00:00",
    "status": 200,
    "headers": {
      "x-request-id": "01M6PMHVQY8A3A45RZHPHZW9NY",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "repeat": 1,
    "target_home": "fra",
    "value": {
      "id": "01M6PK77TX4H0B1D3GQZ73XZCF",
      "project_id": "01M6PENV06CRPSJYDDRZTF75R4",
      "name": "milestone-b-postfix",
      "created_at": "2026-11-05T19:33:23.9337227+00:00",
      "region": "gru",
      "home_region": "fra",
      "branched_from": null,
      "raw": {
        "id": "01M6PK77TX4H0B1D3GQZ73XZCF",
        "projectId": "01M6PENV06CRPSJYDDRZTF75R4",
        "name": "milestone-b-postfix",
        "branchedFrom": null,
        "createdAt": "2026-11-05T19:33:23.9337227+00:00",
        "region": "gru",
        "homeRegion": "fra",
        "replicaRegions": [],
        "allRegions": true,
        "previousName": null,
        "conflictOf": null,
        "expiresAt": null
      },
      "previous_name": null,
      "conflict_of": null,
      "expires_at": null,
      "replica_regions": [],
      "all_regions": true
    }
  },
  {
    "started_at": "2026-11-05T19:56:47.927748+00:00",
    "completed_at": "2026-11-05T19:56:48.180923+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 253.153,
    "response_received_at": "2026-11-05T19:56:48.178195+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=3.44, home_lookup;dur=5.14, qos;dur=0.03, project;dur=0.03, placement;dur=0.01, tenant;dur=0.13, replica_open;dur=1.12, route;dur=1.48, local_begin;dur=8.90, local_allowance;dur=0.01, local_native;dur=0.16, local_execute;dur=0.18, ack;dur=14.95, local_capture;dur=0.62, local_hlc;dur=0.00, local_seal;dur=0.42, push_publish;dur=1.86, push_hold;dur=8.48, local_commit;dur=3.49, execute;dur=0.06, app;dur=35.60;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMJ315CJX9QMQ7B08N1SBN",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMJ2XQMTFJ99TR4YYHQHPG",
    "n": 11,
    "repeat": 1,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:56:48.681088+00:00",
    "completed_at": "2026-11-05T19:56:48.898295+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 217.19,
    "response_received_at": "2026-11-05T19:56:48.898054+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.11, auth;dur=0.35, home_lookup;dur=0.88, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.12, replica_open;dur=0.20, route;dur=0.39, local_begin;dur=7.25, local_allowance;dur=0.01, local_native;dur=0.18, local_execute;dur=0.21, ack;dur=11.00, local_capture;dur=0.33, local_hlc;dur=0.00, local_seal;dur=0.21, push_publish;dur=1.83, push_hold;dur=5.76, local_commit;dur=2.66, execute;dur=0.06, app;dur=20.62;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMJ3RQE7KK0EF72N7S3HPV",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMJ3N9Y9MNBCC7T3742TSH",
    "n": 12,
    "repeat": 1,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:56:49.398574+00:00",
    "completed_at": "2026-11-05T19:56:49.642975+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 244.357,
    "response_received_at": "2026-11-05T19:56:49.642266+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.07, auth;dur=0.30, home_lookup;dur=5.58, qos;dur=2.06, project;dur=0.02, placement;dur=0.01, tenant;dur=12.07, replica_open;dur=2.72, route;dur=0.65, local_begin;dur=7.06, local_allowance;dur=0.01, local_native;dur=0.21, local_execute;dur=0.24, ack;dur=14.36, local_capture;dur=0.24, local_hlc;dur=0.00, local_seal;dur=0.07, push_publish;dur=0.55, push_hold;dur=10.02, local_commit;dur=3.37, execute;dur=0.19, app;dur=45.35;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMJ4F6BSZB47X879YM1NR9",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMJ4BPZJ03YHH86YXPCPR1",
    "n": 13,
    "repeat": 1,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:56:49.667337+00:00",
    "completed_at": "2026-11-05T19:56:50.107423+00:00",
    "kind": "move_rerun_inventory",
    "region": "gru",
    "ok": true,
    "ms": 440.07,
    "response_received_at": "2026-11-05T19:56:50.071115+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.10, auth;dur=7.98, home_lookup;dur=5.91, qos;dur=2.15, project;dur=0.03, placement;dur=1.59, tenant;dur=8.03, replica_open;dur=7.41, execute;dur=6.20, app;dur=39.41;desc=\"gru\"",
      "x-request-id": "01M6PMJ4ZFDVXVSQTXTS250745",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "repeat": 1,
    "row_count": 972,
    "distinct_ids": 972,
    "canonical_inventory_sha256": "28c57d8616aa11be1ad738299e6432d5bee0f5765fb0d754438a10257305ab69"
  },
  {
    "kind": "move_rerun_comparison",
    "repeat": 1,
    "region": "gru",
    "expected": 972,
    "accepted_writes": 14,
    "attempted_writes": 14,
    "rows": 972,
    "distinct": 972,
    "missing": [],
    "unexpected": [],
    "changed": []
  },
  {
    "started_at": "2026-11-05T19:56:50.109978+00:00",
    "completed_at": "2026-11-05T19:56:50.133623+00:00",
    "kind": "move_rerun_metadata",
    "region": "gru",
    "ok": true,
    "ms": 23.634,
    "response_received_at": "2026-11-05T19:56:50.133217+00:00",
    "status": 200,
    "headers": {
      "x-request-id": "01M6PMJ52HJM1HYR3X49RZG4AF",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "repeat": 1,
    "value": {
      "id": "01M6PK77TX4H0B1D3GQZ73XZCF",
      "project_id": "01M6PENV06CRPSJYDDRZTF75R4",
      "name": "milestone-b-postfix",
      "created_at": "2026-11-05T19:33:23.9337227+00:00",
      "region": "gru",
      "home_region": "fra",
      "branched_from": null,
      "raw": {
        "id": "01M6PK77TX4H0B1D3GQZ73XZCF",
        "projectId": "01M6PENV06CRPSJYDDRZTF75R4",
        "name": "milestone-b-postfix",
        "branchedFrom": null,
        "createdAt": "2026-11-05T19:33:23.9337227+00:00",
        "region": "gru",
        "homeRegion": "fra",
        "replicaRegions": [],
        "allRegions": true,
        "previousName": null,
        "conflictOf": null,
        "expiresAt": null
      },
      "previous_name": null,
      "conflict_of": null,
      "expires_at": null,
      "replica_regions": [],
      "all_regions": true
    }
  },
  {
    "started_at": "2026-11-05T19:56:50.155877+00:00",
    "completed_at": "2026-11-05T19:56:51.544745+00:00",
    "kind": "move_rerun_inventory",
    "region": "sjc",
    "ok": true,
    "ms": 1388.848,
    "response_received_at": "2026-11-05T19:56:51.157393+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=181.42, home_lookup;dur=2.53, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.10, replica_open;dur=0.18, execute;dur=2.77, app;dur=187.20;desc=\"sjc\"",
      "x-request-id": "01M6PMJ5T4TT4XJKCZ889AVRKZ",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "repeat": 1,
    "row_count": 972,
    "distinct_ids": 972,
    "canonical_inventory_sha256": "28c57d8616aa11be1ad738299e6432d5bee0f5765fb0d754438a10257305ab69"
  },
  {
    "kind": "move_rerun_comparison",
    "repeat": 1,
    "region": "sjc",
    "expected": 972,
    "accepted_writes": 14,
    "attempted_writes": 14,
    "rows": 972,
    "distinct": 972,
    "missing": [],
    "unexpected": [],
    "changed": []
  },
  {
    "started_at": "2026-11-05T19:56:51.550533+00:00",
    "completed_at": "2026-11-05T19:56:51.784093+00:00",
    "kind": "move_rerun_metadata",
    "region": "sjc",
    "ok": true,
    "ms": 233.537,
    "response_received_at": "2026-11-05T19:56:51.783258+00:00",
    "status": 200,
    "headers": {
      "x-request-id": "01M6PMJ6JD8CW9A4YKTEHCHGAR",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "repeat": 1,
    "value": {
      "id": "01M6PK77TX4H0B1D3GQZ73XZCF",
      "project_id": "01M6PENV06CRPSJYDDRZTF75R4",
      "name": "milestone-b-postfix",
      "created_at": "2026-11-05T19:33:24.7695968+00:00",
      "region": "sjc",
      "home_region": "fra",
      "branched_from": null,
      "raw": {
        "id": "01M6PK77TX4H0B1D3GQZ73XZCF",
        "projectId": "01M6PENV06CRPSJYDDRZTF75R4",
        "name": "milestone-b-postfix",
        "branchedFrom": null,
        "createdAt": "2026-11-05T19:33:24.7695968+00:00",
        "region": "sjc",
        "homeRegion": "fra",
        "replicaRegions": [],
        "allRegions": true,
        "previousName": null,
        "conflictOf": null,
        "expiresAt": null
      },
      "previous_name": null,
      "conflict_of": null,
      "expires_at": null,
      "replica_regions": [],
      "all_regions": true
    }
  },
  {
    "started_at": "2026-11-05T19:56:51.809426+00:00",
    "completed_at": "2026-11-05T19:56:53.236859+00:00",
    "kind": "move_rerun_inventory",
    "region": "fra",
    "ok": true,
    "ms": 1427.415,
    "response_received_at": "2026-11-05T19:56:52.818463+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.13, auth;dur=187.02, home_lookup;dur=6.50, qos;dur=3.79, project;dur=0.03, placement;dur=0.01, tenant;dur=2.14, execute;dur=5.64, app;dur=205.27;desc=\"fra\"",
      "x-request-id": "01M6PMJ7D4DKEP6D46J2SRJ5EV",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "repeat": 1,
    "row_count": 972,
    "distinct_ids": 972,
    "canonical_inventory_sha256": "28c57d8616aa11be1ad738299e6432d5bee0f5765fb0d754438a10257305ab69"
  },
  {
    "kind": "move_rerun_comparison",
    "repeat": 1,
    "region": "fra",
    "expected": 972,
    "accepted_writes": 14,
    "attempted_writes": 14,
    "rows": 972,
    "distinct": 972,
    "missing": [],
    "unexpected": [],
    "changed": []
  },
  {
    "started_at": "2026-11-05T19:56:53.239311+00:00",
    "completed_at": "2026-11-05T19:56:53.458582+00:00",
    "kind": "move_rerun_metadata",
    "region": "fra",
    "ok": true,
    "ms": 219.264,
    "response_received_at": "2026-11-05T19:56:53.458321+00:00",
    "status": 200,
    "headers": {
      "x-request-id": "01M6PMJ87ATN1FSNVTT1TTPEAN",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "repeat": 1,
    "value": {
      "id": "01M6PK77TX4H0B1D3GQZ73XZCF",
      "project_id": "01M6PENV06CRPSJYDDRZTF75R4",
      "name": "milestone-b-postfix",
      "created_at": "2026-11-05T19:33:24.6609603+00:00",
      "region": "fra",
      "home_region": "fra",
      "branched_from": null,
      "raw": {
        "id": "01M6PK77TX4H0B1D3GQZ73XZCF",
        "projectId": "01M6PENV06CRPSJYDDRZTF75R4",
        "name": "milestone-b-postfix",
        "branchedFrom": null,
        "createdAt": "2026-11-05T19:33:24.6609603+00:00",
        "region": "fra",
        "homeRegion": "fra",
        "replicaRegions": [
          "gru",
          "sjc"
        ],
        "allRegions": true,
        "previousName": null,
        "conflictOf": null,
        "expiresAt": null
      },
      "previous_name": null,
      "conflict_of": null,
      "expires_at": null,
      "replica_regions": [
        "gru",
        "sjc"
      ],
      "all_regions": true
    }
  },
  {
    "started_at": "2026-11-05T19:56:53.473102+00:00",
    "completed_at": "2026-11-05T19:56:54.424979+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 951.861,
    "response_received_at": "2026-11-05T19:56:54.424271+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=173.68, home_lookup;dur=3.19, qos;dur=1.32, project;dur=0.04, placement;dur=0.03, tenant;dur=4.09, replica_open;dur=1.39, route;dur=0.46, local_begin;dur=7.97, local_allowance;dur=0.01, local_native;dur=0.76, local_execute;dur=0.80, ack;dur=11.77, local_capture;dur=0.79, local_hlc;dur=0.00, local_seal;dur=0.47, push_publish;dur=1.14, push_hold;dur=6.61, local_commit;dur=2.64, execute;dur=0.10, app;dur=204.96;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMJ8ZQF7CQHQF82FJGEFWK",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMJ8B1NDK5J1D6S79V0A7X",
    "n": 0,
    "repeat": 2,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:56:54.925157+00:00",
    "completed_at": "2026-11-05T19:56:55.146053+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 220.879,
    "response_received_at": "2026-11-05T19:56:55.145809+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.10, auth;dur=0.28, home_lookup;dur=1.89, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.11, replica_open;dur=0.79, route;dur=2.43, local_begin;dur=9.59, local_allowance;dur=0.01, local_native;dur=0.18, local_execute;dur=0.20, ack;dur=11.96, local_capture;dur=0.55, local_hlc;dur=0.00, local_seal;dur=0.08, push_publish;dur=0.85, push_hold;dur=7.33, local_commit;dur=3.05, execute;dur=0.08, app;dur=27.74;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMJ9VTHGEC3JH3SWESZ1X2",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMJ9RDQ7SP88A83CS58A7K",
    "n": 1,
    "repeat": 2,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:56:55.646204+00:00",
    "completed_at": "2026-11-05T19:56:55.858070+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 211.85,
    "response_received_at": "2026-11-05T19:56:55.857595+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.11, auth;dur=0.30, home_lookup;dur=0.86, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.11, replica_open;dur=0.16, route;dur=0.57, local_begin;dur=5.90, local_allowance;dur=0.01, local_native;dur=0.26, local_execute;dur=0.31, ack;dur=10.38, local_capture;dur=0.32, local_hlc;dur=0.00, local_seal;dur=0.08, push_publish;dur=0.84, push_hold;dur=6.02, local_commit;dur=2.72, execute;dur=0.09, app;dur=18.84;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMJAJA2N1QVGRVPJGR8TRW",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMJAEYHTY7JYJRWTZYBK7Y",
    "n": 2,
    "repeat": 2,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:56:56.358247+00:00",
    "completed_at": "2026-11-05T19:56:56.572045+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 213.782,
    "response_received_at": "2026-11-05T19:56:56.571820+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.39, auth;dur=0.33, home_lookup;dur=0.67, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.11, replica_open;dur=0.14, route;dur=0.52, local_begin;dur=5.55, local_allowance;dur=0.01, local_native;dur=0.18, local_execute;dur=0.21, ack;dur=11.56, local_capture;dur=0.32, local_hlc;dur=0.00, local_seal;dur=0.09, push_publish;dur=1.13, push_hold;dur=5.96, local_commit;dur=3.97, execute;dur=0.75, app;dur=20.30;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMJB8JNDD1QKXC6BERSHMZ",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMJB56J6964F7W508GHD1A",
    "n": 3,
    "repeat": 2,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:56:57.072246+00:00",
    "completed_at": "2026-11-05T19:56:57.298866+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 226.588,
    "response_received_at": "2026-11-05T19:56:57.298598+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.29, home_lookup;dur=3.12, qos;dur=1.09, project;dur=0.02, placement;dur=0.01, tenant;dur=5.09, replica_open;dur=1.27, route;dur=0.51, local_begin;dur=9.70, local_allowance;dur=0.01, local_native;dur=0.55, local_execute;dur=0.59, ack;dur=11.49, local_capture;dur=0.79, local_hlc;dur=0.00, local_seal;dur=0.08, push_publish;dur=0.53, push_hold;dur=7.12, local_commit;dur=2.86, execute;dur=0.09, app;dur=33.40;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMJBYXJ3TRF4B0WP80DG2X",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMJBVGQWTTWXY84PGZXPYM",
    "n": 4,
    "repeat": 2,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:56:57.799077+00:00",
    "completed_at": "2026-11-05T19:56:58.011902+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 212.806,
    "response_received_at": "2026-11-05T19:56:58.011699+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.36, home_lookup;dur=0.76, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.13, replica_open;dur=0.16, route;dur=0.56, local_begin;dur=6.40, local_allowance;dur=0.01, local_native;dur=0.19, local_execute;dur=0.21, ack;dur=11.08, local_capture;dur=0.36, local_hlc;dur=0.00, local_seal;dur=0.09, push_publish;dur=0.72, push_hold;dur=6.25, local_commit;dur=3.29, execute;dur=0.42, app;dur=20.28;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMJCNKFK1KCB7BWD1A6EJD",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMJCJ7AJ4DPZKDH5874F75",
    "n": 5,
    "repeat": 2,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:56:58.512099+00:00",
    "completed_at": "2026-11-05T19:56:58.723496+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 211.372,
    "response_received_at": "2026-11-05T19:56:58.722465+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.11, auth;dur=0.35, home_lookup;dur=0.79, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.12, replica_open;dur=0.16, route;dur=0.69, local_begin;dur=0.56, local_allowance;dur=0.01, local_native;dur=0.25, local_execute;dur=0.27, ack;dur=13.49, local_capture;dur=2.37, local_hlc;dur=0.00, local_seal;dur=0.09, push_publish;dur=1.66, push_hold;dur=6.52, local_commit;dur=2.77, execute;dur=0.08, app;dur=16.70;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMJDBXVDHR94CN9QWPSB1K",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMJD8GR2AGN9T44NA8TCCH",
    "n": 6,
    "repeat": 2,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:56:59.223754+00:00",
    "completed_at": "2026-11-05T19:56:59.425580+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": false,
    "ms": 201.805,
    "response_received_at": "2026-11-05T19:56:59.425183+00:00",
    "status": 400,
    "headers": {
      "server-timing": "admission;dur=0.11, auth;dur=0.34, home_lookup;dur=4.16, qos;dur=0.70, project;dur=0.02, placement;dur=0.01, tenant;dur=3.88, execute;dur=0.21, app;dur=9.45;desc=\"sjc\"",
      "x-request-id": "01M6PMJE24DEMZDJ63M4MDZ7CH",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMJDYQKVTJ0B1EB6VTKVSZ",
    "n": 7,
    "repeat": 2,
    "error": {
      "type": "SqlError",
      "status": 400,
      "code": "sql_error",
      "request_id": "01M6PMJE24DEMZDJ63M4MDZ7CH",
      "message": "the database does not exist",
      "sqlite_code": null,
      "sqlstate": "0A000",
      "statement": 0
    }
  },
  {
    "started_at": "2026-11-05T19:56:59.925846+00:00",
    "completed_at": "2026-11-05T19:57:01.036191+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 1110.329,
    "response_received_at": "2026-11-05T19:57:01.035944+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.20, auth;dur=0.41, home_lookup;dur=1.74, qos;dur=0.02, project;dur=0.01, placement;dur=0.01, tenant;dur=0.09, replica_open;dur=898.72, route;dur=0.70, local_begin;dur=5.25, local_allowance;dur=0.01, local_native;dur=0.22, local_execute;dur=0.25, ack;dur=10.46, local_capture;dur=0.28, local_hlc;dur=0.00, local_seal;dur=0.19, push_publish;dur=0.71, push_hold;dur=6.26, local_commit;dur=2.93, execute;dur=0.08, app;dur=917.96;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMJER2GH3H04Y8KQC4DGCK",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMJEMN00185G35YM2N73P1",
    "n": 8,
    "repeat": 2,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:57:01.536369+00:00",
    "completed_at": "2026-11-05T19:57:01.958288+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 421.9,
    "response_received_at": "2026-11-05T19:57:01.957979+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.17, auth;dur=175.82, home_lookup;dur=4.95, qos;dur=9.63, project;dur=0.03, placement;dur=0.35, tenant;dur=11.89, replica_open;dur=1.52, route;dur=1.06, local_begin;dur=9.92, local_allowance;dur=0.01, local_native;dur=0.19, local_execute;dur=0.21, ack;dur=12.86, local_capture;dur=0.27, local_hlc;dur=0.00, local_seal;dur=0.10, push_publish;dur=2.11, push_hold;dur=6.91, local_commit;dur=2.62, execute;dur=0.34, app;dur=228.76;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMJGADQYSYT4X1XEWYMPP8",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMJG70T2T635ANPMPTVXER",
    "n": 9,
    "repeat": 2,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:57:02.458548+00:00",
    "completed_at": "2026-11-05T19:57:02.672310+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 213.736,
    "response_received_at": "2026-11-05T19:57:02.672055+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.30, home_lookup;dur=0.79, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.15, replica_open;dur=0.16, route;dur=0.51, local_begin;dur=6.29, local_allowance;dur=0.01, local_native;dur=0.19, local_execute;dur=0.21, ack;dur=12.48, local_capture;dur=0.29, local_hlc;dur=0.00, local_seal;dur=0.08, push_publish;dur=0.86, push_hold;dur=7.07, local_commit;dur=4.07, execute;dur=0.09, app;dur=21.16;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMJH76KAD2F8WR539F4RDQ",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMJH3TBF2883R7041HBRET",
    "n": 10,
    "repeat": 2,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:57:03.172487+00:00",
    "completed_at": "2026-11-05T19:57:03.385971+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 213.467,
    "response_received_at": "2026-11-05T19:57:03.385722+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.11, auth;dur=0.34, home_lookup;dur=0.85, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.12, replica_open;dur=0.18, route;dur=0.63, local_begin;dur=6.22, local_allowance;dur=0.01, local_native;dur=0.22, local_execute;dur=0.25, ack;dur=11.57, local_capture;dur=0.26, local_hlc;dur=0.00, local_seal;dur=0.10, push_publish;dur=0.94, push_hold;dur=6.35, local_commit;dur=3.48, execute;dur=0.37, app;dur=20.70;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMJHXGWJ4W4E5BVYGF7D14",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMJHT4QKGKC7PT1JG2E4WZ",
    "n": 11,
    "repeat": 2,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:56:55.869535+00:00",
    "completed_at": "2026-11-05T19:57:03.397107+00:00",
    "kind": "move_rerun_home",
    "region": "fra",
    "ok": true,
    "ms": 7527.563,
    "response_received_at": "2026-11-05T19:57:03.396704+00:00",
    "status": 200,
    "headers": {
      "x-request-id": "01M6PMJBC194S5NRV8G1VGTVTT",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "repeat": 2,
    "target_home": "gru",
    "value": {
      "id": "01M6PK77TX4H0B1D3GQZ73XZCF",
      "project_id": "01M6PENV06CRPSJYDDRZTF75R4",
      "name": "milestone-b-postfix",
      "created_at": "2026-11-05T19:33:24.6609603+00:00",
      "region": "fra",
      "home_region": "gru",
      "branched_from": null,
      "raw": {
        "id": "01M6PK77TX4H0B1D3GQZ73XZCF",
        "projectId": "01M6PENV06CRPSJYDDRZTF75R4",
        "name": "milestone-b-postfix",
        "branchedFrom": null,
        "createdAt": "2026-11-05T19:33:24.6609603+00:00",
        "region": "fra",
        "homeRegion": "gru",
        "replicaRegions": [
          "fra",
          "sjc"
        ],
        "allRegions": true,
        "previousName": null,
        "conflictOf": null,
        "expiresAt": null
      },
      "previous_name": null,
      "conflict_of": null,
      "expires_at": null,
      "replica_regions": [
        "fra",
        "sjc"
      ],
      "all_regions": true
    }
  },
  {
    "started_at": "2026-11-05T19:57:03.886153+00:00",
    "completed_at": "2026-11-05T19:57:04.100434+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 214.265,
    "response_received_at": "2026-11-05T19:57:04.100199+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.37, home_lookup;dur=2.71, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.12, replica_open;dur=0.17, route;dur=0.64, local_begin;dur=6.04, local_allowance;dur=0.01, local_native;dur=0.20, local_execute;dur=0.23, ack;dur=10.68, local_capture;dur=0.33, local_hlc;dur=0.00, local_seal;dur=0.10, push_publish;dur=0.53, push_hold;dur=7.04, local_commit;dur=2.44, execute;dur=0.17, app;dur=21.33;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMJJKT5T3S2KBQB1T53HE7",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMJJGEFWX7TZAATJP2BHBC",
    "n": 12,
    "repeat": 2,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:57:04.600596+00:00",
    "completed_at": "2026-11-05T19:57:04.829592+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 228.977,
    "response_received_at": "2026-11-05T19:57:04.829368+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.62, home_lookup;dur=5.57, qos;dur=1.60, project;dur=0.03, placement;dur=0.01, tenant;dur=5.59, replica_open;dur=0.17, route;dur=0.51, local_begin;dur=5.58, local_allowance;dur=0.01, local_native;dur=0.19, local_execute;dur=0.21, ack;dur=11.51, local_capture;dur=0.71, local_hlc;dur=0.00, local_seal;dur=0.09, push_publish;dur=0.54, push_hold;dur=6.86, local_commit;dur=2.82, execute;dur=0.37, app;dur=31.89;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMJKA5ASZJ13JPYM6KXQSD",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMJK6RHN97G6GATYVM2515",
    "n": 13,
    "repeat": 2,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:57:05.329844+00:00",
    "completed_at": "2026-11-05T19:57:05.554926+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 225.068,
    "response_received_at": "2026-11-05T19:57:05.554723+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.08, auth;dur=0.42, home_lookup;dur=2.95, qos;dur=0.05, project;dur=0.02, placement;dur=0.06, tenant;dur=0.13, replica_open;dur=9.93, route;dur=0.77, local_begin;dur=5.80, local_allowance;dur=0.05, local_native;dur=0.17, local_execute;dur=0.24, ack;dur=11.02, local_capture;dur=0.28, local_hlc;dur=0.00, local_seal;dur=0.09, push_publish;dur=0.47, push_hold;dur=7.41, local_commit;dur=2.68, execute;dur=0.09, app;dur=31.57;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMJM0XYW0W851SPR1MMBB5",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMJKXHTQSTZJD27E209QMS",
    "n": 14,
    "repeat": 2,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:57:05.566209+00:00",
    "completed_at": "2026-11-05T19:57:05.904193+00:00",
    "kind": "move_rerun_inventory",
    "region": "gru",
    "ok": true,
    "ms": 337.967,
    "response_received_at": "2026-11-05T19:57:05.856612+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.13, auth;dur=12.92, home_lookup;dur=4.85, qos;dur=2.80, project;dur=0.03, placement;dur=0.01, tenant;dur=0.11, execute;dur=6.79, app;dur=27.66;desc=\"gru\"",
      "x-request-id": "01M6PMJMCPZ809485A33JHBXDX",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "repeat": 2,
    "row_count": 986,
    "distinct_ids": 986,
    "canonical_inventory_sha256": "b4f132ecaa106e9bc7b1203be1652132a3a06d17c5b125463f411dde1c3c5c43"
  },
  {
    "kind": "move_rerun_comparison",
    "repeat": 2,
    "region": "gru",
    "expected": 986,
    "accepted_writes": 14,
    "attempted_writes": 15,
    "rows": 986,
    "distinct": 986,
    "missing": [],
    "unexpected": [],
    "changed": []
  },
  {
    "started_at": "2026-11-05T19:57:05.907610+00:00",
    "completed_at": "2026-11-05T19:57:05.932767+00:00",
    "kind": "move_rerun_metadata",
    "region": "gru",
    "ok": true,
    "ms": 25.148,
    "response_received_at": "2026-11-05T19:57:05.932498+00:00",
    "status": 200,
    "headers": {
      "x-request-id": "01M6PMJMG74HZ3N61MVTZW4PXA",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "repeat": 2,
    "value": {
      "id": "01M6PK77TX4H0B1D3GQZ73XZCF",
      "project_id": "01M6PENV06CRPSJYDDRZTF75R4",
      "name": "milestone-b-postfix",
      "created_at": "2026-11-05T19:33:23.9337227+00:00",
      "region": "gru",
      "home_region": "gru",
      "branched_from": null,
      "raw": {
        "id": "01M6PK77TX4H0B1D3GQZ73XZCF",
        "projectId": "01M6PENV06CRPSJYDDRZTF75R4",
        "name": "milestone-b-postfix",
        "branchedFrom": null,
        "createdAt": "2026-11-05T19:33:23.9337227+00:00",
        "region": "gru",
        "homeRegion": "gru",
        "replicaRegions": [
          "fra",
          "sjc"
        ],
        "allRegions": true,
        "previousName": null,
        "conflictOf": null,
        "expiresAt": null
      },
      "previous_name": null,
      "conflict_of": null,
      "expires_at": null,
      "replica_regions": [
        "fra",
        "sjc"
      ],
      "all_regions": true
    }
  },
  {
    "started_at": "2026-11-05T19:57:05.943907+00:00",
    "completed_at": "2026-11-05T19:57:07.244762+00:00",
    "kind": "move_rerun_inventory",
    "region": "sjc",
    "ok": true,
    "ms": 1300.842,
    "response_received_at": "2026-11-05T19:57:06.859415+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.11, auth;dur=177.79, home_lookup;dur=0.78, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.10, replica_open;dur=0.19, execute;dur=2.49, app;dur=181.52;desc=\"sjc\"",
      "x-request-id": "01M6PMJN50H8949X8ZKQ6QKPDQ",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "repeat": 2,
    "row_count": 986,
    "distinct_ids": 986,
    "canonical_inventory_sha256": "b4f132ecaa106e9bc7b1203be1652132a3a06d17c5b125463f411dde1c3c5c43"
  },
  {
    "kind": "move_rerun_comparison",
    "repeat": 2,
    "region": "sjc",
    "expected": 986,
    "accepted_writes": 14,
    "attempted_writes": 15,
    "rows": 986,
    "distinct": 986,
    "missing": [],
    "unexpected": [],
    "changed": []
  },
  {
    "started_at": "2026-11-05T19:57:07.247950+00:00",
    "completed_at": "2026-11-05T19:57:07.445702+00:00",
    "kind": "move_rerun_metadata",
    "region": "sjc",
    "ok": true,
    "ms": 197.742,
    "response_received_at": "2026-11-05T19:57:07.445450+00:00",
    "status": 200,
    "headers": {
      "x-request-id": "01M6PMJNWW4FB88HV6KM15B7H9",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "repeat": 2,
    "value": {
      "id": "01M6PK77TX4H0B1D3GQZ73XZCF",
      "project_id": "01M6PENV06CRPSJYDDRZTF75R4",
      "name": "milestone-b-postfix",
      "created_at": "2026-11-05T19:33:24.7695968+00:00",
      "region": "sjc",
      "home_region": "gru",
      "branched_from": null,
      "raw": {
        "id": "01M6PK77TX4H0B1D3GQZ73XZCF",
        "projectId": "01M6PENV06CRPSJYDDRZTF75R4",
        "name": "milestone-b-postfix",
        "branchedFrom": null,
        "createdAt": "2026-11-05T19:33:24.7695968+00:00",
        "region": "sjc",
        "homeRegion": "gru",
        "replicaRegions": [],
        "allRegions": true,
        "previousName": null,
        "conflictOf": null,
        "expiresAt": null
      },
      "previous_name": null,
      "conflict_of": null,
      "expires_at": null,
      "replica_regions": [],
      "all_regions": true
    }
  },
  {
    "started_at": "2026-11-05T19:57:07.456544+00:00",
    "completed_at": "2026-11-05T19:57:08.894203+00:00",
    "kind": "move_rerun_inventory",
    "region": "fra",
    "ok": true,
    "ms": 1437.643,
    "response_received_at": "2026-11-05T19:57:08.469647+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.11, auth;dur=186.04, home_lookup;dur=4.52, qos;dur=1.35, project;dur=0.03, placement;dur=0.02, tenant;dur=6.54, replica_open;dur=4.52, execute;dur=5.06, app;dur=208.20;desc=\"fra\"",
      "x-request-id": "01M6PMJPP53E1PFHPE41AJ3XJD",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "repeat": 2,
    "row_count": 986,
    "distinct_ids": 986,
    "canonical_inventory_sha256": "b4f132ecaa106e9bc7b1203be1652132a3a06d17c5b125463f411dde1c3c5c43"
  },
  {
    "kind": "move_rerun_comparison",
    "repeat": 2,
    "region": "fra",
    "expected": 986,
    "accepted_writes": 14,
    "attempted_writes": 15,
    "rows": 986,
    "distinct": 986,
    "missing": [],
    "unexpected": [],
    "changed": []
  },
  {
    "started_at": "2026-11-05T19:57:08.899622+00:00",
    "completed_at": "2026-11-05T19:57:09.120396+00:00",
    "kind": "move_rerun_metadata",
    "region": "fra",
    "ok": true,
    "ms": 220.76,
    "response_received_at": "2026-11-05T19:57:09.119582+00:00",
    "status": 200,
    "headers": {
      "x-request-id": "01M6PMJQGP2C863ZMDM4QR7PRH",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "repeat": 2,
    "value": {
      "id": "01M6PK77TX4H0B1D3GQZ73XZCF",
      "project_id": "01M6PENV06CRPSJYDDRZTF75R4",
      "name": "milestone-b-postfix",
      "created_at": "2026-11-05T19:33:24.6609603+00:00",
      "region": "fra",
      "home_region": "gru",
      "branched_from": null,
      "raw": {
        "id": "01M6PK77TX4H0B1D3GQZ73XZCF",
        "projectId": "01M6PENV06CRPSJYDDRZTF75R4",
        "name": "milestone-b-postfix",
        "branchedFrom": null,
        "createdAt": "2026-11-05T19:33:24.6609603+00:00",
        "region": "fra",
        "homeRegion": "gru",
        "replicaRegions": [
          "fra",
          "sjc"
        ],
        "allRegions": true,
        "previousName": null,
        "conflictOf": null,
        "expiresAt": null
      },
      "previous_name": null,
      "conflict_of": null,
      "expires_at": null,
      "replica_regions": [
        "fra",
        "sjc"
      ],
      "all_regions": true
    }
  },
  {
    "started_at": "2026-11-05T19:57:09.141613+00:00",
    "completed_at": "2026-11-05T19:57:10.099472+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 957.845,
    "response_received_at": "2026-11-05T19:57:10.098754+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.18, auth;dur=177.97, home_lookup;dur=4.33, qos;dur=1.08, project;dur=0.03, placement;dur=0.01, tenant;dur=3.64, replica_open;dur=1.98, route;dur=0.54, local_begin;dur=7.04, local_allowance;dur=0.01, local_native;dur=0.20, local_execute;dur=0.23, ack;dur=12.54, local_capture;dur=0.29, local_hlc;dur=0.00, local_seal;dur=0.09, push_publish;dur=0.62, push_hold;dur=6.43, local_commit;dur=5.01, execute;dur=0.09, app;dur=209.65;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMJR99E12PGFA033F18RHD",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMJQMNXFCM95NB8VMK2GYM",
    "n": 0,
    "repeat": 3,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:57:10.599849+00:00",
    "completed_at": "2026-11-05T19:57:10.819662+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 219.767,
    "response_received_at": "2026-11-05T19:57:10.818900+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.40, home_lookup;dur=2.89, qos;dur=0.12, project;dur=0.02, placement;dur=0.01, tenant;dur=0.16, replica_open;dur=0.18, route;dur=1.23, local_begin;dur=5.93, local_allowance;dur=0.01, local_native;dur=0.16, local_execute;dur=0.35, ack;dur=10.11, local_capture;dur=0.35, local_hlc;dur=0.00, local_seal;dur=0.12, push_publish;dur=0.58, push_hold;dur=6.36, local_commit;dur=2.60, execute;dur=0.10, app;dur=21.63;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMJS5PW0Q8C0M8DWZW4JC5",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMJS27KVWDNZT72YGVYGNT",
    "n": 1,
    "repeat": 3,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:57:11.320043+00:00",
    "completed_at": "2026-11-05T19:57:11.545678+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 225.587,
    "response_received_at": "2026-11-05T19:57:11.544826+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.11, auth;dur=0.35, home_lookup;dur=2.92, qos;dur=0.02, project;dur=0.01, placement;dur=0.01, tenant;dur=0.31, replica_open;dur=0.63, route;dur=0.86, local_begin;dur=5.54, local_allowance;dur=0.01, local_native;dur=0.17, local_execute;dur=0.20, ack;dur=15.96, local_capture;dur=1.94, local_hlc;dur=0.00, local_seal;dur=0.37, push_publish;dur=1.02, push_hold;dur=7.82, local_commit;dur=4.71, execute;dur=0.09, app;dur=27.03;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMJSW66TN8MDZDPDZMHNBF",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMJSRQ330TN42GMHN7W53Q",
    "n": 2,
    "repeat": 3,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:57:12.046155+00:00",
    "completed_at": "2026-11-05T19:57:12.275731+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 229.532,
    "response_received_at": "2026-11-05T19:57:12.274947+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.35, home_lookup;dur=3.22, qos;dur=1.15, project;dur=0.02, placement;dur=0.01, tenant;dur=3.48, replica_open;dur=1.29, route;dur=0.48, local_begin;dur=7.85, local_allowance;dur=0.01, local_native;dur=0.66, local_execute;dur=0.69, ack;dur=12.30, local_capture;dur=1.22, local_hlc;dur=0.00, local_seal;dur=0.10, push_publish;dur=0.53, push_hold;dur=7.66, local_commit;dur=2.66, execute;dur=0.13, app;dur=31.09;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMJTJWJ3M35Z25115DM8FJ",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMJTFEF4709ZHX24NZA2JW",
    "n": 3,
    "repeat": 3,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:57:12.776170+00:00",
    "completed_at": "2026-11-05T19:57:12.990754+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 214.542,
    "response_received_at": "2026-11-05T19:57:12.990347+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.36, home_lookup;dur=2.69, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.11, replica_open;dur=0.12, route;dur=0.86, local_begin;dur=0.76, local_allowance;dur=0.01, local_native;dur=0.17, local_execute;dur=0.19, ack;dur=10.69, local_capture;dur=0.63, local_hlc;dur=0.00, local_seal;dur=0.10, push_publish;dur=0.69, push_hold;dur=6.25, local_commit;dur=2.89, execute;dur=0.11, app;dur=16.08;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMJV9P4N1QW9YFYMHPZFS4",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMJV68AQTZAGFDZNRVE000",
    "n": 4,
    "repeat": 3,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:57:13.491093+00:00",
    "completed_at": "2026-11-05T19:57:14.860474+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 1369.334,
    "response_received_at": "2026-11-05T19:57:14.859503+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.11, auth;dur=0.33, home_lookup;dur=3.72, qos;dur=0.06, project;dur=0.02, placement;dur=0.01, tenant;dur=3.54, replica_open;dur=1144.35, route;dur=0.74, local_begin;dur=7.23, local_allowance;dur=0.66, local_native;dur=0.18, local_execute;dur=0.86, ack;dur=9.34, local_capture;dur=0.25, local_hlc;dur=0.00, local_seal;dur=0.09, push_publish;dur=0.50, push_hold;dur=6.09, local_commit;dur=2.30, execute;dur=0.10, app;dur=1170.41;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMJW01S0GKYX6KBCBBGWG0",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMJVWJ2J0R62YKBBG4SCE2",
    "n": 5,
    "repeat": 3,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:57:15.360857+00:00",
    "completed_at": "2026-11-05T19:57:15.780227+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 419.327,
    "response_received_at": "2026-11-05T19:57:15.779578+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.13, auth;dur=174.35, home_lookup;dur=10.28, qos;dur=2.64, project;dur=0.03, placement;dur=0.01, tenant;dur=10.13, replica_open;dur=0.51, route;dur=0.98, local_begin;dur=7.20, local_allowance;dur=0.01, local_native;dur=0.15, local_execute;dur=0.18, ack;dur=14.54, local_capture;dur=0.29, local_hlc;dur=0.00, local_seal;dur=0.10, push_publish;dur=1.57, push_hold;dur=9.97, local_commit;dur=2.51, execute;dur=0.10, app;dur=221.09;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMJXTF16EE78951HN6R8WW",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMJXQ001S0P3SD8PY74H80",
    "n": 6,
    "repeat": 3,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:57:16.280566+00:00",
    "completed_at": "2026-11-05T19:57:16.517806+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 237.192,
    "response_received_at": "2026-11-05T19:57:16.517208+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.49, home_lookup;dur=1.83, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.34, replica_open;dur=0.18, route;dur=0.60, local_begin;dur=6.67, local_allowance;dur=0.01, local_native;dur=0.23, local_execute;dur=0.25, ack;dur=29.18, local_capture;dur=0.39, local_hlc;dur=0.00, local_seal;dur=0.09, push_publish;dur=1.01, push_hold;dur=23.89, local_commit;dur=3.69, execute;dur=0.09, app;dur=39.81;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMJYQ7VNM3Q9DFS6XC4Q8W",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMJYKRM1F2Z27YWK2SC54H",
    "n": 7,
    "repeat": 3,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:57:17.018167+00:00",
    "completed_at": "2026-11-05T19:57:17.235087+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 216.884,
    "response_received_at": "2026-11-05T19:57:17.234005+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.42, home_lookup;dur=0.79, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.12, replica_open;dur=0.17, route;dur=0.58, local_begin;dur=5.90, local_allowance;dur=0.01, local_native;dur=0.21, local_execute;dur=0.24, ack;dur=9.15, local_capture;dur=0.28, local_hlc;dur=0.00, local_seal;dur=0.07, push_publish;dur=0.46, push_hold;dur=5.71, local_commit;dur=2.20, execute;dur=0.78, app;dur=18.34;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMJZE8BP5DZS6BS4QZN88V",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMJZATQQK50CSV6D7MC1K8",
    "n": 8,
    "repeat": 3,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:57:17.735515+00:00",
    "completed_at": "2026-11-05T19:57:17.951887+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 216.333,
    "response_received_at": "2026-11-05T19:57:17.951204+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.09, auth;dur=0.33, home_lookup;dur=0.78, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.13, replica_open;dur=0.17, route;dur=1.06, local_begin;dur=6.45, local_allowance;dur=0.01, local_native;dur=0.17, local_execute;dur=0.20, ack;dur=9.65, local_capture;dur=0.33, local_hlc;dur=0.00, local_seal;dur=0.09, push_publish;dur=0.52, push_hold;dur=5.90, local_commit;dur=2.70, execute;dur=0.09, app;dur=19.02;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMK04NT1DN3DP5M14FVAS4",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMK017WG5Z7HTVSYXGCHQA",
    "n": 9,
    "repeat": 3,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:57:18.452276+00:00",
    "completed_at": "2026-11-05T19:57:18.669275+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 216.952,
    "response_received_at": "2026-11-05T19:57:18.668514+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.32, home_lookup;dur=0.76, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.11, replica_open;dur=0.16, route;dur=0.56, local_begin;dur=6.10, local_allowance;dur=0.01, local_native;dur=0.19, local_execute;dur=0.22, ack;dur=9.67, local_capture;dur=0.35, local_hlc;dur=0.00, local_seal;dur=0.09, push_publish;dur=0.61, push_hold;dur=5.95, local_commit;dur=2.55, execute;dur=0.09, app;dur=18.15;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMK0V2E1RR04VZ70Q1TBA6",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMK0QMAR5G0AX1ZM4RHWQ8",
    "n": 10,
    "repeat": 3,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:57:19.169627+00:00",
    "completed_at": "2026-11-05T19:57:19.391400+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 221.732,
    "response_received_at": "2026-11-05T19:57:19.390622+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.10, auth;dur=0.39, home_lookup;dur=1.49, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.11, replica_open;dur=0.16, route;dur=0.66, local_begin;dur=6.66, local_allowance;dur=0.01, local_native;dur=0.16, local_execute;dur=0.19, ack;dur=12.86, local_capture;dur=0.32, local_hlc;dur=0.00, local_seal;dur=0.10, push_publish;dur=1.42, push_hold;dur=7.88, local_commit;dur=2.73, execute;dur=0.18, app;dur=22.88;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMK1HFNXJJYNHB3EV05EYS",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMK1E1D44EGF2QS0HB94NS",
    "n": 11,
    "repeat": 3,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:57:19.892083+00:00",
    "completed_at": "2026-11-05T19:57:20.127529+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 235.393,
    "response_received_at": "2026-11-05T19:57:20.126830+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.34, home_lookup;dur=4.25, qos;dur=1.34, project;dur=0.03, placement;dur=0.01, tenant;dur=3.61, replica_open;dur=1.47, route;dur=1.45, local_begin;dur=8.45, local_allowance;dur=0.01, local_native;dur=0.19, local_execute;dur=0.23, ack;dur=12.86, local_capture;dur=0.51, local_hlc;dur=0.00, local_seal;dur=0.08, push_publish;dur=1.92, push_hold;dur=6.08, local_commit;dur=3.99, execute;dur=0.20, app;dur=34.36;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMK282VW6SW5J540VZR3RT",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMK24K19Y25DHVNWVTYBRX",
    "n": 12,
    "repeat": 3,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:57:20.627894+00:00",
    "completed_at": "2026-11-05T19:57:20.848404+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 220.449,
    "response_received_at": "2026-11-05T19:57:20.847652+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.78, home_lookup;dur=0.85, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.11, replica_open;dur=0.17, route;dur=0.59, local_begin;dur=7.09, local_allowance;dur=0.01, local_native;dur=0.17, local_execute;dur=0.20, ack;dur=12.24, local_capture;dur=0.36, local_hlc;dur=0.00, local_seal;dur=0.16, push_publish;dur=1.00, push_hold;dur=6.19, local_commit;dur=2.94, execute;dur=0.14, app;dur=22.37;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMK2Z2ENW9D2ZB5WX946Y8",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMK2VKPRPG40BW9J5TCBK2",
    "n": 13,
    "repeat": 3,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:57:11.569932+00:00",
    "completed_at": "2026-11-05T19:57:21.238176+00:00",
    "kind": "move_rerun_home",
    "region": "gru",
    "ok": true,
    "ms": 9668.226,
    "response_received_at": "2026-11-05T19:57:21.237381+00:00",
    "status": 200,
    "headers": {
      "x-request-id": "01M6PMJT874F0RB7QMQW914XAG",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "repeat": 3,
    "target_home": "fra",
    "value": {
      "id": "01M6PK77TX4H0B1D3GQZ73XZCF",
      "project_id": "01M6PENV06CRPSJYDDRZTF75R4",
      "name": "milestone-b-postfix",
      "created_at": "2026-11-05T19:33:23.9337227+00:00",
      "region": "gru",
      "home_region": "fra",
      "branched_from": null,
      "raw": {
        "id": "01M6PK77TX4H0B1D3GQZ73XZCF",
        "projectId": "01M6PENV06CRPSJYDDRZTF75R4",
        "name": "milestone-b-postfix",
        "branchedFrom": null,
        "createdAt": "2026-11-05T19:33:23.9337227+00:00",
        "region": "gru",
        "homeRegion": "fra",
        "replicaRegions": [],
        "allRegions": true,
        "previousName": null,
        "conflictOf": null,
        "expiresAt": null
      },
      "previous_name": null,
      "conflict_of": null,
      "expires_at": null,
      "replica_regions": [],
      "all_regions": true
    }
  },
  {
    "started_at": "2026-11-05T19:57:21.348726+00:00",
    "completed_at": "2026-11-05T19:57:21.578710+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 229.955,
    "response_received_at": "2026-11-05T19:57:21.577980+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.11, auth;dur=0.27, home_lookup;dur=3.06, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.12, replica_open;dur=0.66, route;dur=2.16, local_begin;dur=7.67, local_allowance;dur=0.01, local_native;dur=0.17, local_execute;dur=0.20, ack;dur=17.32, local_capture;dur=0.45, local_hlc;dur=0.00, local_seal;dur=0.09, push_publish;dur=1.71, push_hold;dur=11.37, local_commit;dur=3.53, execute;dur=0.11, app;dur=31.74;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMK3NJNFAJBDK6VRT6BPDM",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMK3J4QJQM0DF0GPZFT60H",
    "n": 14,
    "repeat": 3,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:57:22.079076+00:00",
    "completed_at": "2026-11-05T19:57:22.342612+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 263.491,
    "response_received_at": "2026-11-05T19:57:22.341826+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.19, auth;dur=0.36, home_lookup;dur=6.69, qos;dur=1.58, project;dur=0.03, placement;dur=0.01, tenant;dur=7.66, replica_open;dur=1.72, route;dur=1.01, local_begin;dur=10.35, local_allowance;dur=0.01, local_native;dur=0.17, local_execute;dur=0.21, ack;dur=11.89, local_capture;dur=0.50, local_hlc;dur=0.00, local_seal;dur=0.09, push_publish;dur=1.57, push_hold;dur=5.78, local_commit;dur=2.65, execute;dur=0.10, app;dur=41.80;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMK4CDYS5K0Q3J9J3F3EP7",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMK48Y3HCVAQSXVZECASYW",
    "n": 15,
    "repeat": 3,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:57:22.843063+00:00",
    "completed_at": "2026-11-05T19:57:23.059105+00:00",
    "kind": "move_rerun_write",
    "region": "sjc",
    "ok": true,
    "ms": 215.994,
    "response_received_at": "2026-11-05T19:57:23.058409+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.11, auth;dur=0.34, home_lookup;dur=0.88, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.14, replica_open;dur=0.16, route;dur=0.53, local_begin;dur=5.90, local_allowance;dur=0.01, local_native;dur=0.18, local_execute;dur=0.21, ack;dur=9.66, local_capture;dur=0.36, local_hlc;dur=0.00, local_seal;dur=0.09, push_publish;dur=0.50, push_hold;dur=5.81, local_commit;dur=2.79, execute;dur=0.09, app;dur=18.09;desc=\"sjc\"",
      "terrascale-write-route": "route=local; reason=ulid_insert; commit=local",
      "x-request-id": "01M6PMK549WQA29W4D8CZ7QX7N",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "id": "01M6PMK50THRJJZ0M9F21Z390N",
    "n": 16,
    "repeat": 3,
    "value": {
      "columns": [],
      "rows": [],
      "rows_affected": 1,
      "last_insert_rowid": null
    }
  },
  {
    "started_at": "2026-11-05T19:57:23.080820+00:00",
    "completed_at": "2026-11-05T19:57:23.404399+00:00",
    "kind": "move_rerun_inventory",
    "region": "gru",
    "ok": true,
    "ms": 323.568,
    "response_received_at": "2026-11-05T19:57:23.357330+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.10, auth;dur=7.03, home_lookup;dur=7.03, qos;dur=0.69, project;dur=0.03, placement;dur=0.01, tenant;dur=5.76, replica_open;dur=4.62, execute;dur=3.01, app;dur=28.27;desc=\"gru\"",
      "x-request-id": "01M6PMK5FY0FP92NXYZB2RBRWS",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "repeat": 3,
    "row_count": 1003,
    "distinct_ids": 1003,
    "canonical_inventory_sha256": "3b4da727f284ecfb937a0520943a91adf78491aed94a7e5dbe95b1d521fd4f00"
  },
  {
    "kind": "move_rerun_comparison",
    "repeat": 3,
    "region": "gru",
    "expected": 1003,
    "accepted_writes": 17,
    "attempted_writes": 17,
    "rows": 1003,
    "distinct": 1003,
    "missing": [],
    "unexpected": [],
    "changed": []
  },
  {
    "started_at": "2026-11-05T19:57:23.410500+00:00",
    "completed_at": "2026-11-05T19:57:23.436363+00:00",
    "kind": "move_rerun_metadata",
    "region": "gru",
    "ok": true,
    "ms": 25.84,
    "response_received_at": "2026-11-05T19:57:23.435602+00:00",
    "status": 200,
    "headers": {
      "x-request-id": "01M6PMK5K6TGZMS2039JBXQ936",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "repeat": 3,
    "value": {
      "id": "01M6PK77TX4H0B1D3GQZ73XZCF",
      "project_id": "01M6PENV06CRPSJYDDRZTF75R4",
      "name": "milestone-b-postfix",
      "created_at": "2026-11-05T19:33:23.9337227+00:00",
      "region": "gru",
      "home_region": "fra",
      "branched_from": null,
      "raw": {
        "id": "01M6PK77TX4H0B1D3GQZ73XZCF",
        "projectId": "01M6PENV06CRPSJYDDRZTF75R4",
        "name": "milestone-b-postfix",
        "branchedFrom": null,
        "createdAt": "2026-11-05T19:33:23.9337227+00:00",
        "region": "gru",
        "homeRegion": "fra",
        "replicaRegions": [],
        "allRegions": true,
        "previousName": null,
        "conflictOf": null,
        "expiresAt": null
      },
      "previous_name": null,
      "conflict_of": null,
      "expires_at": null,
      "replica_regions": [],
      "all_regions": true
    }
  },
  {
    "started_at": "2026-11-05T19:57:23.459014+00:00",
    "completed_at": "2026-11-05T19:57:24.784963+00:00",
    "kind": "move_rerun_inventory",
    "region": "sjc",
    "ok": true,
    "ms": 1325.93,
    "response_received_at": "2026-11-05T19:57:24.398000+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=173.27, home_lookup;dur=0.88, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.11, replica_open;dur=0.15, execute;dur=5.09, app;dur=179.67;desc=\"sjc\"",
      "x-request-id": "01M6PMK695QQ6T6C2T81V21206",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "repeat": 3,
    "row_count": 1003,
    "distinct_ids": 1003,
    "canonical_inventory_sha256": "3b4da727f284ecfb937a0520943a91adf78491aed94a7e5dbe95b1d521fd4f00"
  },
  {
    "kind": "move_rerun_comparison",
    "repeat": 3,
    "region": "sjc",
    "expected": 1003,
    "accepted_writes": 17,
    "attempted_writes": 17,
    "rows": 1003,
    "distinct": 1003,
    "missing": [],
    "unexpected": [],
    "changed": []
  },
  {
    "started_at": "2026-11-05T19:57:24.791044+00:00",
    "completed_at": "2026-11-05T19:57:24.994061+00:00",
    "kind": "move_rerun_metadata",
    "region": "sjc",
    "ok": true,
    "ms": 202.997,
    "response_received_at": "2026-11-05T19:57:24.993093+00:00",
    "status": 200,
    "headers": {
      "x-request-id": "01M6PMK716WGSAWXYWA1QZG175",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "repeat": 3,
    "value": {
      "id": "01M6PK77TX4H0B1D3GQZ73XZCF",
      "project_id": "01M6PENV06CRPSJYDDRZTF75R4",
      "name": "milestone-b-postfix",
      "created_at": "2026-11-05T19:33:24.7695968+00:00",
      "region": "sjc",
      "home_region": "fra",
      "branched_from": null,
      "raw": {
        "id": "01M6PK77TX4H0B1D3GQZ73XZCF",
        "projectId": "01M6PENV06CRPSJYDDRZTF75R4",
        "name": "milestone-b-postfix",
        "branchedFrom": null,
        "createdAt": "2026-11-05T19:33:24.7695968+00:00",
        "region": "sjc",
        "homeRegion": "fra",
        "replicaRegions": [],
        "allRegions": true,
        "previousName": null,
        "conflictOf": null,
        "expiresAt": null
      },
      "previous_name": null,
      "conflict_of": null,
      "expires_at": null,
      "replica_regions": [],
      "all_regions": true
    }
  },
  {
    "started_at": "2026-11-05T19:57:25.016176+00:00",
    "completed_at": "2026-11-05T19:57:26.447943+00:00",
    "kind": "move_rerun_inventory",
    "region": "fra",
    "ok": true,
    "ms": 1431.751,
    "response_received_at": "2026-11-05T19:57:26.027790+00:00",
    "status": 200,
    "headers": {
      "server-timing": "admission;dur=0.11, auth;dur=186.41, home_lookup;dur=8.15, qos;dur=4.46, project;dur=0.08, placement;dur=0.02, tenant;dur=2.86, execute;dur=5.32, app;dur=207.41;desc=\"fra\"",
      "x-request-id": "01M6PMK7TVYD9MYXNTZ09NXBYC",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "repeat": 3,
    "row_count": 1003,
    "distinct_ids": 1003,
    "canonical_inventory_sha256": "3b4da727f284ecfb937a0520943a91adf78491aed94a7e5dbe95b1d521fd4f00"
  },
  {
    "kind": "move_rerun_comparison",
    "repeat": 3,
    "region": "fra",
    "expected": 1003,
    "accepted_writes": 17,
    "attempted_writes": 17,
    "rows": 1003,
    "distinct": 1003,
    "missing": [],
    "unexpected": [],
    "changed": []
  },
  {
    "started_at": "2026-11-05T19:57:26.453597+00:00",
    "completed_at": "2026-11-05T19:57:26.669179+00:00",
    "kind": "move_rerun_metadata",
    "region": "fra",
    "ok": true,
    "ms": 215.564,
    "response_received_at": "2026-11-05T19:57:26.668374+00:00",
    "status": 200,
    "headers": {
      "x-request-id": "01M6PMK8N8D4XFB64M80ARN6K8",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "repeat": 3,
    "value": {
      "id": "01M6PK77TX4H0B1D3GQZ73XZCF",
      "project_id": "01M6PENV06CRPSJYDDRZTF75R4",
      "name": "milestone-b-postfix",
      "created_at": "2026-11-05T19:33:24.6609603+00:00",
      "region": "fra",
      "home_region": "fra",
      "branched_from": null,
      "raw": {
        "id": "01M6PK77TX4H0B1D3GQZ73XZCF",
        "projectId": "01M6PENV06CRPSJYDDRZTF75R4",
        "name": "milestone-b-postfix",
        "branchedFrom": null,
        "createdAt": "2026-11-05T19:33:24.6609603+00:00",
        "region": "fra",
        "homeRegion": "fra",
        "replicaRegions": [
          "gru",
          "sjc"
        ],
        "allRegions": true,
        "previousName": null,
        "conflictOf": null,
        "expiresAt": null
      },
      "previous_name": null,
      "conflict_of": null,
      "expires_at": null,
      "replica_regions": [
        "gru",
        "sjc"
      ],
      "all_regions": true
    }
  }
]
```

## Stock psql after corrected R9

All regional endpoints return1003 total/distinct rows with TLS certificate verification. Connection establishment is included in client timings.

```json
[
  {
    "region": "gru",
    "started_at": "2026-11-05T20:00:30.206078+00:00",
    "completed_at": "2026-11-05T20:00:30.606696+00:00",
    "ms": 400.646,
    "exit": 0,
    "stdout": "1003|1003\n",
    "stderr": ""
  },
  {
    "region": "sjc",
    "started_at": "2026-11-05T20:00:30.206751+00:00",
    "completed_at": "2026-11-05T20:00:31.975200+00:00",
    "ms": 1768.47,
    "exit": 0,
    "stdout": "1003|1003\n",
    "stderr": ""
  },
  {
    "region": "fra",
    "started_at": "2026-11-05T20:00:30.207844+00:00",
    "completed_at": "2026-11-05T20:00:32.063062+00:00",
    "ms": 1855.242,
    "exit": 0,
    "stdout": "1003|1003\n",
    "stderr": ""
  }
]
```

## Baseline-first independent confirmation

The path baseline precedes every measured write. Full timed requests, including failures and sanitized SQL error messages, are in the samples document. Exact inventories retain all ID/value comparisons.

```json
[
  {
    "kind": "environment",
    "host": "delta",
    "sdk_revision": "ddbc3a87d7c16cedf918cb3754bad364f7c068eb",
    "at": "2026-11-05T20:20:49Z"
  },
  {
    "kind": "confirmation_database_created",
    "at": "2026-11-05T20:20:50.064511+00:00",
    "value": {
      "id": "01M6PNY2NSDD07TVSQZJYHK8JW",
      "project_id": "01M6PENV06CRPSJYDDRZTF75R4",
      "name": "milestone-b-confirmation",
      "created_at": "2026-11-05T20:20:49.4654528+00:00",
      "region": "gru",
      "home_region": "gru",
      "branched_from": null,
      "raw": {
        "id": "01M6PNY2NSDD07TVSQZJYHK8JW",
        "projectId": "01M6PENV06CRPSJYDDRZTF75R4",
        "name": "milestone-b-confirmation",
        "branchedFrom": null,
        "createdAt": "2026-11-05T20:20:49.4654528+00:00",
        "region": "gru",
        "homeRegion": "gru",
        "replicaRegions": [
          "fra",
          "sjc"
        ],
        "allRegions": true,
        "previousName": null,
        "conflictOf": null,
        "expiresAt": null
      },
      "previous_name": null,
      "conflict_of": null,
      "expires_at": null,
      "replica_regions": [
        "fra",
        "sjc"
      ],
      "all_regions": true
    }
  },
  {
    "kind": "exact_comparison",
    "region": "gru",
    "expected": 960,
    "rows": 960,
    "distinct": 960,
    "missing": [],
    "unexpected": [],
    "changed": []
  },
  {
    "kind": "exact_comparison",
    "region": "sjc",
    "expected": 960,
    "rows": 960,
    "distinct": 960,
    "missing": [],
    "unexpected": [],
    "changed": []
  },
  {
    "kind": "exact_comparison",
    "region": "fra",
    "expected": 960,
    "rows": 960,
    "distinct": 960,
    "missing": [],
    "unexpected": [],
    "changed": []
  }
]
```

All-attempt write percentiles:

```json
[
  {
    "region": "gru",
    "repeat": 1,
    "attempted": 100,
    "accepted": 100,
    "origin_samples": 100,
    "server_pass": true,
    "client_pass": true,
    "server_p99": 38.7,
    "client_p99": 65.293,
    "server_max": 47.73,
    "client_max": 70.401,
    "baseline": 23.199
  },
  {
    "region": "gru",
    "repeat": 2,
    "attempted": 100,
    "accepted": 100,
    "origin_samples": 100,
    "server_pass": true,
    "client_pass": true,
    "server_p99": 47.14,
    "client_p99": 68.869,
    "server_max": 54.47,
    "client_max": 77.193,
    "baseline": 23.199
  },
  {
    "region": "gru",
    "repeat": 3,
    "attempted": 100,
    "accepted": 100,
    "origin_samples": 100,
    "server_pass": true,
    "client_pass": true,
    "server_p99": 57.93,
    "client_p99": 81.493,
    "server_max": 61.03,
    "client_max": 84.913,
    "baseline": 23.199
  },
  {
    "region": "gru",
    "repeat": null,
    "attempted": 300,
    "accepted": 300,
    "origin_samples": 300,
    "server_pass": true,
    "client_pass": true,
    "server_p99": 54.47,
    "client_p99": 77.193,
    "server_max": 61.03,
    "client_max": 84.913,
    "baseline": 23.199
  },
  {
    "region": "sjc",
    "repeat": 1,
    "attempted": 100,
    "accepted": 100,
    "origin_samples": 100,
    "server_pass": false,
    "client_pass": false,
    "server_p99": 202.83,
    "client_p99": 400.024,
    "server_max": 551.04,
    "client_max": 745.894,
    "baseline": 193.266
  },
  {
    "region": "sjc",
    "repeat": 2,
    "attempted": 100,
    "accepted": 100,
    "origin_samples": 100,
    "server_pass": true,
    "client_pass": true,
    "server_p99": 46.33,
    "client_p99": 242.575,
    "server_max": 79.61,
    "client_max": 275.416,
    "baseline": 193.266
  },
  {
    "region": "sjc",
    "repeat": 3,
    "attempted": 100,
    "accepted": 100,
    "origin_samples": 100,
    "server_pass": true,
    "client_pass": true,
    "server_p99": 61.07,
    "client_p99": 255.486,
    "server_max": 91.69,
    "client_max": 289.329,
    "baseline": 193.266
  },
  {
    "region": "sjc",
    "repeat": null,
    "attempted": 300,
    "accepted": 300,
    "origin_samples": 300,
    "server_pass": true,
    "client_pass": true,
    "server_p99": 79.61,
    "client_p99": 275.416,
    "server_max": 551.04,
    "client_max": 745.894,
    "baseline": 193.266
  },
  {
    "region": "fra",
    "repeat": 1,
    "attempted": 100,
    "accepted": 100,
    "origin_samples": 100,
    "server_pass": true,
    "client_pass": true,
    "server_p99": 57.08,
    "client_p99": 269.991,
    "server_max": 92.77,
    "client_max": 306.775,
    "baseline": 211.43699999999998
  },
  {
    "region": "fra",
    "repeat": 2,
    "attempted": 100,
    "accepted": 100,
    "origin_samples": 100,
    "server_pass": true,
    "client_pass": true,
    "server_p99": 47.27,
    "client_p99": 260.67,
    "server_max": 84.42,
    "client_max": 308.176,
    "baseline": 211.43699999999998
  },
  {
    "region": "fra",
    "repeat": 3,
    "attempted": 100,
    "accepted": 100,
    "origin_samples": 100,
    "server_pass": true,
    "client_pass": true,
    "server_p99": 49.84,
    "client_p99": 263.422,
    "server_max": 50.1,
    "client_max": 263.659,
    "baseline": 211.43699999999998
  },
  {
    "region": "fra",
    "repeat": null,
    "attempted": 300,
    "accepted": 300,
    "origin_samples": 300,
    "server_pass": true,
    "client_pass": true,
    "server_p99": 50.64,
    "client_p99": 265.568,
    "server_max": 92.77,
    "client_max": 308.176,
    "baseline": 211.43699999999998
  }
]
```

Every failed API call:

```json
[]
```

Immediate home checks following home-durable acknowledgement:

```json
[
  {
    "source": "gru",
    "id": "01M6PP38QNC3FFZXZ37FHE523K",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T20:23:39.575935+00:00",
    "headers": {
      "server-timing": "admission;dur=0.13, auth;dur=0.15, home_lookup;dur=0.82, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.10, execute;dur=0.66, app;dur=1.92;desc=\"gru\"",
      "x-request-id": "01M6PP38SQN59R36CSYTE38T98",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "gru",
    "id": "01M6PP38SRD1STTZ8XZANSCT3H",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T20:23:39.635603+00:00",
    "headers": {
      "server-timing": "admission;dur=0.14, auth;dur=0.13, home_lookup;dur=0.86, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.07, execute;dur=0.70, app;dur=1.96;desc=\"gru\"",
      "x-request-id": "01M6PP38VJ4SF60N781Q2EV9EW",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "gru",
    "id": "01M6PP38VK6ZE1J3Y18J60VH88",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T20:23:39.692761+00:00",
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.12, home_lookup;dur=0.85, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.08, execute;dur=0.71, app;dur=1.94;desc=\"gru\"",
      "x-request-id": "01M6PP38XCHHXMB544CBFNZAJ2",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "gru",
    "id": "01M6PP38XCNX3N7B9SR873P08T",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T20:23:39.752660+00:00",
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.12, home_lookup;dur=0.60, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.08, execute;dur=0.85, app;dur=1.84;desc=\"gru\"",
      "x-request-id": "01M6PP38Z8D2E4YABWSKJK7GB4",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "gru",
    "id": "01M6PP38Z83SZ5KQR8P89HC10E",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T20:23:39.823841+00:00",
    "headers": {
      "server-timing": "admission;dur=0.11, auth;dur=0.11, home_lookup;dur=1.00, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.09, execute;dur=0.72, app;dur=2.10;desc=\"gru\"",
      "x-request-id": "01M6PP391DXM774HQXE7DJ1SEB",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "gru",
    "id": "01M6PP391GMJH17JQS380B3FW0",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T20:23:39.882527+00:00",
    "headers": {
      "server-timing": "admission;dur=0.13, auth;dur=0.12, home_lookup;dur=0.94, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.07, execute;dur=0.68, app;dur=2.00;desc=\"gru\"",
      "x-request-id": "01M6PP393956H848C1VKDRDMHK",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "gru",
    "id": "01M6PP393AGJBY6XC4MG3MMM0F",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T20:23:39.957812+00:00",
    "headers": {
      "server-timing": "admission;dur=0.11, auth;dur=0.13, home_lookup;dur=0.77, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.08, execute;dur=1.20, app;dur=2.36;desc=\"gru\"",
      "x-request-id": "01M6PP395MZN7ZPHXTMY7BRJDW",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "gru",
    "id": "01M6PP395NJA3V1S3WPJBV0TGR",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T20:23:40.024012+00:00",
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.15, home_lookup;dur=0.83, qos;dur=0.03, project;dur=0.03, placement;dur=0.01, tenant;dur=0.08, execute;dur=0.74, app;dur=1.99;desc=\"gru\"",
      "x-request-id": "01M6PP397QJH3B25RY5HBRYVTT",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "gru",
    "id": "01M6PP397RMNC4B4G674VJEGC3",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T20:23:40.083160+00:00",
    "headers": {
      "server-timing": "admission;dur=0.07, auth;dur=0.08, home_lookup;dur=0.55, qos;dur=0.02, project;dur=0.02, placement;dur=0.01, tenant;dur=0.05, execute;dur=2.06, app;dur=2.87;desc=\"gru\"",
      "x-request-id": "01M6PP399H0079NAH31VMPRFZ9",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "gru",
    "id": "01M6PP399K5PHG6EJH0ZF0FYJN",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T20:23:40.143497+00:00",
    "headers": {
      "server-timing": "admission;dur=0.13, auth;dur=0.12, home_lookup;dur=0.95, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.07, execute;dur=0.73, app;dur=2.07;desc=\"gru\"",
      "x-request-id": "01M6PP39BE2J1VZC8MXPJKV52B",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "sjc",
    "id": "01M6PP3APEHR772N0QTDA4RFFX",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T20:23:41.946120+00:00",
    "headers": {
      "server-timing": "admission;dur=0.16, auth;dur=0.12, home_lookup;dur=3.69, qos;dur=1.43, project;dur=0.03, placement;dur=0.01, tenant;dur=1.77, execute;dur=1.53, app;dur=8.75;desc=\"gru\"",
      "x-request-id": "01M6PP3B3J8XWEVF901CSJN80B",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "sjc",
    "id": "01M6PP3B3TGSYG12T42P8GW10Q",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T20:23:42.358756+00:00",
    "headers": {
      "server-timing": "admission;dur=0.14, auth;dur=0.58, home_lookup;dur=0.92, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.10, execute;dur=1.10, app;dur=2.91;desc=\"gru\"",
      "x-request-id": "01M6PP3BGNF49XVQQGQ9WSCP3W",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "sjc",
    "id": "01M6PP3BGPYWX4AVSRQP78C3N4",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T20:23:42.769578+00:00",
    "headers": {
      "server-timing": "admission;dur=0.11, auth;dur=0.11, home_lookup;dur=0.71, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.09, execute;dur=1.79, app;dur=2.87;desc=\"gru\"",
      "x-request-id": "01M6PP3BXG3J2HSCPGXHXN025N",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "sjc",
    "id": "01M6PP3BXHQHRQVTRT3M493PNB",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T20:23:43.194197+00:00",
    "headers": {
      "server-timing": "admission;dur=0.13, auth;dur=0.15, home_lookup;dur=0.84, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.08, execute;dur=0.81, app;dur=2.08;desc=\"gru\"",
      "x-request-id": "01M6PP3CASWKBMGJEMD7ERMK11",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "sjc",
    "id": "01M6PP3CAT0HNAR9A48JBA5K2H",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T20:23:43.608135+00:00",
    "headers": {
      "server-timing": "admission;dur=0.13, auth;dur=0.44, home_lookup;dur=0.93, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.08, execute;dur=0.86, app;dur=2.50;desc=\"gru\"",
      "x-request-id": "01M6PP3CQPWXP0AFCAE04WQV5F",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "sjc",
    "id": "01M6PP3CQRECSDS3A4CBV76Y5M",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T20:23:44.022244+00:00",
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.14, home_lookup;dur=0.87, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.08, execute;dur=3.26, app;dur=4.54;desc=\"gru\"",
      "x-request-id": "01M6PP3D4J3BSJTT0HP7CYC32P",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "sjc",
    "id": "01M6PP3D4PHYF0F0N4ENG4PYJG",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T20:23:44.428944+00:00",
    "headers": {
      "server-timing": "admission;dur=0.13, auth;dur=0.12, home_lookup;dur=0.90, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.08, execute;dur=0.67, app;dur=1.97;desc=\"gru\"",
      "x-request-id": "01M6PP3DHCCEE1PJG56N9JEV77",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "sjc",
    "id": "01M6PP3DHDY5T4E26HTY3Z0YK1",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T20:23:44.846139+00:00",
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.46, home_lookup;dur=4.46, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.05, execute;dur=1.37, app;dur=6.52;desc=\"gru\"",
      "x-request-id": "01M6PP3DY9NHSANTP07DKCSBFS",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "sjc",
    "id": "01M6PP3DYEWMM5TM1S2GKHSQ5Z",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T20:23:45.258191+00:00",
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.12, home_lookup;dur=0.90, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.14, execute;dur=0.78, app;dur=2.13;desc=\"gru\"",
      "x-request-id": "01M6PP3EB9DEVKBTJY448E9AXC",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "sjc",
    "id": "01M6PP3EBA4TGYX8YD8P7KRYGS",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T20:23:45.671974+00:00",
    "headers": {
      "server-timing": "admission;dur=0.13, auth;dur=0.15, home_lookup;dur=0.90, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.10, execute;dur=1.01, app;dur=2.35;desc=\"gru\"",
      "x-request-id": "01M6PP3ER69WKS1ZYT19AEQNDJ",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "fra",
    "id": "01M6PP3MDZCNG0REGSGY9QKP64",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T20:23:52.016697+00:00",
    "headers": {
      "server-timing": "admission;dur=0.08, auth;dur=0.07, home_lookup;dur=5.60, qos;dur=5.52, project;dur=0.03, placement;dur=0.02, tenant;dur=3.14, execute;dur=3.77, app;dur=18.24;desc=\"gru\"",
      "x-request-id": "01M6PP3MXZKXNX6P503Q2GT93N",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "fra",
    "id": "01M6PP3MYG6DKVM8HF8PMKHHKF",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T20:23:52.463233+00:00",
    "headers": {
      "server-timing": "admission;dur=0.09, auth;dur=0.25, home_lookup;dur=1.92, qos;dur=0.02, project;dur=0.01, placement;dur=0.01, tenant;dur=0.07, execute;dur=0.87, app;dur=3.24;desc=\"gru\"",
      "x-request-id": "01M6PP3NCCYR7HK0M76CZ1Q51M",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "fra",
    "id": "01M6PP3NCFSJ6PP3ZR69ADFBMH",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T20:23:52.928573+00:00",
    "headers": {
      "server-timing": "admission;dur=0.09, auth;dur=0.11, home_lookup;dur=0.86, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.08, execute;dur=0.75, app;dur=1.96;desc=\"gru\"",
      "x-request-id": "01M6PP3NTZDPN0YN6ZRYCDMYTA",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "fra",
    "id": "01M6PP3NV0MWEP9NWY5X9N2V8K",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T20:23:53.398688+00:00",
    "headers": {
      "server-timing": "admission;dur=0.13, auth;dur=0.12, home_lookup;dur=0.91, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.11, execute;dur=0.72, app;dur=2.06;desc=\"gru\"",
      "x-request-id": "01M6PP3P9NFYDTNAKJGN41YYRA",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "fra",
    "id": "01M6PP3P9PFCGS0P70K0TQRN07",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T20:23:53.847366+00:00",
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.12, home_lookup;dur=0.98, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.08, execute;dur=0.69, app;dur=2.04;desc=\"gru\"",
      "x-request-id": "01M6PP3PQPF2XJCDCGY3YX20JW",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "fra",
    "id": "01M6PP3PQQY8NJ29XDDNTPPM41",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T20:23:54.294406+00:00",
    "headers": {
      "server-timing": "admission;dur=0.13, auth;dur=0.53, home_lookup;dur=0.89, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.07, execute;dur=1.11, app;dur=2.80;desc=\"gru\"",
      "x-request-id": "01M6PP3Q5MQRYMBCZ4Y1RRHW74",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "fra",
    "id": "01M6PP3Q5PMY68DR7PMG85QR99",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T20:23:54.740116+00:00",
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.18, home_lookup;dur=2.36, qos;dur=0.04, project;dur=0.02, placement;dur=0.01, tenant;dur=0.10, execute;dur=0.78, app;dur=3.61;desc=\"gru\"",
      "x-request-id": "01M6PP3QKJRA9FJCCKW706CDQ9",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "fra",
    "id": "01M6PP3QKMTNY7TMJTXB2XVBSS",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T20:23:55.185283+00:00",
    "headers": {
      "server-timing": "admission;dur=0.13, auth;dur=0.12, home_lookup;dur=1.12, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.13, execute;dur=0.79, app;dur=2.35;desc=\"gru\"",
      "x-request-id": "01M6PP3R1GD5H8E1MADVG27DFX",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "fra",
    "id": "01M6PP3R1H3A9ZFMSP2QGGNXK9",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T20:23:55.628549+00:00",
    "headers": {
      "server-timing": "admission;dur=0.09, auth;dur=0.09, home_lookup;dur=0.61, qos;dur=0.03, project;dur=0.02, placement;dur=0.01, tenant;dur=0.07, execute;dur=0.69, app;dur=1.62;desc=\"gru\"",
      "x-request-id": "01M6PP3RFB7TBQEFCQT98CGK4N",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "source": "fra",
    "id": "01M6PP3RFCVJN9C9TRH412QJEN",
    "ok": true,
    "status": 200,
    "visible": true,
    "completed_at": "2026-11-05T20:23:56.102833+00:00",
    "headers": {
      "server-timing": "admission;dur=0.10, auth;dur=0.42, home_lookup;dur=5.69, qos;dur=3.55, project;dur=0.03, placement;dur=0.01, tenant;dur=2.49, execute;dur=1.21, app;dur=13.51;desc=\"gru\"",
      "x-request-id": "01M6PP3RXTWSDVCKTQKXY9JC8W",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  }
]
```

## final-database-cleanup.json

```json
[
  {
    "status": 200,
    "received_at": "2026-11-05T20:28:13.490303+00:00",
    "headers": {
      "x-request-id": "01M6PPBKWZ5NFSTRQRJZHJQTM1",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "ok": true,
    "database_ids": [
      "01M6PFQFV240ECH0BCQ5AKFYHX",
      "01M6PK77TX4H0B1D3GQZ73XZCF",
      "01M6PNY2NSDD07TVSQZJYHK8JW"
    ],
    "next_cursor": null,
    "kind": "final-list-before-delete",
    "region": "gru",
    "started_at": "2026-11-05T20:28:13.031048+00:00",
    "completed_at": "2026-11-05T20:28:13.490907+00:00",
    "ms": 459.872
  },
  {
    "status": 200,
    "received_at": "2026-11-05T20:28:14.603574+00:00",
    "headers": {
      "x-request-id": "01M6PPBMRY5WE6TV27C3TF7WGY",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "ok": true,
    "database_ids": [
      "01M6PFQFV240ECH0BCQ5AKFYHX",
      "01M6PK77TX4H0B1D3GQZ73XZCF",
      "01M6PNY2NSDD07TVSQZJYHK8JW"
    ],
    "next_cursor": null,
    "kind": "final-list-before-delete",
    "region": "sjc",
    "started_at": "2026-11-05T20:28:13.491049+00:00",
    "completed_at": "2026-11-05T20:28:14.604167+00:00",
    "ms": 1113.129
  },
  {
    "status": 200,
    "received_at": "2026-11-05T20:28:15.835373+00:00",
    "headers": {
      "x-request-id": "01M6PPBNXPPHT4F4AQZMSGGGJD",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "ok": true,
    "database_ids": [
      "01M6PFQFV240ECH0BCQ5AKFYHX",
      "01M6PK77TX4H0B1D3GQZ73XZCF",
      "01M6PNY2NSDD07TVSQZJYHK8JW"
    ],
    "next_cursor": null,
    "kind": "final-list-before-delete",
    "region": "fra",
    "started_at": "2026-11-05T20:28:14.604298+00:00",
    "completed_at": "2026-11-05T20:28:15.835982+00:00",
    "ms": 1231.691
  },
  {
    "status": 204,
    "received_at": "2026-11-05T20:28:19.554875+00:00",
    "headers": {
      "x-request-id": "01M6PPBPM3R5BXCM06WC6WV3QP",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "ok": true,
    "kind": "prior-delete-database",
    "region": "gru",
    "started_at": "2026-11-05T20:28:15.866870+00:00",
    "completed_at": "2026-11-05T20:28:19.555334+00:00",
    "ms": 3688.476,
    "database_id": "01M6PFQFV240ECH0BCQ5AKFYHX"
  },
  {
    "status": 404,
    "received_at": "2026-11-05T20:28:19.769594+00:00",
    "headers": {
      "server-timing": "admission;dur=0.10, auth;dur=0.11, home_lookup;dur=188.57, qos;dur=2.03, project;dur=0.03, placement;dur=0.02, execute;dur=0.98, app;dur=191.85;desc=\"gru\"",
      "x-request-id": "01M6PPBT7C1XE67WYXC1ZBY10J",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "ok": false,
    "code": "unknown_database",
    "error_type": "NotFoundError",
    "kind": "prior-sql-after-delete",
    "region": "gru",
    "started_at": "2026-11-05T20:28:19.555445+00:00",
    "completed_at": "2026-11-05T20:28:19.770214+00:00",
    "ms": 214.78,
    "database_id": "01M6PFQFV240ECH0BCQ5AKFYHX"
  },
  {
    "status": 404,
    "received_at": "2026-11-05T20:28:19.975511+00:00",
    "headers": {
      "x-request-id": "01M6PPBTE3SKWA6VT02H31X24B",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "ok": false,
    "code": "unknown_database",
    "error_type": "NotFoundError",
    "kind": "prior-metadata-after-delete",
    "region": "gru",
    "started_at": "2026-11-05T20:28:19.770315+00:00",
    "completed_at": "2026-11-05T20:28:19.975979+00:00",
    "ms": 205.671,
    "database_id": "01M6PFQFV240ECH0BCQ5AKFYHX"
  },
  {
    "status": 404,
    "received_at": "2026-11-05T20:28:20.737790+00:00",
    "headers": {
      "server-timing": "admission;dur=0.13, auth;dur=0.31, home_lookup;dur=170.98, qos;dur=0.90, project;dur=0.03, placement;dur=0.01, execute;dur=1.11, app;dur=173.47;desc=\"sjc\"",
      "x-request-id": "01M6PPBV3NECYAZ60YBMCDNJKA",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "ok": false,
    "code": "unknown_database",
    "error_type": "NotFoundError",
    "kind": "prior-sql-after-delete",
    "region": "sjc",
    "started_at": "2026-11-05T20:28:19.976042+00:00",
    "completed_at": "2026-11-05T20:28:20.738124+00:00",
    "ms": 762.087,
    "database_id": "01M6PFQFV240ECH0BCQ5AKFYHX"
  },
  {
    "status": 404,
    "received_at": "2026-11-05T20:28:21.102616+00:00",
    "headers": {
      "x-request-id": "01M6PPBVF5X9KX1P4EWV91NGZF",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "ok": false,
    "code": "unknown_database",
    "error_type": "NotFoundError",
    "kind": "prior-metadata-after-delete",
    "region": "sjc",
    "started_at": "2026-11-05T20:28:20.738190+00:00",
    "completed_at": "2026-11-05T20:28:21.103008+00:00",
    "ms": 364.824,
    "database_id": "01M6PFQFV240ECH0BCQ5AKFYHX"
  },
  {
    "status": 404,
    "received_at": "2026-11-05T20:28:21.910967+00:00",
    "headers": {
      "server-timing": "admission;dur=0.13, auth;dur=0.12, home_lookup;dur=183.06, qos;dur=0.87, project;dur=0.02, placement;dur=0.01, execute;dur=2.38, app;dur=186.60;desc=\"fra\"",
      "x-request-id": "01M6PPBW7KAYCFKC89D8D9SQ6X",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "ok": false,
    "code": "unknown_database",
    "error_type": "NotFoundError",
    "kind": "prior-sql-after-delete",
    "region": "fra",
    "started_at": "2026-11-05T20:28:21.103078+00:00",
    "completed_at": "2026-11-05T20:28:21.911370+00:00",
    "ms": 808.299,
    "database_id": "01M6PFQFV240ECH0BCQ5AKFYHX"
  },
  {
    "status": 404,
    "received_at": "2026-11-05T20:28:22.303467+00:00",
    "headers": {
      "x-request-id": "01M6PPBWKXWP1WX20HBJRP3XFA",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "ok": false,
    "code": "unknown_database",
    "error_type": "NotFoundError",
    "kind": "prior-metadata-after-delete",
    "region": "fra",
    "started_at": "2026-11-05T20:28:21.911476+00:00",
    "completed_at": "2026-11-05T20:28:22.303969+00:00",
    "ms": 392.499,
    "database_id": "01M6PFQFV240ECH0BCQ5AKFYHX"
  },
  {
    "status": 204,
    "received_at": "2026-11-05T20:28:26.198254+00:00",
    "headers": {
      "x-request-id": "01M6PPBX136R2FSKM3VJ7MJF39",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "ok": true,
    "kind": "prior-delete-database",
    "region": "fra",
    "started_at": "2026-11-05T20:28:22.333893+00:00",
    "completed_at": "2026-11-05T20:28:26.198402+00:00",
    "ms": 3864.513,
    "database_id": "01M6PK77TX4H0B1D3GQZ73XZCF"
  },
  {
    "status": 404,
    "received_at": "2026-11-05T20:28:26.408789+00:00",
    "headers": {
      "server-timing": "admission;dur=0.09, auth;dur=0.15, home_lookup;dur=185.23, qos;dur=1.97, project;dur=0.02, placement;dur=0.01, execute;dur=1.20, app;dur=188.69;desc=\"gru\"",
      "x-request-id": "01M6PPC0PZYSG1P3A1SZ5K8WDT",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "ok": false,
    "code": "unknown_database",
    "error_type": "NotFoundError",
    "kind": "prior-sql-after-delete",
    "region": "gru",
    "started_at": "2026-11-05T20:28:26.198468+00:00",
    "completed_at": "2026-11-05T20:28:26.409281+00:00",
    "ms": 210.822,
    "database_id": "01M6PK77TX4H0B1D3GQZ73XZCF"
  },
  {
    "status": 404,
    "received_at": "2026-11-05T20:28:26.618319+00:00",
    "headers": {
      "x-request-id": "01M6PPC0XJ1PNM5B22AR138VZQ",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "ok": false,
    "code": "unknown_database",
    "error_type": "NotFoundError",
    "kind": "prior-metadata-after-delete",
    "region": "gru",
    "started_at": "2026-11-05T20:28:26.409394+00:00",
    "completed_at": "2026-11-05T20:28:26.618688+00:00",
    "ms": 209.302,
    "database_id": "01M6PK77TX4H0B1D3GQZ73XZCF"
  },
  {
    "status": 404,
    "received_at": "2026-11-05T20:28:27.374688+00:00",
    "headers": {
      "server-timing": "admission;dur=0.13, auth;dur=0.17, home_lookup;dur=170.35, qos;dur=1.50, project;dur=0.04, placement;dur=0.01, execute;dur=0.96, app;dur=173.18;desc=\"sjc\"",
      "x-request-id": "01M6PPC1K1CTP6ME8NMZQ3BSF5",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "ok": false,
    "code": "unknown_database",
    "error_type": "NotFoundError",
    "kind": "prior-sql-after-delete",
    "region": "sjc",
    "started_at": "2026-11-05T20:28:26.618781+00:00",
    "completed_at": "2026-11-05T20:28:27.375301+00:00",
    "ms": 756.527,
    "database_id": "01M6PK77TX4H0B1D3GQZ73XZCF"
  },
  {
    "status": 404,
    "received_at": "2026-11-05T20:28:27.739249+00:00",
    "headers": {
      "x-request-id": "01M6PPC1YHT5P41V0H55DG4JJ3",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "ok": false,
    "code": "unknown_database",
    "error_type": "NotFoundError",
    "kind": "prior-metadata-after-delete",
    "region": "sjc",
    "started_at": "2026-11-05T20:28:27.375406+00:00",
    "completed_at": "2026-11-05T20:28:27.739548+00:00",
    "ms": 364.148,
    "database_id": "01M6PK77TX4H0B1D3GQZ73XZCF"
  },
  {
    "status": 404,
    "received_at": "2026-11-05T20:28:28.133734+00:00",
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.11, home_lookup;dur=184.46, qos;dur=1.42, project;dur=0.03, placement;dur=0.01, execute;dur=1.01, app;dur=187.18;desc=\"fra\"",
      "x-request-id": "01M6PPC2A2WX2V620SJD137MMB",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "ok": false,
    "code": "unknown_database",
    "error_type": "NotFoundError",
    "kind": "prior-sql-after-delete",
    "region": "fra",
    "started_at": "2026-11-05T20:28:27.739624+00:00",
    "completed_at": "2026-11-05T20:28:28.134004+00:00",
    "ms": 394.387,
    "database_id": "01M6PK77TX4H0B1D3GQZ73XZCF"
  },
  {
    "status": 404,
    "received_at": "2026-11-05T20:28:28.524021+00:00",
    "headers": {
      "x-request-id": "01M6PPC2PB0XMV38NGRHZ1C5HG",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "ok": false,
    "code": "unknown_database",
    "error_type": "NotFoundError",
    "kind": "prior-metadata-after-delete",
    "region": "fra",
    "started_at": "2026-11-05T20:28:28.134074+00:00",
    "completed_at": "2026-11-05T20:28:28.524541+00:00",
    "ms": 390.478,
    "database_id": "01M6PK77TX4H0B1D3GQZ73XZCF"
  },
  {
    "status": 200,
    "received_at": "2026-11-05T20:28:29.011475+00:00",
    "headers": {
      "x-request-id": "01M6PPC30JY0GHVWA798Q94W0T",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "ok": true,
    "kind": "final-key-before-delete",
    "region": "gru",
    "started_at": "2026-11-05T20:28:28.554069+00:00",
    "completed_at": "2026-11-05T20:28:29.013215+00:00",
    "ms": 459.152
  },
  {
    "status": 200,
    "received_at": "2026-11-05T20:28:29.379846+00:00",
    "headers": {
      "server-timing": "home;dur=169.5;desc=\"gru\"",
      "x-request-id": "01M6PPC3HQT7NPGEYZZ6937336",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "ok": true,
    "kind": "final-key-before-delete",
    "region": "sjc",
    "started_at": "2026-11-05T20:28:29.013318+00:00",
    "completed_at": "2026-11-05T20:28:29.380126+00:00",
    "ms": 366.815
  },
  {
    "status": 200,
    "received_at": "2026-11-05T20:28:29.772343+00:00",
    "headers": {
      "server-timing": "home;dur=181.9;desc=\"gru\"",
      "x-request-id": "01M6PPC3X98TX2285DVJPWCMHV",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "ok": true,
    "kind": "final-key-before-delete",
    "region": "fra",
    "started_at": "2026-11-05T20:28:29.380197+00:00",
    "completed_at": "2026-11-05T20:28:29.772811+00:00",
    "ms": 392.62
  },
  {
    "status": 204,
    "received_at": "2026-11-05T20:28:33.875654+00:00",
    "headers": {
      "x-request-id": "01M6PPC46N4E73K9FH29ER9Z1J",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "ok": true,
    "kind": "final-delete-database",
    "region": "gru",
    "started_at": "2026-11-05T20:28:29.772873+00:00",
    "completed_at": "2026-11-05T20:28:33.875825+00:00",
    "ms": 4102.957
  },
  {
    "status": 404,
    "received_at": "2026-11-05T20:28:34.087311+00:00",
    "headers": {
      "server-timing": "admission;dur=0.11, auth;dur=0.11, home_lookup;dur=185.71, qos;dur=1.91, project;dur=0.03, placement;dur=0.01, execute;dur=1.14, app;dur=189.04;desc=\"gru\"",
      "x-request-id": "01M6PPC86WXE08S67BB5Q67RW5",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "ok": false,
    "code": "unknown_database",
    "error_type": "NotFoundError",
    "kind": "final-sql-after-delete",
    "region": "gru",
    "started_at": "2026-11-05T20:28:33.875894+00:00",
    "completed_at": "2026-11-05T20:28:34.087645+00:00",
    "ms": 211.758
  },
  {
    "status": 404,
    "received_at": "2026-11-05T20:28:34.293089+00:00",
    "headers": {
      "x-request-id": "01M6PPC8DG263BB468GWYRM4CM",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "ok": false,
    "code": "unknown_database",
    "error_type": "NotFoundError",
    "kind": "final-metadata-after-delete",
    "region": "gru",
    "started_at": "2026-11-05T20:28:34.087726+00:00",
    "completed_at": "2026-11-05T20:28:34.293512+00:00",
    "ms": 205.793
  },
  {
    "status": 404,
    "received_at": "2026-11-05T20:28:34.499927+00:00",
    "headers": {
      "x-request-id": "01M6PPC8KYY75KWDHAZYAQHDHB",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "ok": true,
    "code": "unknown_database",
    "kind": "final-key-after-delete",
    "region": "gru",
    "started_at": "2026-11-05T20:28:34.293584+00:00",
    "completed_at": "2026-11-05T20:28:34.500203+00:00",
    "ms": 206.625
  },
  {
    "status": 200,
    "received_at": "2026-11-05T20:28:34.708735+00:00",
    "headers": {
      "x-request-id": "01M6PPC8TD7GK5MB58PNZ9Q1FW",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "ok": true,
    "database_ids": [],
    "next_cursor": null,
    "kind": "final-list-after-delete",
    "region": "gru",
    "started_at": "2026-11-05T20:28:34.500266+00:00",
    "completed_at": "2026-11-05T20:28:34.709044+00:00",
    "ms": 208.784
  },
  {
    "status": 404,
    "received_at": "2026-11-05T20:28:35.464883+00:00",
    "headers": {
      "server-timing": "admission;dur=0.12, auth;dur=0.14, home_lookup;dur=171.51, qos;dur=1.65, project;dur=0.03, placement;dur=0.01, execute;dur=1.22, app;dur=174.68;desc=\"sjc\"",
      "x-request-id": "01M6PPC9FW67WGSP4C2FKRHE9D",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "ok": false,
    "code": "unknown_database",
    "error_type": "NotFoundError",
    "kind": "final-sql-after-delete",
    "region": "sjc",
    "started_at": "2026-11-05T20:28:34.709121+00:00",
    "completed_at": "2026-11-05T20:28:35.465204+00:00",
    "ms": 756.089
  },
  {
    "status": 404,
    "received_at": "2026-11-05T20:28:35.828638+00:00",
    "headers": {
      "x-request-id": "01M6PPC9VB29JMF07583T44QT5",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "ok": false,
    "code": "unknown_database",
    "error_type": "NotFoundError",
    "kind": "final-metadata-after-delete",
    "region": "sjc",
    "started_at": "2026-11-05T20:28:35.465280+00:00",
    "completed_at": "2026-11-05T20:28:35.829216+00:00",
    "ms": 363.946
  },
  {
    "status": 404,
    "received_at": "2026-11-05T20:28:36.198295+00:00",
    "headers": {
      "x-request-id": "01M6PPCA6QHBHY9Q54EKRRVHBC",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "ok": true,
    "code": "unknown_database",
    "kind": "final-key-after-delete",
    "region": "sjc",
    "started_at": "2026-11-05T20:28:35.829335+00:00",
    "completed_at": "2026-11-05T20:28:36.198900+00:00",
    "ms": 369.581
  },
  {
    "status": 200,
    "received_at": "2026-11-05T20:28:36.565161+00:00",
    "headers": {
      "x-request-id": "01M6PPCAJ91DT79S6YR51PZCED",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "ok": true,
    "database_ids": [],
    "next_cursor": null,
    "kind": "final-list-after-delete",
    "region": "sjc",
    "started_at": "2026-11-05T20:28:36.199045+00:00",
    "completed_at": "2026-11-05T20:28:36.565543+00:00",
    "ms": 366.505
  },
  {
    "status": 404,
    "received_at": "2026-11-05T20:28:37.387086+00:00",
    "headers": {
      "server-timing": "admission;dur=0.14, auth;dur=0.11, home_lookup;dur=183.41, qos;dur=1.60, project;dur=0.03, placement;dur=0.01, execute;dur=1.09, app;dur=186.41;desc=\"fra\"",
      "x-request-id": "01M6PPCBB5949K45EZTR1MYPXT",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "ok": false,
    "code": "unknown_database",
    "error_type": "NotFoundError",
    "kind": "final-sql-after-delete",
    "region": "fra",
    "started_at": "2026-11-05T20:28:36.565637+00:00",
    "completed_at": "2026-11-05T20:28:37.387415+00:00",
    "ms": 821.784
  },
  {
    "status": 404,
    "received_at": "2026-11-05T20:28:37.784395+00:00",
    "headers": {
      "x-request-id": "01M6PPCBQK9X3MP9PTX9MKKJ07",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "ok": false,
    "code": "unknown_database",
    "error_type": "NotFoundError",
    "kind": "final-metadata-after-delete",
    "region": "fra",
    "started_at": "2026-11-05T20:28:37.387480+00:00",
    "completed_at": "2026-11-05T20:28:37.784704+00:00",
    "ms": 397.23
  },
  {
    "status": 404,
    "received_at": "2026-11-05T20:28:38.184888+00:00",
    "headers": {
      "x-request-id": "01M6PPCC41CM4Y47V8H74VF3S0",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "ok": true,
    "code": "unknown_database",
    "kind": "final-key-after-delete",
    "region": "fra",
    "started_at": "2026-11-05T20:28:37.784807+00:00",
    "completed_at": "2026-11-05T20:28:38.185235+00:00",
    "ms": 400.434
  },
  {
    "status": 200,
    "received_at": "2026-11-05T20:28:38.581135+00:00",
    "headers": {
      "x-request-id": "01M6PPCCGHNHEA2DH2YVCBYHKQ",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "ok": true,
    "database_ids": [],
    "next_cursor": null,
    "kind": "final-list-after-delete",
    "region": "fra",
    "started_at": "2026-11-05T20:28:38.185297+00:00",
    "completed_at": "2026-11-05T20:28:38.581419+00:00",
    "ms": 396.127
  }
]
```

## account-closure.json

```json
{
  "started_at": "2026-11-05T20:30:30.823243+00:00",
  "completed_at": "2026-11-05T20:30:37.789928+00:00",
  "exit": 0,
  "duration_ms": 6966.696,
  "helper_output": "DELETED mb-acceptance-20261105-b1c4",
  "helper_stderr": ""
}
```

## account-closure-denial.json

```json
[
  {
    "response_host": "gru.api.terrascale.tech",
    "status": 401,
    "headers": {
      "x-request-id": "01M6PPH8XBQY1J45YKAFDESYZJ",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "ok": false,
    "code": "invalid_key",
    "error_type": "AuthenticationError",
    "kind": "retained-token-after-account-close",
    "region": "gru",
    "started_at": "2026-11-05T20:31:18.384674+00:00",
    "completed_at": "2026-11-05T20:31:18.457483+00:00",
    "ms": 72.827
  },
  {
    "response_host": "auth.terrascale.tech",
    "status": 401,
    "headers": {
      "x-request-id": "01M6PPH8ZJTP7ZPRQRXZ9CYCWG"
    },
    "ok": false,
    "code": "invalid_client",
    "error_type": "TokenError",
    "kind": "api-key-after-account-close",
    "region": "gru",
    "started_at": "2026-11-05T20:31:18.457668+00:00",
    "completed_at": "2026-11-05T20:31:18.569551+00:00",
    "ms": 111.906
  },
  {
    "response_host": "sjc.api.terrascale.tech",
    "status": 401,
    "headers": {
      "x-request-id": "01M6PPH9H3QJ5KPXH0B0P3D880",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "ok": false,
    "code": "invalid_key",
    "error_type": "AuthenticationError",
    "kind": "retained-token-after-account-close",
    "region": "sjc",
    "started_at": "2026-11-05T20:31:18.585747+00:00",
    "completed_at": "2026-11-05T20:31:19.172245+00:00",
    "ms": 586.513
  },
  {
    "response_host": "auth.terrascale.tech",
    "status": 401,
    "headers": {
      "x-request-id": "01M6PPH9NP3DNNC2B2KBTW9ZR3"
    },
    "ok": false,
    "code": "invalid_client",
    "error_type": "TokenError",
    "kind": "api-key-after-account-close",
    "region": "sjc",
    "started_at": "2026-11-05T20:31:19.172409+00:00",
    "completed_at": "2026-11-05T20:31:19.274499+00:00",
    "ms": 102.111
  },
  {
    "response_host": "fra.api.terrascale.tech",
    "status": 401,
    "headers": {
      "x-request-id": "01M6PPHA8AVE3V9BM05NVZ01RA",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "ok": false,
    "code": "invalid_key",
    "error_type": "AuthenticationError",
    "kind": "retained-token-after-account-close",
    "region": "fra",
    "started_at": "2026-11-05T20:31:19.290648+00:00",
    "completed_at": "2026-11-05T20:31:19.926759+00:00",
    "ms": 636.132
  },
  {
    "response_host": "auth.terrascale.tech",
    "status": 401,
    "headers": {
      "x-request-id": "01M6PPHADF5E36VP2AR6Q17QXM"
    },
    "ok": false,
    "code": "invalid_client",
    "error_type": "TokenError",
    "kind": "api-key-after-account-close",
    "region": "fra",
    "started_at": "2026-11-05T20:31:19.926929+00:00",
    "completed_at": "2026-11-05T20:31:20.038980+00:00",
    "ms": 112.08
  }
]
```

## final-cdn-before-delete.json

```json
{
  "deleted_at": null,
  "coordinator_check_started_at": "2026-11-05T20:27:35.276861+00:00",
  "url_count": 1,
  "repeats": 2,
  "regions": [
    {
      "region": "gru",
      "results": [
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-4b96f5b96783edd0/1f9c30c673ad5c986c636d4b8360c1ea1da3467d319be8e08a8fe384f9eba1bf",
          "n": 0,
          "status": 200,
          "started_at": "2026-11-05T20:27:36.325391+00:00",
          "completed_at": "2026-11-05T20:27:36.562795+00:00",
          "request_ms": 237.404,
          "bytes": 116250,
          "sha256": "4986838237e6aff7bd002c0b1fc284068306ea43c3686902736656310d252f65",
          "headers": {
            "age": null,
            "cf-cache-status": "HIT",
            "cf-ray": "a55e99a0688ef211-GRU",
            "date": "Thu, 05 Nov 2026 20:27:36 GMT"
          }
        },
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-4b96f5b96783edd0/1f9c30c673ad5c986c636d4b8360c1ea1da3467d319be8e08a8fe384f9eba1bf",
          "n": 1,
          "status": 200,
          "started_at": "2026-11-05T20:27:36.562943+00:00",
          "completed_at": "2026-11-05T20:27:36.611070+00:00",
          "request_ms": 48.132,
          "bytes": 116250,
          "sha256": "4986838237e6aff7bd002c0b1fc284068306ea43c3686902736656310d252f65",
          "headers": {
            "age": "147",
            "cf-cache-status": "HIT",
            "cf-ray": "a55e99a1a83e622f-GRU",
            "date": "Thu, 05 Nov 2026 20:27:36 GMT"
          }
        }
      ]
    },
    {
      "region": "sjc",
      "results": [
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-4b96f5b96783edd0/1f9c30c673ad5c986c636d4b8360c1ea1da3467d319be8e08a8fe384f9eba1bf",
          "n": 0,
          "status": 200,
          "started_at": "2026-11-05T20:27:37.902842+00:00",
          "completed_at": "2026-11-05T20:27:38.149279+00:00",
          "request_ms": 246.436,
          "bytes": 116250,
          "sha256": "4986838237e6aff7bd002c0b1fc284068306ea43c3686902736656310d252f65",
          "headers": {
            "age": null,
            "cf-cache-status": "HIT",
            "cf-ray": "a55e99aa39ec7a33-SJC",
            "date": "Thu, 05 Nov 2026 20:27:38 GMT"
          }
        },
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-4b96f5b96783edd0/1f9c30c673ad5c986c636d4b8360c1ea1da3467d319be8e08a8fe384f9eba1bf",
          "n": 1,
          "status": 200,
          "started_at": "2026-11-05T20:27:38.149404+00:00",
          "completed_at": "2026-11-05T20:27:38.208021+00:00",
          "request_ms": 58.622,
          "bytes": 116250,
          "sha256": "4986838237e6aff7bd002c0b1fc284068306ea43c3686902736656310d252f65",
          "headers": {
            "age": "148",
            "cf-cache-status": "HIT",
            "cf-ray": "a55e99ab8e4feb1e-SJC",
            "date": "Thu, 05 Nov 2026 20:27:38 GMT"
          }
        }
      ]
    },
    {
      "region": "fra",
      "results": [
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-4b96f5b96783edd0/1f9c30c673ad5c986c636d4b8360c1ea1da3467d319be8e08a8fe384f9eba1bf",
          "n": 0,
          "status": 200,
          "started_at": "2026-11-05T20:27:38.351341+00:00",
          "completed_at": "2026-11-05T20:27:38.536244+00:00",
          "request_ms": 184.902,
          "bytes": 116250,
          "sha256": "4986838237e6aff7bd002c0b1fc284068306ea43c3686902736656310d252f65",
          "headers": {
            "age": null,
            "cf-cache-status": "HIT",
            "cf-ray": "a55e99ad0d79d2bf-FRA",
            "date": "Thu, 05 Nov 2026 20:27:38 GMT"
          }
        },
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-4b96f5b96783edd0/1f9c30c673ad5c986c636d4b8360c1ea1da3467d319be8e08a8fe384f9eba1bf",
          "n": 1,
          "status": 200,
          "started_at": "2026-11-05T20:27:38.536393+00:00",
          "completed_at": "2026-11-05T20:27:39.172916+00:00",
          "request_ms": 636.529,
          "bytes": 116250,
          "sha256": "4986838237e6aff7bd002c0b1fc284068306ea43c3686902736656310d252f65",
          "headers": {
            "age": "149",
            "cf-cache-status": "HIT",
            "cf-ray": "a55e99b02f18a4cb-GRU",
            "date": "Thu, 05 Nov 2026 20:27:38 GMT"
          }
        }
      ]
    }
  ]
}
```

## final-cdn-after-delete.json

```json
{
  "deleted_at": "2026-11-05T20:28:33.875825+00:00",
  "coordinator_check_started_at": "2026-11-05T20:29:13.187470+00:00",
  "url_count": 1,
  "repeats": 1,
  "regions": [
    {
      "region": "gru",
      "results": [
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-4b96f5b96783edd0/1f9c30c673ad5c986c636d4b8360c1ea1da3467d319be8e08a8fe384f9eba1bf",
          "n": 0,
          "status": 404,
          "started_at": "2026-11-05T20:29:14.186436+00:00",
          "completed_at": "2026-11-05T20:29:14.480732+00:00",
          "request_ms": 294.295,
          "bytes": 27150,
          "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
          "headers": {
            "age": null,
            "cf-cache-status": "BYPASS",
            "cf-ray": "a55e9c03fe68d986-GRU",
            "date": "Thu, 05 Nov 2026 20:29:14 GMT"
          },
          "elapsed_since_delete_s": 40.310611
        }
      ]
    },
    {
      "region": "sjc",
      "results": [
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-4b96f5b96783edd0/1f9c30c673ad5c986c636d4b8360c1ea1da3467d319be8e08a8fe384f9eba1bf",
          "n": 0,
          "status": 404,
          "started_at": "2026-11-05T20:29:15.819802+00:00",
          "completed_at": "2026-11-05T20:29:16.053445+00:00",
          "request_ms": 233.641,
          "bytes": 27150,
          "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
          "headers": {
            "age": null,
            "cf-cache-status": "BYPASS",
            "cf-ray": "a55e9c0e2eacebed-SJC",
            "date": "Thu, 05 Nov 2026 20:29:16 GMT"
          },
          "elapsed_since_delete_s": 41.943977
        }
      ]
    },
    {
      "region": "fra",
      "results": [
        {
          "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-4b96f5b96783edd0/1f9c30c673ad5c986c636d4b8360c1ea1da3467d319be8e08a8fe384f9eba1bf",
          "n": 0,
          "status": 404,
          "started_at": "2026-11-05T20:29:16.235796+00:00",
          "completed_at": "2026-11-05T20:29:16.978211+00:00",
          "request_ms": 742.412,
          "bytes": 27150,
          "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
          "headers": {
            "age": null,
            "cf-cache-status": "BYPASS",
            "cf-ray": "a55e9c131b384568-GRU",
            "date": "Thu, 05 Nov 2026 20:29:16 GMT"
          },
          "elapsed_since_delete_s": 42.359971
        }
      ]
    }
  ]
}
```

## Final shape subscriptions before cleanup

Shipped SDK ddbc3a87d source,960 rows in all3 regions. No epoch-key values are retained.

```json
[
  {
    "kind": "sdk_environment",
    "sdk_revision": "ddbc3a87d7c16cedf918cb3754bad364f7c068eb",
    "at": "2026-11-05T20:25:06.851Z"
  },
  {
    "kind": "shape_result",
    "region": "gru",
    "ok": true,
    "rows": 960,
    "provisional": 0,
    "offset": "3776"
  },
  {
    "kind": "shape_result",
    "region": "sjc",
    "ok": true,
    "rows": 960,
    "provisional": 0,
    "offset": "3776"
  },
  {
    "kind": "shape_result",
    "region": "fra",
    "ok": true,
    "rows": 960,
    "provisional": 0,
    "offset": "3776"
  }
]
```

## Private credential cleanup

```json
{
  "completed_at": "2026-11-05T20:33:56.978786+00:00",
  "beta_private_directory_removed": true,
  "mariogk_dev_private_directory_removed": true,
  "helper_tag_files_remaining": 0,
  "known_credential_scan": "PASS; 56 files, five private values checked before deletion",
  "retained_public_evidence_directory": "/home/mariogk/.t3/worktrees/terrascale/t3-b1c4ae61/benchmark/bench/milestone-b-acceptance-20261105",
  "owned_preview_tabs_remaining": 0
}
```
