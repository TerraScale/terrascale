# CDN deletion baseline: sanitized raw requests

Public response metadata and synthetic resource IDs only. Authorization and key-response bodies were never retained.
The DELETE timestamp is response completion; CDN timestamps are request start (response completion follows by network latency).

```json
[
  {
    "kind": "api",
    "at": "2026-11-05T19:19:52.073522+00:00",
    "completedAt": "2026-11-05T19:19:52.856028+00:00",
    "region": "gru",
    "method": "POST",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases",
    "status": 201,
    "seconds": 0.783,
    "headers": {
      "location": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/cdn1107-final",
      "x-request-id": "01M6PJEF41487ZJ6QNKGY369MN",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:19:52.867790+00:00",
    "completedAt": "2026-11-05T19:19:53.212762+00:00",
    "region": "gru",
    "method": "POST",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/sql/execute",
    "status": 200,
    "seconds": 0.345,
    "headers": {
      "x-request-id": "01M6PJEFTX0Q49R0GAEVBG2WG3",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:19:53.224296+00:00",
    "completedAt": "2026-11-05T19:19:53.402399+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items0/shape?offset=-1",
    "status": 307,
    "seconds": 0.178,
    "headers": {
      "location": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items0/shape?handle=h1-dc54a39af531864a&offset=snap.27.0",
      "tb-shape-handle": "h1-dc54a39af531864a",
      "x-request-id": "01M6PJEG7ZJQVKXA4J0Y3S8HK3",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:19:53.413393+00:00",
    "completedAt": "2026-11-05T19:19:53.554722+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items0/shape?handle=h1-dc54a39af531864a&offset=snap.27.0",
    "status": 200,
    "seconds": 0.141,
    "headers": {
      "tb-shape-handle": "h1-dc54a39af531864a",
      "x-request-id": "01M6PJEGDX4BH7AJ686WG98PXC",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:19:53.566334+00:00",
    "completedAt": "2026-11-05T19:19:53.726813+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items1/shape?offset=-1",
    "status": 307,
    "seconds": 0.161,
    "headers": {
      "location": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items1/shape?handle=h1-915c646fa385238e&offset=snap.27.0",
      "tb-shape-handle": "h1-915c646fa385238e",
      "x-request-id": "01M6PJEGJPKGCR20CKXJQZZHWZ",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:19:53.737856+00:00",
    "completedAt": "2026-11-05T19:19:53.870791+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items1/shape?handle=h1-915c646fa385238e&offset=snap.27.0",
    "status": 200,
    "seconds": 0.133,
    "headers": {
      "tb-shape-handle": "h1-915c646fa385238e",
      "x-request-id": "01M6PJEGR2HFC8MGFFWZA0ERAP",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:19:53.881882+00:00",
    "completedAt": "2026-11-05T19:19:54.043806+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items2/shape?offset=-1",
    "status": 307,
    "seconds": 0.162,
    "headers": {
      "location": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items2/shape?handle=h1-d79d0079dbb1bd4c&offset=snap.27.0",
      "tb-shape-handle": "h1-d79d0079dbb1bd4c",
      "x-request-id": "01M6PJEGWFCVXZ7DP6G6G77TZK",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:19:54.055121+00:00",
    "completedAt": "2026-11-05T19:19:54.188740+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items2/shape?handle=h1-d79d0079dbb1bd4c&offset=snap.27.0",
    "status": 200,
    "seconds": 0.134,
    "headers": {
      "tb-shape-handle": "h1-d79d0079dbb1bd4c",
      "x-request-id": "01M6PJEH1YMA5XAD1ZW6YH9XKX",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:19:54.200207+00:00",
    "completedAt": "2026-11-05T19:19:54.356401+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items3/shape?offset=-1",
    "status": 307,
    "seconds": 0.156,
    "headers": {
      "location": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items3/shape?handle=h1-8aac07cba4d8e926&offset=snap.27.0",
      "tb-shape-handle": "h1-8aac07cba4d8e926",
      "x-request-id": "01M6PJEH6E9BG0G5KK2ZGWGJXR",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:19:54.367851+00:00",
    "completedAt": "2026-11-05T19:19:54.495279+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items3/shape?handle=h1-8aac07cba4d8e926&offset=snap.27.0",
    "status": 200,
    "seconds": 0.127,
    "headers": {
      "tb-shape-handle": "h1-8aac07cba4d8e926",
      "x-request-id": "01M6PJEHBJ1T2XE51EEVW0A9J9",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:19:54.507676+00:00",
    "completedAt": "2026-11-05T19:19:54.662190+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items4/shape?offset=-1",
    "status": 307,
    "seconds": 0.155,
    "headers": {
      "location": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items4/shape?handle=h1-8ff28099593b1e91&offset=snap.27.0",
      "tb-shape-handle": "h1-8ff28099593b1e91",
      "x-request-id": "01M6PJEHG045C5N1433DHQPWRT",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:19:54.674286+00:00",
    "completedAt": "2026-11-05T19:19:54.805857+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items4/shape?handle=h1-8ff28099593b1e91&offset=snap.27.0",
    "status": 200,
    "seconds": 0.132,
    "headers": {
      "tb-shape-handle": "h1-8ff28099593b1e91",
      "x-request-id": "01M6PJEHN8H3N9K6D187SXF2MD",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:19:54.817317+00:00",
    "completedAt": "2026-11-05T19:19:54.976423+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items5/shape?offset=-1",
    "status": 307,
    "seconds": 0.159,
    "headers": {
      "location": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items5/shape?handle=h1-d36455ad6764d242&offset=snap.27.0",
      "tb-shape-handle": "h1-d36455ad6764d242",
      "x-request-id": "01M6PJEHSPBJ81E7WYBHFA02KF",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:19:54.988330+00:00",
    "completedAt": "2026-11-05T19:19:55.119054+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items5/shape?handle=h1-d36455ad6764d242&offset=snap.27.0",
    "status": 200,
    "seconds": 0.131,
    "headers": {
      "tb-shape-handle": "h1-d36455ad6764d242",
      "x-request-id": "01M6PJEHZ2Z80G8WAH81JVN4FF",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:19:55.130546+00:00",
    "completedAt": "2026-11-05T19:19:55.291219+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items6/shape?offset=-1",
    "status": 307,
    "seconds": 0.161,
    "headers": {
      "location": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items6/shape?handle=h1-b7bfee3204a5c65e&offset=snap.27.0",
      "tb-shape-handle": "h1-b7bfee3204a5c65e",
      "x-request-id": "01M6PJEJ3FB6JE4GRM46G9BF2V",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:19:55.302818+00:00",
    "completedAt": "2026-11-05T19:19:55.441642+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items6/shape?handle=h1-b7bfee3204a5c65e&offset=snap.27.0",
    "status": 200,
    "seconds": 0.139,
    "headers": {
      "tb-shape-handle": "h1-b7bfee3204a5c65e",
      "x-request-id": "01M6PJEJ8X1YEYYFDCF585HSFW",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:20:57.742412+00:00",
    "completedAt": "2026-11-05T19:20:57.887052+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items0/shape/manifest?handle=h1-dc54a39af531864a&offset=snap.27.0",
    "status": 200,
    "seconds": 0.145,
    "headers": {
      "x-request-id": "01M6PJGF84J7M8QHMDKB5X5V3Q",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:20:57.898555+00:00",
    "completedAt": "2026-11-05T19:20:58.031681+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items1/shape/manifest?handle=h1-915c646fa385238e&offset=snap.27.0",
    "status": 200,
    "seconds": 0.133,
    "headers": {
      "x-request-id": "01M6PJGFCY04D1AY7972V8EQBR",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:20:58.043067+00:00",
    "completedAt": "2026-11-05T19:20:58.172841+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items2/shape/manifest?handle=h1-d79d0079dbb1bd4c&offset=snap.27.0",
    "status": 200,
    "seconds": 0.13,
    "headers": {
      "x-request-id": "01M6PJGFHGVDXMRWFNT3X8XEV8",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:20:58.184270+00:00",
    "completedAt": "2026-11-05T19:20:58.312445+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items3/shape/manifest?handle=h1-8aac07cba4d8e926&offset=snap.27.0",
    "status": 200,
    "seconds": 0.128,
    "headers": {
      "x-request-id": "01M6PJGFNVRY0QHZZKW5V2SA1H",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:20:58.323774+00:00",
    "completedAt": "2026-11-05T19:20:58.456580+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items4/shape/manifest?handle=h1-8ff28099593b1e91&offset=snap.27.0",
    "status": 200,
    "seconds": 0.133,
    "headers": {
      "x-request-id": "01M6PJGFTA9G1XCNX6FZ1K8RPY",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:20:58.468803+00:00",
    "completedAt": "2026-11-05T19:20:58.600757+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items5/shape/manifest?handle=h1-d36455ad6764d242&offset=snap.27.0",
    "status": 200,
    "seconds": 0.132,
    "headers": {
      "x-request-id": "01M6PJGFYTSGHFT6S8Q01BJHZR",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:20:58.612592+00:00",
    "completedAt": "2026-11-05T19:20:58.743261+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items6/shape/manifest?handle=h1-b7bfee3204a5c65e&offset=snap.27.0",
    "status": 200,
    "seconds": 0.131,
    "headers": {
      "x-request-id": "01M6PJGG38JCK1SQRGJXBT5E7X",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "at": "2026-11-05T19:20:59.851339+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-dc54a39af531864a/e635ed78dc69030c0be4705258cd6c6a5b56fa92de9c0af5ccf049f4e7df0f69",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "4e41923fc6b359176403b0c69bee87be8edc189a2f2529ffbbce5cdc42cf7d0b",
    "headers": {
      "cf-cache-status": "MISS",
      "cf-ray": "a55e380e6b904bbe-GRU",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "gru"
  },
  {
    "at": "2026-11-05T19:21:00.178641+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-dc54a39af531864a/e635ed78dc69030c0be4705258cd6c6a5b56fa92de9c0af5ccf049f4e7df0f69",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "4e41923fc6b359176403b0c69bee87be8edc189a2f2529ffbbce5cdc42cf7d0b",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e38103f9e95c7-GRU",
      "age": "0",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "gru"
  },
  {
    "at": "2026-11-05T19:21:00.217482+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-915c646fa385238e/95438dff77e8bea86acefb325e7cb4f018f3582beec27ebf2827615d08eedae8",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "f3add4055cd9ae52eb2b7f1783d769f78d0f57f6bf516dfe797d67583adb29de",
    "headers": {
      "cf-cache-status": "MISS",
      "cf-ray": "a55e38107cce0116-GRU",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "gru"
  },
  {
    "at": "2026-11-05T19:21:00.524946+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-915c646fa385238e/95438dff77e8bea86acefb325e7cb4f018f3582beec27ebf2827615d08eedae8",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "f3add4055cd9ae52eb2b7f1783d769f78d0f57f6bf516dfe797d67583adb29de",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e38126a77d4c0-GRU",
      "age": "0",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "gru"
  },
  {
    "at": "2026-11-05T19:21:00.565878+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-d79d0079dbb1bd4c/39f169ce8ab5acf8be6ba62adf2f39bc73eb0a47005766da4753af10f82f02b6",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "aeb874f97fa34f8f0218a7a3d3086b171a4716ae5e3ceb87abc75fcb801ea47c",
    "headers": {
      "cf-cache-status": "MISS",
      "cf-ray": "a55e3812995623ac-GRU",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "gru"
  },
  {
    "at": "2026-11-05T19:21:01.034397+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-d79d0079dbb1bd4c/39f169ce8ab5acf8be6ba62adf2f39bc73eb0a47005766da4753af10f82f02b6",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "aeb874f97fa34f8f0218a7a3d3086b171a4716ae5e3ceb87abc75fcb801ea47c",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e3815ada1622b-GRU",
      "age": "0",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "gru"
  },
  {
    "at": "2026-11-05T19:21:01.092130+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-8aac07cba4d8e926/4c9f4b1d6fc0f8178cc4cc0af5022b61f7f80e4cd2e1887fe322aab10641299a",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "275501dd9f11e5921e3b70f603778fa683cc52c115089d9c1e327483b8cff140",
    "headers": {
      "cf-cache-status": "MISS",
      "cf-ray": "a55e3815ec03a181-GRU",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "gru"
  },
  {
    "at": "2026-11-05T19:21:01.406491+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-8aac07cba4d8e926/4c9f4b1d6fc0f8178cc4cc0af5022b61f7f80e4cd2e1887fe322aab10641299a",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "275501dd9f11e5921e3b70f603778fa683cc52c115089d9c1e327483b8cff140",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e3817e9531d38-GRU",
      "age": "0",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "gru"
  },
  {
    "at": "2026-11-05T19:21:01.443780+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-8ff28099593b1e91/2177efae30eb8d2e88305f1a52ebadfea1687aa46de82e6547a684a66b949661",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "4e07ce2f79ff2f1b057aa4cde329e82e3ac6d472d629702490acaaa878f40378",
    "headers": {
      "cf-cache-status": "MISS",
      "cf-ray": "a55e38181e4d8a4f-GRU",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "gru"
  },
  {
    "at": "2026-11-05T19:21:01.704555+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-8ff28099593b1e91/2177efae30eb8d2e88305f1a52ebadfea1687aa46de82e6547a684a66b949661",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "4e07ce2f79ff2f1b057aa4cde329e82e3ac6d472d629702490acaaa878f40378",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e3819b8df346c-GRU",
      "age": "0",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "gru"
  },
  {
    "at": "2026-11-05T19:21:01.742543+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-d36455ad6764d242/4558bf2b3d944b81a5ea094b57655eb4cda130ca9e93295fca3d3cb966650af9",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "2e58859466acf3115f4b7b36307f9538692c0cb8e0f01a6c5b3c2408053b70fe",
    "headers": {
      "cf-cache-status": "MISS",
      "cf-ray": "a55e3819fc23a493-GRU",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "gru"
  },
  {
    "at": "2026-11-05T19:21:02.078301+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-d36455ad6764d242/4558bf2b3d944b81a5ea094b57655eb4cda130ca9e93295fca3d3cb966650af9",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "2e58859466acf3115f4b7b36307f9538692c0cb8e0f01a6c5b3c2408053b70fe",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e381c19f0f20c-GRU",
      "age": "0",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "gru"
  },
  {
    "at": "2026-11-05T19:21:02.110620+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-b7bfee3204a5c65e/9476be6dafe6aa0748e9b14eb5eebbb504f1c3d59e4564ac83dd22511e69ab72",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "403b2fa3d06375837c759562f84d7a70fda15a541b3c87e59d77c3a7ce4ed45d",
    "headers": {
      "cf-cache-status": "MISS",
      "cf-ray": "a55e381c4e59aeba-GRU",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "gru"
  },
  {
    "at": "2026-11-05T19:21:02.511773+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-b7bfee3204a5c65e/9476be6dafe6aa0748e9b14eb5eebbb504f1c3d59e4564ac83dd22511e69ab72",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "403b2fa3d06375837c759562f84d7a70fda15a541b3c87e59d77c3a7ce4ed45d",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e381ec9ee4eb1-GRU",
      "age": "0",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "gru"
  },
  {
    "at": "2026-11-05T19:21:01.870739+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-dc54a39af531864a/e635ed78dc69030c0be4705258cd6c6a5b56fa92de9c0af5ccf049f4e7df0f69",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "4e41923fc6b359176403b0c69bee87be8edc189a2f2529ffbbce5cdc42cf7d0b",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e381b39f7fb44-SJC",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "sjc"
  },
  {
    "at": "2026-11-05T19:21:02.084354+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-dc54a39af531864a/e635ed78dc69030c0be4705258cd6c6a5b56fa92de9c0af5ccf049f4e7df0f69",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "4e41923fc6b359176403b0c69bee87be8edc189a2f2529ffbbce5cdc42cf7d0b",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e381c1c3fea80-SJC",
      "age": "2",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "sjc"
  },
  {
    "at": "2026-11-05T19:21:02.135800+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-915c646fa385238e/95438dff77e8bea86acefb325e7cb4f018f3582beec27ebf2827615d08eedae8",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "f3add4055cd9ae52eb2b7f1783d769f78d0f57f6bf516dfe797d67583adb29de",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e381c7fc6c6bb-SJC",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "sjc"
  },
  {
    "at": "2026-11-05T19:21:02.271176+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-915c646fa385238e/95438dff77e8bea86acefb325e7cb4f018f3582beec27ebf2827615d08eedae8",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "f3add4055cd9ae52eb2b7f1783d769f78d0f57f6bf516dfe797d67583adb29de",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e381d4d9df378-SJC",
      "age": "2",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "sjc"
  },
  {
    "at": "2026-11-05T19:21:02.542881+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-d79d0079dbb1bd4c/39f169ce8ab5acf8be6ba62adf2f39bc73eb0a47005766da4753af10f82f02b6",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "aeb874f97fa34f8f0218a7a3d3086b171a4716ae5e3ceb87abc75fcb801ea47c",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e381f08e0f97b-SJC",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "sjc"
  },
  {
    "at": "2026-11-05T19:21:02.726970+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-d79d0079dbb1bd4c/39f169ce8ab5acf8be6ba62adf2f39bc73eb0a47005766da4753af10f82f02b6",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "aeb874f97fa34f8f0218a7a3d3086b171a4716ae5e3ceb87abc75fcb801ea47c",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e38204d38173e-SJC",
      "age": "1",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "sjc"
  },
  {
    "at": "2026-11-05T19:21:02.810047+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-8aac07cba4d8e926/4c9f4b1d6fc0f8178cc4cc0af5022b61f7f80e4cd2e1887fe322aab10641299a",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "275501dd9f11e5921e3b70f603778fa683cc52c115089d9c1e327483b8cff140",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e3820a9037834-SJC",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "sjc"
  },
  {
    "at": "2026-11-05T19:21:02.955447+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-8aac07cba4d8e926/4c9f4b1d6fc0f8178cc4cc0af5022b61f7f80e4cd2e1887fe322aab10641299a",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "275501dd9f11e5921e3b70f603778fa683cc52c115089d9c1e327483b8cff140",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e38219ca9a661-SJC",
      "age": "1",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "sjc"
  },
  {
    "at": "2026-11-05T19:21:03.015634+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-8ff28099593b1e91/2177efae30eb8d2e88305f1a52ebadfea1687aa46de82e6547a684a66b949661",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "4e07ce2f79ff2f1b057aa4cde329e82e3ac6d472d629702490acaaa878f40378",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e3821fac115c4-SJC",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "sjc"
  },
  {
    "at": "2026-11-05T19:21:03.150971+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-8ff28099593b1e91/2177efae30eb8d2e88305f1a52ebadfea1687aa46de82e6547a684a66b949661",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "4e07ce2f79ff2f1b057aa4cde329e82e3ac6d472d629702490acaaa878f40378",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e3822ccebf64e-SJC",
      "age": "1",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "sjc"
  },
  {
    "at": "2026-11-05T19:21:03.207505+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-d36455ad6764d242/4558bf2b3d944b81a5ea094b57655eb4cda130ca9e93295fca3d3cb966650af9",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "2e58859466acf3115f4b7b36307f9538692c0cb8e0f01a6c5b3c2408053b70fe",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e38232e5622ae-SJC",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "sjc"
  },
  {
    "at": "2026-11-05T19:21:03.358195+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-d36455ad6764d242/4558bf2b3d944b81a5ea094b57655eb4cda130ca9e93295fca3d3cb966650af9",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "2e58859466acf3115f4b7b36307f9538692c0cb8e0f01a6c5b3c2408053b70fe",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e38241f38d59a-SJC",
      "age": "1",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "sjc"
  },
  {
    "at": "2026-11-05T19:21:03.424517+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-b7bfee3204a5c65e/9476be6dafe6aa0748e9b14eb5eebbb504f1c3d59e4564ac83dd22511e69ab72",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "403b2fa3d06375837c759562f84d7a70fda15a541b3c87e59d77c3a7ce4ed45d",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e38247b848b3c-SJC",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "sjc"
  },
  {
    "at": "2026-11-05T19:21:03.546796+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-b7bfee3204a5c65e/9476be6dafe6aa0748e9b14eb5eebbb504f1c3d59e4564ac83dd22511e69ab72",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "403b2fa3d06375837c759562f84d7a70fda15a541b3c87e59d77c3a7ce4ed45d",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e38254a87e19d-SJC",
      "age": "1",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "sjc"
  },
  {
    "at": "2026-11-05T19:21:02.057179+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-dc54a39af531864a/e635ed78dc69030c0be4705258cd6c6a5b56fa92de9c0af5ccf049f4e7df0f69",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "4e41923fc6b359176403b0c69bee87be8edc189a2f2529ffbbce5cdc42cf7d0b",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e381c3babd28a-FRA",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "fra"
  },
  {
    "at": "2026-11-05T19:21:02.252172+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-dc54a39af531864a/e635ed78dc69030c0be4705258cd6c6a5b56fa92de9c0af5ccf049f4e7df0f69",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "4e41923fc6b359176403b0c69bee87be8edc189a2f2529ffbbce5cdc42cf7d0b",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e381d38c5d2ee-FRA",
      "age": "2",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "fra"
  },
  {
    "at": "2026-11-05T19:21:02.303594+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-915c646fa385238e/95438dff77e8bea86acefb325e7cb4f018f3582beec27ebf2827615d08eedae8",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "f3add4055cd9ae52eb2b7f1783d769f78d0f57f6bf516dfe797d67583adb29de",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e381d8ed8dc5a-FRA",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "fra"
  },
  {
    "at": "2026-11-05T19:21:02.465383+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-915c646fa385238e/95438dff77e8bea86acefb325e7cb4f018f3582beec27ebf2827615d08eedae8",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "f3add4055cd9ae52eb2b7f1783d769f78d0f57f6bf516dfe797d67583adb29de",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e381e7adf7641-FRA",
      "age": "2",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "fra"
  },
  {
    "at": "2026-11-05T19:21:02.524292+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-d79d0079dbb1bd4c/39f169ce8ab5acf8be6ba62adf2f39bc73eb0a47005766da4753af10f82f02b6",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "aeb874f97fa34f8f0218a7a3d3086b171a4716ae5e3ceb87abc75fcb801ea47c",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e381eedadc8d5-FRA",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "fra"
  },
  {
    "at": "2026-11-05T19:21:02.692541+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-d79d0079dbb1bd4c/39f169ce8ab5acf8be6ba62adf2f39bc73eb0a47005766da4753af10f82f02b6",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "aeb874f97fa34f8f0218a7a3d3086b171a4716ae5e3ceb87abc75fcb801ea47c",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e381fffc48f3c-FRA",
      "age": "1",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "fra"
  },
  {
    "at": "2026-11-05T19:21:02.763478+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-8aac07cba4d8e926/4c9f4b1d6fc0f8178cc4cc0af5022b61f7f80e4cd2e1887fe322aab10641299a",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "275501dd9f11e5921e3b70f603778fa683cc52c115089d9c1e327483b8cff140",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e38206a4937e0-FRA",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "fra"
  },
  {
    "at": "2026-11-05T19:21:02.921376+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-8aac07cba4d8e926/4c9f4b1d6fc0f8178cc4cc0af5022b61f7f80e4cd2e1887fe322aab10641299a",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "275501dd9f11e5921e3b70f603778fa683cc52c115089d9c1e327483b8cff140",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e38216fabd3b1-FRA",
      "age": "1",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "fra"
  },
  {
    "at": "2026-11-05T19:21:02.972463+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-8ff28099593b1e91/2177efae30eb8d2e88305f1a52ebadfea1687aa46de82e6547a684a66b949661",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "4e07ce2f79ff2f1b057aa4cde329e82e3ac6d472d629702490acaaa878f40378",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e3821a9f7909a-FRA",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "fra"
  },
  {
    "at": "2026-11-05T19:21:03.117578+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-8ff28099593b1e91/2177efae30eb8d2e88305f1a52ebadfea1687aa46de82e6547a684a66b949661",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "4e07ce2f79ff2f1b057aa4cde329e82e3ac6d472d629702490acaaa878f40378",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e38229a2fdaff-FRA",
      "age": "1",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "fra"
  },
  {
    "at": "2026-11-05T19:21:03.165975+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-d36455ad6764d242/4558bf2b3d944b81a5ea094b57655eb4cda130ca9e93295fca3d3cb966650af9",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "2e58859466acf3115f4b7b36307f9538692c0cb8e0f01a6c5b3c2408053b70fe",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e3822ec50dc88-FRA",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "fra"
  },
  {
    "at": "2026-11-05T19:21:03.336286+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-d36455ad6764d242/4558bf2b3d944b81a5ea094b57655eb4cda130ca9e93295fca3d3cb966650af9",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "2e58859466acf3115f4b7b36307f9538692c0cb8e0f01a6c5b3c2408053b70fe",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e3823f995dc9d-FRA",
      "age": "1",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "fra"
  },
  {
    "at": "2026-11-05T19:21:03.407010+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-b7bfee3204a5c65e/9476be6dafe6aa0748e9b14eb5eebbb504f1c3d59e4564ac83dd22511e69ab72",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "403b2fa3d06375837c759562f84d7a70fda15a541b3c87e59d77c3a7ce4ed45d",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e38246ba8d362-FRA",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "fra"
  },
  {
    "at": "2026-11-05T19:21:03.577257+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-b7bfee3204a5c65e/9476be6dafe6aa0748e9b14eb5eebbb504f1c3d59e4564ac83dd22511e69ab72",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "403b2fa3d06375837c759562f84d7a70fda15a541b3c87e59d77c3a7ce4ed45d",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e382579b5c614-FRA",
      "age": "1",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "fra"
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:21:44.363121+00:00",
    "completedAt": "2026-11-05T19:21:44.505647+00:00",
    "region": "gru",
    "method": "POST",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/sql/execute",
    "status": 200,
    "seconds": 0.143,
    "headers": {
      "x-request-id": "01M6PJHWRZ46V4RZVYC9V8DYS8",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:21:44.517201+00:00",
    "completedAt": "2026-11-05T19:21:45.533305+00:00",
    "region": "sjc",
    "method": "POST",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/sql/execute",
    "status": 200,
    "seconds": 1.016,
    "headers": {
      "x-request-id": "01M6PJHXGKQDSZ9G1WYJ5Q87EG",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:21:45.545905+00:00",
    "completedAt": "2026-11-05T19:21:46.645995+00:00",
    "region": "fra",
    "method": "POST",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/sql/execute",
    "status": 200,
    "seconds": 1.1,
    "headers": {
      "x-request-id": "01M6PJHYJRXYBB7JPSWQMEAAYN",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:21:46.657236+00:00",
    "completedAt": "2026-11-05T19:21:48.444587+00:00",
    "region": "gru",
    "method": "POST",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/move-home",
    "status": 200,
    "seconds": 1.787,
    "headers": {
      "x-request-id": "01M6PJHZ0PZ6NTMZARGJBN2H1T",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:21:48.748228+00:00",
    "completedAt": "2026-11-05T19:21:53.860886+00:00",
    "region": "fra",
    "method": "DELETE",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8",
    "status": 204,
    "seconds": 5.113,
    "headers": {
      "x-request-id": "01M6PJJ1PK4QPBQ6CYT9S3QKR4",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:21:53.891054+00:00",
    "completedAt": "2026-11-05T19:21:54.232872+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8",
    "status": 404,
    "seconds": 0.342,
    "headers": {
      "x-request-id": "01M6PJJ62T3MSV5BWM8P1Y72GT",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "error": "unknown_database"
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:21:54.245555+00:00",
    "completedAt": "2026-11-05T19:21:54.566750+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items0/shape/keys?handle=h1-dc54a39af531864a&epoch=2989843&key_id=01M6PJEG8QEP6TGQYTR020MNKQ",
    "status": 404,
    "seconds": 0.321,
    "headers": {
      "x-request-id": "01M6PJJ6DTJRYQTJ399EPW3FQC",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:21:54.579071+00:00",
    "completedAt": "2026-11-05T19:21:54.899996+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items1/shape/keys?handle=h1-915c646fa385238e&epoch=2989843&key_id=01M6PJEG8QEP6TGQYTR020MNKQ",
    "status": 404,
    "seconds": 0.321,
    "headers": {
      "x-request-id": "01M6PJJ6R703D5Q1MD1SRFMBS7",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:21:54.912732+00:00",
    "completedAt": "2026-11-05T19:21:55.232530+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items2/shape/keys?handle=h1-d79d0079dbb1bd4c&epoch=2989843&key_id=01M6PJEG8QEP6TGQYTR020MNKQ",
    "status": 404,
    "seconds": 0.32,
    "headers": {
      "x-request-id": "01M6PJJ72Q2AQPCW741M8NDNHB",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:21:55.247756+00:00",
    "completedAt": "2026-11-05T19:21:55.579550+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items3/shape/keys?handle=h1-8aac07cba4d8e926&epoch=2989843&key_id=01M6PJEG8QEP6TGQYTR020MNKQ",
    "status": 404,
    "seconds": 0.332,
    "headers": {
      "x-request-id": "01M6PJJ7DHQCQ29960XEESRKMK",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:21:55.595385+00:00",
    "completedAt": "2026-11-05T19:21:55.986186+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items4/shape/keys?handle=h1-8ff28099593b1e91&epoch=2989843&key_id=01M6PJEG8QEP6TGQYTR020MNKQ",
    "status": 404,
    "seconds": 0.391,
    "headers": {
      "x-request-id": "01M6PJJ7T6E9GWFENDWCQDBKGR",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:21:56.000047+00:00",
    "completedAt": "2026-11-05T19:21:56.323501+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items5/shape/keys?handle=h1-d36455ad6764d242&epoch=2989843&key_id=01M6PJEG8QEP6TGQYTR020MNKQ",
    "status": 404,
    "seconds": 0.323,
    "headers": {
      "x-request-id": "01M6PJJ84PSZ90GWTFPNPGZ9CJ",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:21:56.336868+00:00",
    "completedAt": "2026-11-05T19:21:56.659907+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items6/shape/keys?handle=h1-b7bfee3204a5c65e&epoch=2989843&key_id=01M6PJEG8QEP6TGQYTR020MNKQ",
    "status": 404,
    "seconds": 0.323,
    "headers": {
      "x-request-id": "01M6PJJ8FA8BJEM8386H3JQHJ5",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:21:56.674879+00:00",
    "completedAt": "2026-11-05T19:21:58.012517+00:00",
    "region": "sjc",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8",
    "status": 404,
    "seconds": 1.338,
    "headers": {
      "x-request-id": "01M6PJJ9CJZA1PS3TNXHNQDG02",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "error": "unknown_database"
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:21:58.028553+00:00",
    "completedAt": "2026-11-05T19:21:59.226569+00:00",
    "region": "sjc",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items0/shape/keys?handle=h1-dc54a39af531864a&epoch=2989843&key_id=01M6PJEG8QEP6TGQYTR020MNKQ",
    "status": 404,
    "seconds": 1.198,
    "headers": {
      "x-request-id": "01M6PJJAPYVCVG0BAAFVHPC4SP",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:21:59.238811+00:00",
    "completedAt": "2026-11-05T19:22:00.438118+00:00",
    "region": "sjc",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items1/shape/keys?handle=h1-915c646fa385238e&epoch=2989843&key_id=01M6PJEG8QEP6TGQYTR020MNKQ",
    "status": 404,
    "seconds": 1.199,
    "headers": {
      "x-request-id": "01M6PJJBWNYNEMFYRBKHD84MD3",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:22:00.456869+00:00",
    "completedAt": "2026-11-05T19:22:01.657016+00:00",
    "region": "sjc",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items2/shape/keys?handle=h1-d79d0079dbb1bd4c&epoch=2989843&key_id=01M6PJEG8QEP6TGQYTR020MNKQ",
    "status": 404,
    "seconds": 1.2,
    "headers": {
      "x-request-id": "01M6PJJD2VWY17XABBZWJW240Y",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:22:01.670853+00:00",
    "completedAt": "2026-11-05T19:22:02.864021+00:00",
    "region": "sjc",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items3/shape/keys?handle=h1-8aac07cba4d8e926&epoch=2989843&key_id=01M6PJEG8QEP6TGQYTR020MNKQ",
    "status": 404,
    "seconds": 1.193,
    "headers": {
      "x-request-id": "01M6PJJE8N1Z0VJTNFBV6YWVCZ",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:22:02.878580+00:00",
    "completedAt": "2026-11-05T19:22:04.074605+00:00",
    "region": "sjc",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items4/shape/keys?handle=h1-8ff28099593b1e91&epoch=2989843&key_id=01M6PJEG8QEP6TGQYTR020MNKQ",
    "status": 404,
    "seconds": 1.196,
    "headers": {
      "x-request-id": "01M6PJJFEFBXWP8DSVJ3QFZ8CT",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:22:04.088084+00:00",
    "completedAt": "2026-11-05T19:22:05.289116+00:00",
    "region": "sjc",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items5/shape/keys?handle=h1-d36455ad6764d242&epoch=2989843&key_id=01M6PJEG8QEP6TGQYTR020MNKQ",
    "status": 404,
    "seconds": 1.201,
    "headers": {
      "x-request-id": "01M6PJJGMFFK739K61M00BHYJW",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:22:05.302496+00:00",
    "completedAt": "2026-11-05T19:22:06.501534+00:00",
    "region": "sjc",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items6/shape/keys?handle=h1-b7bfee3204a5c65e&epoch=2989843&key_id=01M6PJEG8QEP6TGQYTR020MNKQ",
    "status": 404,
    "seconds": 1.199,
    "headers": {
      "x-request-id": "01M6PJJHT6EB9MC8W52K141SXA",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:22:06.515694+00:00",
    "completedAt": "2026-11-05T19:22:07.925681+00:00",
    "region": "fra",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8",
    "status": 404,
    "seconds": 1.41,
    "headers": {
      "x-request-id": "01M6PJJK1TBYWGNRN3YPF3SDYQ",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "error": "unknown_database"
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:22:07.936758+00:00",
    "completedAt": "2026-11-05T19:22:09.207244+00:00",
    "region": "fra",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items0/shape/keys?handle=h1-dc54a39af531864a&epoch=2989843&key_id=01M6PJEG8QEP6TGQYTR020MNKQ",
    "status": 404,
    "seconds": 1.271,
    "headers": {
      "x-request-id": "01M6PJJME8GX6APNGKP0Z781C5",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:22:09.218961+00:00",
    "completedAt": "2026-11-05T19:22:10.484977+00:00",
    "region": "fra",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items1/shape/keys?handle=h1-915c646fa385238e&epoch=2989843&key_id=01M6PJEG8QEP6TGQYTR020MNKQ",
    "status": 404,
    "seconds": 1.266,
    "headers": {
      "x-request-id": "01M6PJJNP8FB8YV9Y3STQVQ9TF",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:22:10.502134+00:00",
    "completedAt": "2026-11-05T19:22:11.767207+00:00",
    "region": "fra",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items2/shape/keys?handle=h1-d79d0079dbb1bd4c&epoch=2989843&key_id=01M6PJEG8QEP6TGQYTR020MNKQ",
    "status": 404,
    "seconds": 1.265,
    "headers": {
      "x-request-id": "01M6PJJPYCJQY89EAD8D69R2PN",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:22:11.778709+00:00",
    "completedAt": "2026-11-05T19:22:13.039264+00:00",
    "region": "fra",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items3/shape/keys?handle=h1-8aac07cba4d8e926&epoch=2989843&key_id=01M6PJEG8QEP6TGQYTR020MNKQ",
    "status": 404,
    "seconds": 1.261,
    "headers": {
      "x-request-id": "01M6PJJR67A585ZGM3S1J8XDFZ",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:22:13.051002+00:00",
    "completedAt": "2026-11-05T19:22:14.315001+00:00",
    "region": "fra",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items4/shape/keys?handle=h1-8ff28099593b1e91&epoch=2989843&key_id=01M6PJEG8QEP6TGQYTR020MNKQ",
    "status": 404,
    "seconds": 1.264,
    "headers": {
      "x-request-id": "01M6PJJSE08ZJFJNXSVCN0Q51A",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:22:14.332284+00:00",
    "completedAt": "2026-11-05T19:22:15.596040+00:00",
    "region": "fra",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items5/shape/keys?handle=h1-d36455ad6764d242&epoch=2989843&key_id=01M6PJEG8QEP6TGQYTR020MNKQ",
    "status": 404,
    "seconds": 1.264,
    "headers": {
      "x-request-id": "01M6PJJTP4V5Q66PV8NPH1Y8YA",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:22:15.612859+00:00",
    "completedAt": "2026-11-05T19:22:16.880349+00:00",
    "region": "fra",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items6/shape/keys?handle=h1-b7bfee3204a5c65e&epoch=2989843&key_id=01M6PJEG8QEP6TGQYTR020MNKQ",
    "status": 404,
    "seconds": 1.268,
    "headers": {
      "x-request-id": "01M6PJJVY50B19Y9PZ3Y447A3T",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    }
  },
  {
    "at": "2026-11-05T19:22:17.984591+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-dc54a39af531864a/e635ed78dc69030c0be4705258cd6c6a5b56fa92de9c0af5ccf049f4e7df0f69",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "4e41923fc6b359176403b0c69bee87be8edc189a2f2529ffbbce5cdc42cf7d0b",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e39f6a87ef22d-GRU",
      "age": "77",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-probe",
    "region": "gru",
    "secondsAfterDelete": 24.123705
  },
  {
    "at": "2026-11-05T19:22:18.063939+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-915c646fa385238e/95438dff77e8bea86acefb325e7cb4f018f3582beec27ebf2827615d08eedae8",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "f3add4055cd9ae52eb2b7f1783d769f78d0f57f6bf516dfe797d67583adb29de",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e39f6fed71d1f-GRU",
      "age": "77",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-probe",
    "region": "gru",
    "secondsAfterDelete": 24.203053
  },
  {
    "at": "2026-11-05T19:22:18.101970+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-d79d0079dbb1bd4c/39f169ce8ab5acf8be6ba62adf2f39bc73eb0a47005766da4753af10f82f02b6",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "aeb874f97fa34f8f0218a7a3d3086b171a4716ae5e3ceb87abc75fcb801ea47c",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e39f73d4daa58-GRU",
      "age": "77",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-probe",
    "region": "gru",
    "secondsAfterDelete": 24.241084
  },
  {
    "at": "2026-11-05T19:22:18.139800+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-8aac07cba4d8e926/4c9f4b1d6fc0f8178cc4cc0af5022b61f7f80e4cd2e1887fe322aab10641299a",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "275501dd9f11e5921e3b70f603778fa683cc52c115089d9c1e327483b8cff140",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e39f77f13f1ea-GRU",
      "age": "76",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-probe",
    "region": "gru",
    "secondsAfterDelete": 24.278914
  },
  {
    "at": "2026-11-05T19:22:18.186081+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-8ff28099593b1e91/2177efae30eb8d2e88305f1a52ebadfea1687aa46de82e6547a684a66b949661",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "4e07ce2f79ff2f1b057aa4cde329e82e3ac6d472d629702490acaaa878f40378",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e39f7ba59f1bb-GRU",
      "age": "76",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-probe",
    "region": "gru",
    "secondsAfterDelete": 24.325195
  },
  {
    "at": "2026-11-05T19:22:18.218640+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-d36455ad6764d242/4558bf2b3d944b81a5ea094b57655eb4cda130ca9e93295fca3d3cb966650af9",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "2e58859466acf3115f4b7b36307f9538692c0cb8e0f01a6c5b3c2408053b70fe",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e39f7f9d21da1-GRU",
      "age": "76",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-probe",
    "region": "gru",
    "secondsAfterDelete": 24.357754
  },
  {
    "at": "2026-11-05T19:22:18.259356+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-b7bfee3204a5c65e/9476be6dafe6aa0748e9b14eb5eebbb504f1c3d59e4564ac83dd22511e69ab72",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "403b2fa3d06375837c759562f84d7a70fda15a541b3c87e59d77c3a7ce4ed45d",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e39f83fa74931-GRU",
      "age": "75",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-probe",
    "region": "gru",
    "secondsAfterDelete": 24.39847
  },
  {
    "at": "2026-11-05T19:22:19.973184+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-dc54a39af531864a/e635ed78dc69030c0be4705258cd6c6a5b56fa92de9c0af5ccf049f4e7df0f69",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "4e41923fc6b359176403b0c69bee87be8edc189a2f2529ffbbce5cdc42cf7d0b",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e3a032f5b7abb-SJC",
      "age": "79",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-probe",
    "region": "sjc",
    "secondsAfterDelete": 26.112298
  },
  {
    "at": "2026-11-05T19:22:20.055260+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-915c646fa385238e/95438dff77e8bea86acefb325e7cb4f018f3582beec27ebf2827615d08eedae8",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "f3add4055cd9ae52eb2b7f1783d769f78d0f57f6bf516dfe797d67583adb29de",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e3a0378213911-SJC",
      "age": "79",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-probe",
    "region": "sjc",
    "secondsAfterDelete": 26.194374
  },
  {
    "at": "2026-11-05T19:22:20.106858+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-d79d0079dbb1bd4c/39f169ce8ab5acf8be6ba62adf2f39bc73eb0a47005766da4753af10f82f02b6",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "aeb874f97fa34f8f0218a7a3d3086b171a4716ae5e3ceb87abc75fcb801ea47c",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e3a03c86391f0-SJC",
      "age": "79",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-probe",
    "region": "sjc",
    "secondsAfterDelete": 26.245972
  },
  {
    "at": "2026-11-05T19:22:20.205515+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-8aac07cba4d8e926/4c9f4b1d6fc0f8178cc4cc0af5022b61f7f80e4cd2e1887fe322aab10641299a",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "275501dd9f11e5921e3b70f603778fa683cc52c115089d9c1e327483b8cff140",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e3a045b6edf26-SJC",
      "age": "78",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-probe",
    "region": "sjc",
    "secondsAfterDelete": 26.344629
  },
  {
    "at": "2026-11-05T19:22:20.245748+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-8ff28099593b1e91/2177efae30eb8d2e88305f1a52ebadfea1687aa46de82e6547a684a66b949661",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "4e07ce2f79ff2f1b057aa4cde329e82e3ac6d472d629702490acaaa878f40378",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e3a04aa4c1321-SJC",
      "age": "78",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-probe",
    "region": "sjc",
    "secondsAfterDelete": 26.384862
  },
  {
    "at": "2026-11-05T19:22:20.318777+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-d36455ad6764d242/4558bf2b3d944b81a5ea094b57655eb4cda130ca9e93295fca3d3cb966650af9",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "2e58859466acf3115f4b7b36307f9538692c0cb8e0f01a6c5b3c2408053b70fe",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e3a0518b81754-SJC",
      "age": "78",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-probe",
    "region": "sjc",
    "secondsAfterDelete": 26.457891
  },
  {
    "at": "2026-11-05T19:22:20.401276+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-b7bfee3204a5c65e/9476be6dafe6aa0748e9b14eb5eebbb504f1c3d59e4564ac83dd22511e69ab72",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "403b2fa3d06375837c759562f84d7a70fda15a541b3c87e59d77c3a7ce4ed45d",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e3a059bf47aaf-SJC",
      "age": "78",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-probe",
    "region": "sjc",
    "secondsAfterDelete": 26.54039
  },
  {
    "at": "2026-11-05T19:22:20.132093+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-dc54a39af531864a/e635ed78dc69030c0be4705258cd6c6a5b56fa92de9c0af5ccf049f4e7df0f69",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "4e41923fc6b359176403b0c69bee87be8edc189a2f2529ffbbce5cdc42cf7d0b",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e3a042f78dbf0-FRA",
      "age": "80",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-probe",
    "region": "fra",
    "secondsAfterDelete": 26.271207
  },
  {
    "at": "2026-11-05T19:22:20.233396+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-915c646fa385238e/95438dff77e8bea86acefb325e7cb4f018f3582beec27ebf2827615d08eedae8",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "f3add4055cd9ae52eb2b7f1783d769f78d0f57f6bf516dfe797d67583adb29de",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e3a048c9bd274-FRA",
      "age": "79",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-probe",
    "region": "fra",
    "secondsAfterDelete": 26.37251
  },
  {
    "at": "2026-11-05T19:22:20.280514+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-d79d0079dbb1bd4c/39f169ce8ab5acf8be6ba62adf2f39bc73eb0a47005766da4753af10f82f02b6",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "aeb874f97fa34f8f0218a7a3d3086b171a4716ae5e3ceb87abc75fcb801ea47c",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e3a04da885f5b-FRA",
      "age": "79",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-probe",
    "region": "fra",
    "secondsAfterDelete": 26.419628
  },
  {
    "at": "2026-11-05T19:22:20.356921+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-8aac07cba4d8e926/4c9f4b1d6fc0f8178cc4cc0af5022b61f7f80e4cd2e1887fe322aab10641299a",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "275501dd9f11e5921e3b70f603778fa683cc52c115089d9c1e327483b8cff140",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e3a055942cd05-FRA",
      "age": "79",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-probe",
    "region": "fra",
    "secondsAfterDelete": 26.496035
  },
  {
    "at": "2026-11-05T19:22:20.415769+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-8ff28099593b1e91/2177efae30eb8d2e88305f1a52ebadfea1687aa46de82e6547a684a66b949661",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "4e07ce2f79ff2f1b057aa4cde329e82e3ac6d472d629702490acaaa878f40378",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e3a05bde0d358-FRA",
      "age": "78",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-probe",
    "region": "fra",
    "secondsAfterDelete": 26.554883
  },
  {
    "at": "2026-11-05T19:22:20.467337+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-d36455ad6764d242/4558bf2b3d944b81a5ea094b57655eb4cda130ca9e93295fca3d3cb966650af9",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "2e58859466acf3115f4b7b36307f9538692c0cb8e0f01a6c5b3c2408053b70fe",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e3a060a4dd278-FRA",
      "age": "78",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-probe",
    "region": "fra",
    "secondsAfterDelete": 26.606451
  },
  {
    "at": "2026-11-05T19:22:20.538322+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-b7bfee3204a5c65e/9476be6dafe6aa0748e9b14eb5eebbb504f1c3d59e4564ac83dd22511e69ab72",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "403b2fa3d06375837c759562f84d7a70fda15a541b3c87e59d77c3a7ce4ed45d",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e3a068966d36c-FRA",
      "age": "78",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-probe",
    "region": "fra",
    "secondsAfterDelete": 26.677436
  },
  {
    "at": "2026-11-05T19:29:04.168556+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-dc54a39af531864a/e635ed78dc69030c0be4705258cd6c6a5b56fa92de9c0af5ccf049f4e7df0f69",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "4e41923fc6b359176403b0c69bee87be8edc189a2f2529ffbbce5cdc42cf7d0b",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e43e16caaf181-GRU",
      "age": "484",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-probe",
    "region": "gru",
    "secondsAfterDelete": 430.30767
  },
  {
    "at": "2026-11-05T19:29:04.249331+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-915c646fa385238e/95438dff77e8bea86acefb325e7cb4f018f3582beec27ebf2827615d08eedae8",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e43e1a800ae6c-GRU",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "gru",
    "secondsAfterDelete": 430.388445
  },
  {
    "at": "2026-11-05T19:29:04.522797+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-d79d0079dbb1bd4c/39f169ce8ab5acf8be6ba62adf2f39bc73eb0a47005766da4753af10f82f02b6",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "aeb874f97fa34f8f0218a7a3d3086b171a4716ae5e3ceb87abc75fcb801ea47c",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e43e358231d2a-GRU",
      "age": "483",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-probe",
    "region": "gru",
    "secondsAfterDelete": 430.661911
  },
  {
    "at": "2026-11-05T19:29:04.567150+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-8aac07cba4d8e926/4c9f4b1d6fc0f8178cc4cc0af5022b61f7f80e4cd2e1887fe322aab10641299a",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e43e3ad7e57e6-GRU",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "gru",
    "secondsAfterDelete": 430.706264
  },
  {
    "at": "2026-11-05T19:29:04.815534+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-8ff28099593b1e91/2177efae30eb8d2e88305f1a52ebadfea1687aa46de82e6547a684a66b949661",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e43e53ca70127-GRU",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "gru",
    "secondsAfterDelete": 430.954648
  },
  {
    "at": "2026-11-05T19:29:05.163919+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-d36455ad6764d242/4558bf2b3d944b81a5ea094b57655eb4cda130ca9e93295fca3d3cb966650af9",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "2e58859466acf3115f4b7b36307f9538692c0cb8e0f01a6c5b3c2408053b70fe",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e43e76b0f8b2a-GRU",
      "age": "483",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-probe",
    "region": "gru",
    "secondsAfterDelete": 431.303033
  },
  {
    "at": "2026-11-05T19:29:05.201168+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-b7bfee3204a5c65e/9476be6dafe6aa0748e9b14eb5eebbb504f1c3d59e4564ac83dd22511e69ab72",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "403b2fa3d06375837c759562f84d7a70fda15a541b3c87e59d77c3a7ce4ed45d",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e43e79aa4d97a-GRU",
      "age": "482",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-probe",
    "region": "gru",
    "secondsAfterDelete": 431.340282
  },
  {
    "at": "2026-11-05T19:29:06.149801+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-dc54a39af531864a/e635ed78dc69030c0be4705258cd6c6a5b56fa92de9c0af5ccf049f4e7df0f69",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "4e41923fc6b359176403b0c69bee87be8edc189a2f2529ffbbce5cdc42cf7d0b",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e43ede81bf9f5-SJC",
      "age": "486",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-probe",
    "region": "sjc",
    "secondsAfterDelete": 432.288915
  },
  {
    "at": "2026-11-05T19:29:06.299017+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-915c646fa385238e/95438dff77e8bea86acefb325e7cb4f018f3582beec27ebf2827615d08eedae8",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e43ee8de92513-SJC",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "sjc",
    "secondsAfterDelete": 432.438131
  },
  {
    "at": "2026-11-05T19:29:06.478368+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-d79d0079dbb1bd4c/39f169ce8ab5acf8be6ba62adf2f39bc73eb0a47005766da4753af10f82f02b6",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "aeb874f97fa34f8f0218a7a3d3086b171a4716ae5e3ceb87abc75fcb801ea47c",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e43efabebe0ce-SJC",
      "age": "485",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-probe",
    "region": "sjc",
    "secondsAfterDelete": 432.617482
  },
  {
    "at": "2026-11-05T19:29:06.583848+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-8aac07cba4d8e926/4c9f4b1d6fc0f8178cc4cc0af5022b61f7f80e4cd2e1887fe322aab10641299a",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e43f03d88ce74-SJC",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "sjc",
    "secondsAfterDelete": 432.722962
  },
  {
    "at": "2026-11-05T19:29:06.839267+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-8ff28099593b1e91/2177efae30eb8d2e88305f1a52ebadfea1687aa46de82e6547a684a66b949661",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e43f1eb9e7ac4-SJC",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "sjc",
    "secondsAfterDelete": 432.978381
  },
  {
    "at": "2026-11-05T19:29:07.242493+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-d36455ad6764d242/4558bf2b3d944b81a5ea094b57655eb4cda130ca9e93295fca3d3cb966650af9",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "2e58859466acf3115f4b7b36307f9538692c0cb8e0f01a6c5b3c2408053b70fe",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e43f45d10963f-SJC",
      "age": "485",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-probe",
    "region": "sjc",
    "secondsAfterDelete": 433.381607
  },
  {
    "at": "2026-11-05T19:29:07.389561+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-b7bfee3204a5c65e/9476be6dafe6aa0748e9b14eb5eebbb504f1c3d59e4564ac83dd22511e69ab72",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "403b2fa3d06375837c759562f84d7a70fda15a541b3c87e59d77c3a7ce4ed45d",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e43f54f5c0fb5-SJC",
      "age": "484",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-probe",
    "region": "sjc",
    "secondsAfterDelete": 433.528675
  },
  {
    "at": "2026-11-05T19:29:06.298155+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-dc54a39af531864a/e635ed78dc69030c0be4705258cd6c6a5b56fa92de9c0af5ccf049f4e7df0f69",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "4e41923fc6b359176403b0c69bee87be8edc189a2f2529ffbbce5cdc42cf7d0b",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e43eebca8dc76-FRA",
      "age": "486",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-probe",
    "region": "fra",
    "secondsAfterDelete": 432.437269
  },
  {
    "at": "2026-11-05T19:29:06.399393+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-915c646fa385238e/95438dff77e8bea86acefb325e7cb4f018f3582beec27ebf2827615d08eedae8",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e43ef1c653a76-FRA",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "fra",
    "secondsAfterDelete": 432.538507
  },
  {
    "at": "2026-11-05T19:29:06.617275+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-d79d0079dbb1bd4c/39f169ce8ab5acf8be6ba62adf2f39bc73eb0a47005766da4753af10f82f02b6",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "aeb874f97fa34f8f0218a7a3d3086b171a4716ae5e3ceb87abc75fcb801ea47c",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e43f099f2df1c-FRA",
      "age": "485",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-probe",
    "region": "fra",
    "secondsAfterDelete": 432.756389
  },
  {
    "at": "2026-11-05T19:29:06.672935+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-8aac07cba4d8e926/4c9f4b1d6fc0f8178cc4cc0af5022b61f7f80e4cd2e1887fe322aab10641299a",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e43f0ced69874-FRA",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "fra",
    "secondsAfterDelete": 432.812049
  },
  {
    "at": "2026-11-05T19:29:06.878943+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-8ff28099593b1e91/2177efae30eb8d2e88305f1a52ebadfea1687aa46de82e6547a684a66b949661",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e43f21e1f4860-FRA",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "fra",
    "secondsAfterDelete": 433.018057
  },
  {
    "at": "2026-11-05T19:29:07.211138+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-d36455ad6764d242/4558bf2b3d944b81a5ea094b57655eb4cda130ca9e93295fca3d3cb966650af9",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "2e58859466acf3115f4b7b36307f9538692c0cb8e0f01a6c5b3c2408053b70fe",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e43f42e2c5d4a-FRA",
      "age": "485",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-probe",
    "region": "fra",
    "secondsAfterDelete": 433.350252
  },
  {
    "at": "2026-11-05T19:29:07.274731+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-b7bfee3204a5c65e/9476be6dafe6aa0748e9b14eb5eebbb504f1c3d59e4564ac83dd22511e69ab72",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "403b2fa3d06375837c759562f84d7a70fda15a541b3c87e59d77c3a7ce4ed45d",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e43f48c753f2c-FRA",
      "age": "484",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-probe",
    "region": "fra",
    "secondsAfterDelete": 433.413845
  }
]
```

Manifest object metadata used to validate cached ciphertext (contains key IDs, never epoch key material):

```json
[
  {
    "offset": "snap.27.0",
    "next_offset": "27",
    "binding": "h1-dc54a39af531864a\u0000snap.27.0",
    "object": "v1/shapes/h1-dc54a39af531864a/e635ed78dc69030c0be4705258cd6c6a5b56fa92de9c0af5ccf049f4e7df0f69",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-dc54a39af531864a/e635ed78dc69030c0be4705258cd6c6a5b56fa92de9c0af5ccf049f4e7df0f69",
    "expires_at": "2026-11-05T19:30:00+00:00",
    "epoch": "2989843",
    "key_id": "01M6PJEG8QEP6TGQYTR020MNKQ",
    "sha256": "4e41923fc6b359176403b0c69bee87be8edc189a2f2529ffbbce5cdc42cf7d0b",
    "size": 2364,
    "shapePath": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items0/shape",
    "handle": "h1-dc54a39af531864a"
  },
  {
    "offset": "snap.27.0",
    "next_offset": "27",
    "binding": "h1-915c646fa385238e\u0000snap.27.0",
    "object": "v1/shapes/h1-915c646fa385238e/95438dff77e8bea86acefb325e7cb4f018f3582beec27ebf2827615d08eedae8",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-915c646fa385238e/95438dff77e8bea86acefb325e7cb4f018f3582beec27ebf2827615d08eedae8",
    "expires_at": "2026-11-05T19:30:00+00:00",
    "epoch": "2989843",
    "key_id": "01M6PJEG8QEP6TGQYTR020MNKQ",
    "sha256": "f3add4055cd9ae52eb2b7f1783d769f78d0f57f6bf516dfe797d67583adb29de",
    "size": 2364,
    "shapePath": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items1/shape",
    "handle": "h1-915c646fa385238e"
  },
  {
    "offset": "snap.27.0",
    "next_offset": "27",
    "binding": "h1-d79d0079dbb1bd4c\u0000snap.27.0",
    "object": "v1/shapes/h1-d79d0079dbb1bd4c/39f169ce8ab5acf8be6ba62adf2f39bc73eb0a47005766da4753af10f82f02b6",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-d79d0079dbb1bd4c/39f169ce8ab5acf8be6ba62adf2f39bc73eb0a47005766da4753af10f82f02b6",
    "expires_at": "2026-11-05T19:30:00+00:00",
    "epoch": "2989843",
    "key_id": "01M6PJEG8QEP6TGQYTR020MNKQ",
    "sha256": "aeb874f97fa34f8f0218a7a3d3086b171a4716ae5e3ceb87abc75fcb801ea47c",
    "size": 2364,
    "shapePath": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items2/shape",
    "handle": "h1-d79d0079dbb1bd4c"
  },
  {
    "offset": "snap.27.0",
    "next_offset": "27",
    "binding": "h1-8aac07cba4d8e926\u0000snap.27.0",
    "object": "v1/shapes/h1-8aac07cba4d8e926/4c9f4b1d6fc0f8178cc4cc0af5022b61f7f80e4cd2e1887fe322aab10641299a",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-8aac07cba4d8e926/4c9f4b1d6fc0f8178cc4cc0af5022b61f7f80e4cd2e1887fe322aab10641299a",
    "expires_at": "2026-11-05T19:30:00+00:00",
    "epoch": "2989843",
    "key_id": "01M6PJEG8QEP6TGQYTR020MNKQ",
    "sha256": "275501dd9f11e5921e3b70f603778fa683cc52c115089d9c1e327483b8cff140",
    "size": 2364,
    "shapePath": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items3/shape",
    "handle": "h1-8aac07cba4d8e926"
  },
  {
    "offset": "snap.27.0",
    "next_offset": "27",
    "binding": "h1-8ff28099593b1e91\u0000snap.27.0",
    "object": "v1/shapes/h1-8ff28099593b1e91/2177efae30eb8d2e88305f1a52ebadfea1687aa46de82e6547a684a66b949661",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-8ff28099593b1e91/2177efae30eb8d2e88305f1a52ebadfea1687aa46de82e6547a684a66b949661",
    "expires_at": "2026-11-05T19:30:00+00:00",
    "epoch": "2989843",
    "key_id": "01M6PJEG8QEP6TGQYTR020MNKQ",
    "sha256": "4e07ce2f79ff2f1b057aa4cde329e82e3ac6d472d629702490acaaa878f40378",
    "size": 2364,
    "shapePath": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items4/shape",
    "handle": "h1-8ff28099593b1e91"
  },
  {
    "offset": "snap.27.0",
    "next_offset": "27",
    "binding": "h1-d36455ad6764d242\u0000snap.27.0",
    "object": "v1/shapes/h1-d36455ad6764d242/4558bf2b3d944b81a5ea094b57655eb4cda130ca9e93295fca3d3cb966650af9",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-d36455ad6764d242/4558bf2b3d944b81a5ea094b57655eb4cda130ca9e93295fca3d3cb966650af9",
    "expires_at": "2026-11-05T19:30:00+00:00",
    "epoch": "2989843",
    "key_id": "01M6PJEG8QEP6TGQYTR020MNKQ",
    "sha256": "2e58859466acf3115f4b7b36307f9538692c0cb8e0f01a6c5b3c2408053b70fe",
    "size": 2364,
    "shapePath": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items5/shape",
    "handle": "h1-d36455ad6764d242"
  },
  {
    "offset": "snap.27.0",
    "next_offset": "27",
    "binding": "h1-b7bfee3204a5c65e\u0000snap.27.0",
    "object": "v1/shapes/h1-b7bfee3204a5c65e/9476be6dafe6aa0748e9b14eb5eebbb504f1c3d59e4564ac83dd22511e69ab72",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-b7bfee3204a5c65e/9476be6dafe6aa0748e9b14eb5eebbb504f1c3d59e4564ac83dd22511e69ab72",
    "expires_at": "2026-11-05T19:30:00+00:00",
    "epoch": "2989843",
    "key_id": "01M6PJEG8QEP6TGQYTR020MNKQ",
    "sha256": "403b2fa3d06375837c759562f84d7a70fda15a541b3c87e59d77c3a7ce4ed45d",
    "size": 2364,
    "shapePath": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PJEF4TESN35BSJW26Z23P8/tables/items6/shape",
    "handle": "h1-b7bfee3204a5c65e"
  }
]
```
