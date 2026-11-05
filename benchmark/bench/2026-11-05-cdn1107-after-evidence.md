# CDN deletion fix: sanitized live evidence

Public response metadata and synthetic IDs only. Authorization and epoch-key bodies were never retained. `secondsAfterDelete` uses request start; `completedSecondsAfterDelete` uses completed response time. Both are relative to DELETE response completion.

## Requests

```json
[
  {
    "kind": "api",
    "at": "2026-11-05T19:37:39.150131+00:00",
    "completedAt": "2026-11-05T19:37:39.706959+00:00",
    "region": "gru",
    "method": "POST",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases",
    "status": 201,
    "seconds": 0.557,
    "headers": {
      "location": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/cdn1107-final",
      "x-request-id": "01M6PKF163GM5G2JGBH69X68FZ",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:37:39.723149+00:00",
    "completedAt": "2026-11-05T19:37:40.057227+00:00",
    "region": "gru",
    "method": "POST",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/sql/execute",
    "status": 200,
    "seconds": 0.334,
    "headers": {
      "x-request-id": "01M6PKF1P2GF4F0Z64M12JC0WP",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:37:40.073463+00:00",
    "completedAt": "2026-11-05T19:37:40.265697+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items0/shape?offset=-1",
    "status": 307,
    "seconds": 0.192,
    "headers": {
      "location": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items0/shape?handle=h1-20598a059ba4c8ec&offset=snap.27.0",
      "tb-shape-handle": "h1-20598a059ba4c8ec",
      "x-request-id": "01M6PKF22YSE2MERJBFZCVKQWM",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:37:40.281359+00:00",
    "completedAt": "2026-11-05T19:37:40.422433+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items0/shape?handle=h1-20598a059ba4c8ec&offset=snap.27.0",
    "status": 200,
    "seconds": 0.141,
    "headers": {
      "tb-shape-handle": "h1-20598a059ba4c8ec",
      "x-request-id": "01M6PKF29DRS8SMJ1YF786AYZT",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:37:40.438465+00:00",
    "completedAt": "2026-11-05T19:37:40.599045+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items1/shape?offset=-1",
    "status": 307,
    "seconds": 0.161,
    "headers": {
      "location": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items1/shape?handle=h1-ae12fdd14a813c5d&offset=snap.27.0",
      "tb-shape-handle": "h1-ae12fdd14a813c5d",
      "x-request-id": "01M6PKF2EAW0A2NP41XKBV293B",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:37:40.614695+00:00",
    "completedAt": "2026-11-05T19:37:40.748944+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items1/shape?handle=h1-ae12fdd14a813c5d&offset=snap.27.0",
    "status": 200,
    "seconds": 0.134,
    "headers": {
      "tb-shape-handle": "h1-ae12fdd14a813c5d",
      "x-request-id": "01M6PKF2KSKVF6XCTD8346YRTH",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:37:40.765092+00:00",
    "completedAt": "2026-11-05T19:37:40.947144+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items2/shape?offset=-1",
    "status": 307,
    "seconds": 0.182,
    "headers": {
      "location": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items2/shape?handle=h1-268f60bfc571f8c1&offset=snap.27.0",
      "tb-shape-handle": "h1-268f60bfc571f8c1",
      "x-request-id": "01M6PKF2RHKC63ACH4Q6JN93HJ",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:37:40.962872+00:00",
    "completedAt": "2026-11-05T19:37:41.099127+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items2/shape?handle=h1-268f60bfc571f8c1&offset=snap.27.0",
    "status": 200,
    "seconds": 0.136,
    "headers": {
      "tb-shape-handle": "h1-268f60bfc571f8c1",
      "x-request-id": "01M6PKF2YSCWP4E1RMKJ1Z1V76",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:37:41.116074+00:00",
    "completedAt": "2026-11-05T19:37:41.276080+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items3/shape?offset=-1",
    "status": 307,
    "seconds": 0.16,
    "headers": {
      "location": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items3/shape?handle=h1-b34afabf6459dd76&offset=snap.27.0",
      "tb-shape-handle": "h1-b34afabf6459dd76",
      "x-request-id": "01M6PKF33H674QNS2H7VW8NPH7",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:37:41.291889+00:00",
    "completedAt": "2026-11-05T19:37:41.424233+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items3/shape?handle=h1-b34afabf6459dd76&offset=snap.27.0",
    "status": 200,
    "seconds": 0.132,
    "headers": {
      "tb-shape-handle": "h1-b34afabf6459dd76",
      "x-request-id": "01M6PKF39071YW0TTKQTNNPNZQ",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:37:41.440832+00:00",
    "completedAt": "2026-11-05T19:37:41.607812+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items4/shape?offset=-1",
    "status": 307,
    "seconds": 0.167,
    "headers": {
      "location": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items4/shape?handle=h1-4a0b09657d578ff8&offset=snap.27.0",
      "tb-shape-handle": "h1-4a0b09657d578ff8",
      "x-request-id": "01M6PKF3DPVVBY40HBSQRNVFS0",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:37:41.623930+00:00",
    "completedAt": "2026-11-05T19:37:41.768437+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items4/shape?handle=h1-4a0b09657d578ff8&offset=snap.27.0",
    "status": 200,
    "seconds": 0.145,
    "headers": {
      "tb-shape-handle": "h1-4a0b09657d578ff8",
      "x-request-id": "01M6PKF3KCES4SWE79YQAAYVRK",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:37:41.785104+00:00",
    "completedAt": "2026-11-05T19:37:41.942889+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items5/shape?offset=-1",
    "status": 307,
    "seconds": 0.158,
    "headers": {
      "location": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items5/shape?handle=h1-0394df191d3867b4&offset=snap.27.0",
      "tb-shape-handle": "h1-0394df191d3867b4",
      "x-request-id": "01M6PKF3RCESWSQX6FWKJ529FM",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:37:41.960035+00:00",
    "completedAt": "2026-11-05T19:37:42.092537+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items5/shape?handle=h1-0394df191d3867b4&offset=snap.27.0",
    "status": 200,
    "seconds": 0.133,
    "headers": {
      "tb-shape-handle": "h1-0394df191d3867b4",
      "x-request-id": "01M6PKF3XXA4Z3SDJCH6ZSBZMB",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:37:42.109648+00:00",
    "completedAt": "2026-11-05T19:37:42.272592+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items6/shape?offset=-1",
    "status": 307,
    "seconds": 0.163,
    "headers": {
      "location": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items6/shape?handle=h1-c616890b73791c7f&offset=snap.27.0",
      "tb-shape-handle": "h1-c616890b73791c7f",
      "x-request-id": "01M6PKF42K78M659QQ29J14H8P",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:37:42.288727+00:00",
    "completedAt": "2026-11-05T19:37:42.421478+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items6/shape?handle=h1-c616890b73791c7f&offset=snap.27.0",
    "status": 200,
    "seconds": 0.133,
    "headers": {
      "tb-shape-handle": "h1-c616890b73791c7f",
      "x-request-id": "01M6PKF485R8MZ876HR2WCF8AR",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:37:53.538430+00:00",
    "completedAt": "2026-11-05T19:37:53.689664+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items0/shape/manifest?handle=h1-20598a059ba4c8ec&offset=snap.27.0",
    "status": 200,
    "seconds": 0.151,
    "headers": {
      "x-request-id": "01M6PKFF7SSJE1Z2TKBXC12PBZ",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:37:53.706292+00:00",
    "completedAt": "2026-11-05T19:37:53.840462+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items1/shape/manifest?handle=h1-ae12fdd14a813c5d&offset=snap.27.0",
    "status": 200,
    "seconds": 0.134,
    "headers": {
      "x-request-id": "01M6PKFFCZAXG56C5V7JMNG2H1",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:37:53.857107+00:00",
    "completedAt": "2026-11-05T19:37:53.992440+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items2/shape/manifest?handle=h1-268f60bfc571f8c1&offset=snap.27.0",
    "status": 200,
    "seconds": 0.135,
    "headers": {
      "x-request-id": "01M6PKFFHRGXFT6B2M78GA1XZF",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:37:54.008240+00:00",
    "completedAt": "2026-11-05T19:37:54.144257+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items3/shape/manifest?handle=h1-b34afabf6459dd76&offset=snap.27.0",
    "status": 200,
    "seconds": 0.136,
    "headers": {
      "x-request-id": "01M6PKFFPGEMBCKBKSBNQW7QQA",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:37:54.161172+00:00",
    "completedAt": "2026-11-05T19:37:54.294979+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items4/shape/manifest?handle=h1-4a0b09657d578ff8&offset=snap.27.0",
    "status": 200,
    "seconds": 0.134,
    "headers": {
      "x-request-id": "01M6PKFFV7PVBKR039M5NAMK26",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:37:54.311905+00:00",
    "completedAt": "2026-11-05T19:37:54.443945+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items5/shape/manifest?handle=h1-0394df191d3867b4&offset=snap.27.0",
    "status": 200,
    "seconds": 0.132,
    "headers": {
      "x-request-id": "01M6PKFFZXPW84PM2E48R71WMR",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:37:54.460029+00:00",
    "completedAt": "2026-11-05T19:37:54.592319+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items6/shape/manifest?handle=h1-c616890b73791c7f&offset=snap.27.0",
    "status": 200,
    "seconds": 0.132,
    "headers": {
      "x-request-id": "01M6PKFG4F32141T87Q4RVRDAA",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "at": "2026-11-05T19:38:19.921474+00:00",
    "completedAt": "2026-11-05T19:38:20.377460+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-20598a059ba4c8ec/b6e5245bd0ccc4364fa745e0947cbc87c66f8a37847030f253fb239f11ae5a38",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "f463feabc99e1cbf18097d1499e75abd3724a7ad8328a715ad5fe156a40b3cf1",
    "headers": {
      "cf-cache-status": "MISS",
      "cf-ray": "a55e5172df4ae967-GRU",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "gru"
  },
  {
    "at": "2026-11-05T19:38:20.377517+00:00",
    "completedAt": "2026-11-05T19:38:20.410613+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-20598a059ba4c8ec/b6e5245bd0ccc4364fa745e0947cbc87c66f8a37847030f253fb239f11ae5a38",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "f463feabc99e1cbf18097d1499e75abd3724a7ad8328a715ad5fe156a40b3cf1",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e51757cd089d1-GRU",
      "age": "0",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "gru"
  },
  {
    "at": "2026-11-05T19:38:20.410686+00:00",
    "completedAt": "2026-11-05T19:38:20.738743+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-ae12fdd14a813c5d/7f4caa9c80a6326fe96fc0a91372d215b229d50f23f6bfdd40e5692a1f0cca23",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "ffefe667aaab4c31eed010b7a334ce6226a6ce04480b1f29118675ace1a6b3cf",
    "headers": {
      "cf-cache-status": "MISS",
      "cf-ray": "a55e5175ae2b01af-GRU",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "gru"
  },
  {
    "at": "2026-11-05T19:38:20.738787+00:00",
    "completedAt": "2026-11-05T19:38:20.779473+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-ae12fdd14a813c5d/7f4caa9c80a6326fe96fc0a91372d215b229d50f23f6bfdd40e5692a1f0cca23",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "ffefe667aaab4c31eed010b7a334ce6226a6ce04480b1f29118675ace1a6b3cf",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e5177b8e76cb9-GRU",
      "age": "0",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "gru"
  },
  {
    "at": "2026-11-05T19:38:20.779517+00:00",
    "completedAt": "2026-11-05T19:38:21.057877+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-268f60bfc571f8c1/d7249b615380e29eb05b9c72478e160e4242bef1fa5abdd485db293ed124f1ba",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "aad49a36e9fb68f3c78aac0a48112ecfc19081cfa0032c1edcb0dd77b3248555",
    "headers": {
      "cf-cache-status": "MISS",
      "cf-ray": "a55e51780fb58fcb-GRU",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "gru"
  },
  {
    "at": "2026-11-05T19:38:21.057964+00:00",
    "completedAt": "2026-11-05T19:38:21.094564+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-268f60bfc571f8c1/d7249b615380e29eb05b9c72478e160e4242bef1fa5abdd485db293ed124f1ba",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "aad49a36e9fb68f3c78aac0a48112ecfc19081cfa0032c1edcb0dd77b3248555",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e5179bd565402-GRU",
      "age": "0",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "gru"
  },
  {
    "at": "2026-11-05T19:38:21.094606+00:00",
    "completedAt": "2026-11-05T19:38:21.352961+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-b34afabf6459dd76/d51aa4a77fc362dfd23b8391b8d5fdd8fa2ddd0fa50a1a4360f7f754bde72164",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "8b410f6c3a82afac9098c14fc2369ab00d4ea1efd759a07ef7221450fd2d1e24",
    "headers": {
      "cf-cache-status": "MISS",
      "cf-ray": "a55e5179e9be0123-GRU",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "gru"
  },
  {
    "at": "2026-11-05T19:38:21.353006+00:00",
    "completedAt": "2026-11-05T19:38:21.392919+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-b34afabf6459dd76/d51aa4a77fc362dfd23b8391b8d5fdd8fa2ddd0fa50a1a4360f7f754bde72164",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "8b410f6c3a82afac9098c14fc2369ab00d4ea1efd759a07ef7221450fd2d1e24",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e517b8cb191dd-GRU",
      "age": "0",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "gru"
  },
  {
    "at": "2026-11-05T19:38:21.392966+00:00",
    "completedAt": "2026-11-05T19:38:21.719466+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-4a0b09657d578ff8/a572af5ecdee840d36cb5116da33a801278f2561a92b5002d65bbb99ab31bce8",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "9d0d022d072f57ae71ced9323989b4bbe5d5768e1de5d4b8d6f969949f235b2e",
    "headers": {
      "cf-cache-status": "MISS",
      "cf-ray": "a55e517bca7d156d-GRU",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "gru"
  },
  {
    "at": "2026-11-05T19:38:21.719519+00:00",
    "completedAt": "2026-11-05T19:38:21.755278+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-4a0b09657d578ff8/a572af5ecdee840d36cb5116da33a801278f2561a92b5002d65bbb99ab31bce8",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "9d0d022d072f57ae71ced9323989b4bbe5d5768e1de5d4b8d6f969949f235b2e",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e517dc8caf1f9-GRU",
      "age": "0",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "gru"
  },
  {
    "at": "2026-11-05T19:38:21.755328+00:00",
    "completedAt": "2026-11-05T19:38:22.043080+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-0394df191d3867b4/6b073e16b70c749e0ab0bf2d75a1ca208d0b11adfb0919bacb8c0a456c093738",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "cdbaec1eaf7b8bdcf04c3a0605bc5889692b3157ad03322a4a2d8315199c6f3e",
    "headers": {
      "cf-cache-status": "MISS",
      "cf-ray": "a55e517e09484979-GRU",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "gru"
  },
  {
    "at": "2026-11-05T19:38:22.043121+00:00",
    "completedAt": "2026-11-05T19:38:22.081911+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-0394df191d3867b4/6b073e16b70c749e0ab0bf2d75a1ca208d0b11adfb0919bacb8c0a456c093738",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "cdbaec1eaf7b8bdcf04c3a0605bc5889692b3157ad03322a4a2d8315199c6f3e",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e517fedf99423-GRU",
      "age": "0",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "gru"
  },
  {
    "at": "2026-11-05T19:38:22.081956+00:00",
    "completedAt": "2026-11-05T19:38:22.388212+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-c616890b73791c7f/d2959ab702b5b114313f960a4b1d8b62bdfab45dead3939925ecaf96ea1bd3b7",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "d2b28aad5bb259195d21fa779d109b42fb7e2fa4509b58651ed4c00df8c94023",
    "headers": {
      "cf-cache-status": "MISS",
      "cf-ray": "a55e518018c1ae01-GRU",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "gru"
  },
  {
    "at": "2026-11-05T19:38:22.388266+00:00",
    "completedAt": "2026-11-05T19:38:22.423247+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-c616890b73791c7f/d2959ab702b5b114313f960a4b1d8b62bdfab45dead3939925ecaf96ea1bd3b7",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "d2b28aad5bb259195d21fa779d109b42fb7e2fa4509b58651ed4c00df8c94023",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e51820dfd864b-GRU",
      "age": "0",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "gru"
  },
  {
    "at": "2026-11-05T19:38:21.508825+00:00",
    "completedAt": "2026-11-05T19:38:21.687506+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-20598a059ba4c8ec/b6e5245bd0ccc4364fa745e0947cbc87c66f8a37847030f253fb239f11ae5a38",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "f463feabc99e1cbf18097d1499e75abd3724a7ad8328a715ad5fe156a40b3cf1",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e517cca472599-SJC",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "sjc"
  },
  {
    "at": "2026-11-05T19:38:21.687556+00:00",
    "completedAt": "2026-11-05T19:38:21.745651+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-20598a059ba4c8ec/b6e5245bd0ccc4364fa745e0947cbc87c66f8a37847030f253fb239f11ae5a38",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "f463feabc99e1cbf18097d1499e75abd3724a7ad8328a715ad5fe156a40b3cf1",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e517dbb76ce7c-SJC",
      "age": "1",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "sjc"
  },
  {
    "at": "2026-11-05T19:38:21.745696+00:00",
    "completedAt": "2026-11-05T19:38:21.888430+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-ae12fdd14a813c5d/7f4caa9c80a6326fe96fc0a91372d215b229d50f23f6bfdd40e5692a1f0cca23",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "ffefe667aaab4c31eed010b7a334ce6226a6ce04480b1f29118675ace1a6b3cf",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e517e1d447ab8-SJC",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "sjc"
  },
  {
    "at": "2026-11-05T19:38:21.888474+00:00",
    "completedAt": "2026-11-05T19:38:21.985501+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-ae12fdd14a813c5d/7f4caa9c80a6326fe96fc0a91372d215b229d50f23f6bfdd40e5692a1f0cca23",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "ffefe667aaab4c31eed010b7a334ce6226a6ce04480b1f29118675ace1a6b3cf",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e517eeadf7a20-SJC",
      "age": "1",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "sjc"
  },
  {
    "at": "2026-11-05T19:38:21.985547+00:00",
    "completedAt": "2026-11-05T19:38:22.118592+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-268f60bfc571f8c1/d7249b615380e29eb05b9c72478e160e4242bef1fa5abdd485db293ed124f1ba",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "aad49a36e9fb68f3c78aac0a48112ecfc19081cfa0032c1edcb0dd77b3248555",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e517f894dc6c6-SJC",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "sjc"
  },
  {
    "at": "2026-11-05T19:38:22.118636+00:00",
    "completedAt": "2026-11-05T19:38:22.183646+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-268f60bfc571f8c1/d7249b615380e29eb05b9c72478e160e4242bef1fa5abdd485db293ed124f1ba",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "aad49a36e9fb68f3c78aac0a48112ecfc19081cfa0032c1edcb0dd77b3248555",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e51806f70cefd-SJC",
      "age": "1",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "sjc"
  },
  {
    "at": "2026-11-05T19:38:22.183705+00:00",
    "completedAt": "2026-11-05T19:38:22.305123+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-b34afabf6459dd76/d51aa4a77fc362dfd23b8391b8d5fdd8fa2ddd0fa50a1a4360f7f754bde72164",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "8b410f6c3a82afac9098c14fc2369ab00d4ea1efd759a07ef7221450fd2d1e24",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e5180bd32327e-SJC",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "sjc"
  },
  {
    "at": "2026-11-05T19:38:22.305168+00:00",
    "completedAt": "2026-11-05T19:38:22.370821+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-b34afabf6459dd76/d51aa4a77fc362dfd23b8391b8d5fdd8fa2ddd0fa50a1a4360f7f754bde72164",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "8b410f6c3a82afac9098c14fc2369ab00d4ea1efd759a07ef7221450fd2d1e24",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e51818fe98e9b-SJC",
      "age": "1",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "sjc"
  },
  {
    "at": "2026-11-05T19:38:22.370869+00:00",
    "completedAt": "2026-11-05T19:38:22.501477+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-4a0b09657d578ff8/a572af5ecdee840d36cb5116da33a801278f2561a92b5002d65bbb99ab31bce8",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "9d0d022d072f57ae71ced9323989b4bbe5d5768e1de5d4b8d6f969949f235b2e",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e5181ee0d15e0-SJC",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "sjc"
  },
  {
    "at": "2026-11-05T19:38:22.501519+00:00",
    "completedAt": "2026-11-05T19:38:22.541804+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-4a0b09657d578ff8/a572af5ecdee840d36cb5116da33a801278f2561a92b5002d65bbb99ab31bce8",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "9d0d022d072f57ae71ced9323989b4bbe5d5768e1de5d4b8d6f969949f235b2e",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e5182beeaeb29-SJC",
      "age": "0",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "sjc"
  },
  {
    "at": "2026-11-05T19:38:22.541862+00:00",
    "completedAt": "2026-11-05T19:38:22.712754+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-0394df191d3867b4/6b073e16b70c749e0ab0bf2d75a1ca208d0b11adfb0919bacb8c0a456c093738",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "cdbaec1eaf7b8bdcf04c3a0605bc5889692b3157ad03322a4a2d8315199c6f3e",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e51830e79c89f-SJC",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "sjc"
  },
  {
    "at": "2026-11-05T19:38:22.712796+00:00",
    "completedAt": "2026-11-05T19:38:22.756568+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-0394df191d3867b4/6b073e16b70c749e0ab0bf2d75a1ca208d0b11adfb0919bacb8c0a456c093738",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "cdbaec1eaf7b8bdcf04c3a0605bc5889692b3157ad03322a4a2d8315199c6f3e",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e51841f390dfd-SJC",
      "age": "0",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "sjc"
  },
  {
    "at": "2026-11-05T19:38:22.756615+00:00",
    "completedAt": "2026-11-05T19:38:22.942745+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-c616890b73791c7f/d2959ab702b5b114313f960a4b1d8b62bdfab45dead3939925ecaf96ea1bd3b7",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "d2b28aad5bb259195d21fa779d109b42fb7e2fa4509b58651ed4c00df8c94023",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e51845b2a5c18-SJC",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "sjc"
  },
  {
    "at": "2026-11-05T19:38:22.942786+00:00",
    "completedAt": "2026-11-05T19:38:23.026895+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-c616890b73791c7f/d2959ab702b5b114313f960a4b1d8b62bdfab45dead3939925ecaf96ea1bd3b7",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "d2b28aad5bb259195d21fa779d109b42fb7e2fa4509b58651ed4c00df8c94023",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e51858d7d2714-SJC",
      "age": "0",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "sjc"
  },
  {
    "at": "2026-11-05T19:38:21.690959+00:00",
    "completedAt": "2026-11-05T19:38:21.895599+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-20598a059ba4c8ec/b6e5245bd0ccc4364fa745e0947cbc87c66f8a37847030f253fb239f11ae5a38",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "f463feabc99e1cbf18097d1499e75abd3724a7ad8328a715ad5fe156a40b3cf1",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e517dff1dd288-FRA",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "fra"
  },
  {
    "at": "2026-11-05T19:38:21.895678+00:00",
    "completedAt": "2026-11-05T19:38:21.939655+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-20598a059ba4c8ec/b6e5245bd0ccc4364fa745e0947cbc87c66f8a37847030f253fb239f11ae5a38",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "f463feabc99e1cbf18097d1499e75abd3724a7ad8328a715ad5fe156a40b3cf1",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e517efc2ea6a2-FRA",
      "age": "1",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "fra"
  },
  {
    "at": "2026-11-05T19:38:21.939698+00:00",
    "completedAt": "2026-11-05T19:38:22.075032+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-ae12fdd14a813c5d/7f4caa9c80a6326fe96fc0a91372d215b229d50f23f6bfdd40e5692a1f0cca23",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "ffefe667aaab4c31eed010b7a334ce6226a6ce04480b1f29118675ace1a6b3cf",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e517f384f7084-FRA",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "fra"
  },
  {
    "at": "2026-11-05T19:38:22.075071+00:00",
    "completedAt": "2026-11-05T19:38:22.117729+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-ae12fdd14a813c5d/7f4caa9c80a6326fe96fc0a91372d215b229d50f23f6bfdd40e5692a1f0cca23",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "ffefe667aaab4c31eed010b7a334ce6226a6ce04480b1f29118675ace1a6b3cf",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e518009ffd3a9-FRA",
      "age": "1",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "fra"
  },
  {
    "at": "2026-11-05T19:38:22.117772+00:00",
    "completedAt": "2026-11-05T19:38:22.281795+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-268f60bfc571f8c1/d7249b615380e29eb05b9c72478e160e4242bef1fa5abdd485db293ed124f1ba",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "aad49a36e9fb68f3c78aac0a48112ecfc19081cfa0032c1edcb0dd77b3248555",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e5180589bdc58-FRA",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "fra"
  },
  {
    "at": "2026-11-05T19:38:22.281837+00:00",
    "completedAt": "2026-11-05T19:38:22.330979+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-268f60bfc571f8c1/d7249b615380e29eb05b9c72478e160e4242bef1fa5abdd485db293ed124f1ba",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "aad49a36e9fb68f3c78aac0a48112ecfc19081cfa0032c1edcb0dd77b3248555",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e518158b9b10b-FRA",
      "age": "1",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "fra"
  },
  {
    "at": "2026-11-05T19:38:22.331021+00:00",
    "completedAt": "2026-11-05T19:38:22.506379+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-b34afabf6459dd76/d51aa4a77fc362dfd23b8391b8d5fdd8fa2ddd0fa50a1a4360f7f754bde72164",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "8b410f6c3a82afac9098c14fc2369ab00d4ea1efd759a07ef7221450fd2d1e24",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e5181a861e7ae-FRA",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "fra"
  },
  {
    "at": "2026-11-05T19:38:22.506419+00:00",
    "completedAt": "2026-11-05T19:38:22.562428+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-b34afabf6459dd76/d51aa4a77fc362dfd23b8391b8d5fdd8fa2ddd0fa50a1a4360f7f754bde72164",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "8b410f6c3a82afac9098c14fc2369ab00d4ea1efd759a07ef7221450fd2d1e24",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e5182cde6d291-FRA",
      "age": "1",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "fra"
  },
  {
    "at": "2026-11-05T19:38:22.562470+00:00",
    "completedAt": "2026-11-05T19:38:22.715461+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-4a0b09657d578ff8/a572af5ecdee840d36cb5116da33a801278f2561a92b5002d65bbb99ab31bce8",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "9d0d022d072f57ae71ced9323989b4bbe5d5768e1de5d4b8d6f969949f235b2e",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e51831e27d295-FRA",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "fra"
  },
  {
    "at": "2026-11-05T19:38:22.715498+00:00",
    "completedAt": "2026-11-05T19:38:22.772884+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-4a0b09657d578ff8/a572af5ecdee840d36cb5116da33a801278f2561a92b5002d65bbb99ab31bce8",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "9d0d022d072f57ae71ced9323989b4bbe5d5768e1de5d4b8d6f969949f235b2e",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e518409e037e8-FRA",
      "age": "1",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "fra"
  },
  {
    "at": "2026-11-05T19:38:22.772926+00:00",
    "completedAt": "2026-11-05T19:38:22.926584+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-0394df191d3867b4/6b073e16b70c749e0ab0bf2d75a1ca208d0b11adfb0919bacb8c0a456c093738",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "cdbaec1eaf7b8bdcf04c3a0605bc5889692b3157ad03322a4a2d8315199c6f3e",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e51846d73d38a-FRA",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "fra"
  },
  {
    "at": "2026-11-05T19:38:22.926657+00:00",
    "completedAt": "2026-11-05T19:38:22.988793+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-0394df191d3867b4/6b073e16b70c749e0ab0bf2d75a1ca208d0b11adfb0919bacb8c0a456c093738",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "cdbaec1eaf7b8bdcf04c3a0605bc5889692b3157ad03322a4a2d8315199c6f3e",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e518558e3ddd3-FRA",
      "age": "1",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "fra"
  },
  {
    "at": "2026-11-05T19:38:22.988834+00:00",
    "completedAt": "2026-11-05T19:38:23.145466+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-c616890b73791c7f/d2959ab702b5b114313f960a4b1d8b62bdfab45dead3939925ecaf96ea1bd3b7",
    "repeat": 0,
    "status": 200,
    "bytes": 2364,
    "sha256": "d2b28aad5bb259195d21fa779d109b42fb7e2fa4509b58651ed4c00df8c94023",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e5185ca52dc99-FRA",
      "age": null,
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "fra"
  },
  {
    "at": "2026-11-05T19:38:23.145507+00:00",
    "completedAt": "2026-11-05T19:38:23.186813+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-c616890b73791c7f/d2959ab702b5b114313f960a4b1d8b62bdfab45dead3939925ecaf96ea1bd3b7",
    "repeat": 1,
    "status": 200,
    "bytes": 2364,
    "sha256": "d2b28aad5bb259195d21fa779d109b42fb7e2fa4509b58651ed4c00df8c94023",
    "headers": {
      "cf-cache-status": "HIT",
      "cf-ray": "a55e5186bed5d286-FRA",
      "age": "0",
      "cache-control": "public, max-age=31536000, immutable"
    },
    "kind": "cdn-warm",
    "region": "fra"
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:38:33.556217+00:00",
    "completedAt": "2026-11-05T19:38:33.702545+00:00",
    "region": "gru",
    "method": "POST",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/sql/execute",
    "status": 200,
    "seconds": 0.146,
    "headers": {
      "x-request-id": "01M6PKGPA9ARHXCW8Z1GGZBZXV",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:38:33.718928+00:00",
    "completedAt": "2026-11-05T19:38:34.720011+00:00",
    "region": "sjc",
    "method": "POST",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/sql/execute",
    "status": 200,
    "seconds": 1.001,
    "headers": {
      "x-request-id": "01M6PKGQ254KZ60T81GXZE69XZ",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:38:34.736943+00:00",
    "completedAt": "2026-11-05T19:38:35.816492+00:00",
    "region": "fra",
    "method": "POST",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/sql/execute",
    "status": 200,
    "seconds": 1.08,
    "headers": {
      "x-request-id": "01M6PKGR3QY9CPPV0905SJTPRD",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:38:35.842670+00:00",
    "completedAt": "2026-11-05T19:38:37.843954+00:00",
    "region": "gru",
    "method": "POST",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/move-home",
    "status": 200,
    "seconds": 2.001,
    "headers": {
      "x-request-id": "01M6PKGRHPY2AHDEBCBV6K36MW",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:38:48.454043+00:00",
    "completedAt": "2026-11-05T19:38:58.716500+00:00",
    "region": "fra",
    "method": "DELETE",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2",
    "status": 204,
    "seconds": 10.262,
    "headers": {
      "x-request-id": "01M6PKH5GESVNBZPSA2XEX8E12",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:38:58.750959+00:00",
    "completedAt": "2026-11-05T19:38:59.086318+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2",
    "status": 404,
    "seconds": 0.335,
    "headers": {
      "x-request-id": "01M6PKHEXKGDFXPACD9JJ438AR",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    },
    "error": "unknown_database"
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:38:59.102463+00:00",
    "completedAt": "2026-11-05T19:38:59.425988+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items0/shape/keys?handle=h1-20598a059ba4c8ec&epoch=2989845&key_id=01M6PKF1T357ENMXHF1EPR8YMW",
    "status": 404,
    "seconds": 0.324,
    "headers": {
      "x-request-id": "01M6PKHF8JQPSEFR2HFAX3HSHD",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:38:59.443078+00:00",
    "completedAt": "2026-11-05T19:38:59.768073+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items1/shape/keys?handle=h1-ae12fdd14a813c5d&epoch=2989845&key_id=01M6PKF1T357ENMXHF1EPR8YMW",
    "status": 404,
    "seconds": 0.325,
    "headers": {
      "x-request-id": "01M6PKHFK9SDQRZT8B15X84365",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:38:59.784598+00:00",
    "completedAt": "2026-11-05T19:39:00.108456+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items2/shape/keys?handle=h1-268f60bfc571f8c1&epoch=2989845&key_id=01M6PKF1T357ENMXHF1EPR8YMW",
    "status": 404,
    "seconds": 0.324,
    "headers": {
      "x-request-id": "01M6PKHFXX40WKASTFE0K80NMG",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:39:00.125017+00:00",
    "completedAt": "2026-11-05T19:39:00.447287+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items3/shape/keys?handle=h1-b34afabf6459dd76&epoch=2989845&key_id=01M6PKF1T357ENMXHF1EPR8YMW",
    "status": 404,
    "seconds": 0.322,
    "headers": {
      "x-request-id": "01M6PKHG8HJB672RW5RC6RDJ24",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:39:00.464633+00:00",
    "completedAt": "2026-11-05T19:39:00.792433+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items4/shape/keys?handle=h1-4a0b09657d578ff8&epoch=2989845&key_id=01M6PKF1T357ENMXHF1EPR8YMW",
    "status": 404,
    "seconds": 0.328,
    "headers": {
      "x-request-id": "01M6PKHGK5MZYWQ4JQ560QKK5Z",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:39:00.809376+00:00",
    "completedAt": "2026-11-05T19:39:01.132616+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items5/shape/keys?handle=h1-0394df191d3867b4&epoch=2989845&key_id=01M6PKF1T357ENMXHF1EPR8YMW",
    "status": 404,
    "seconds": 0.323,
    "headers": {
      "x-request-id": "01M6PKHGXW0T4VCZG8E4B5FD5N",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:39:01.149283+00:00",
    "completedAt": "2026-11-05T19:39:01.470160+00:00",
    "region": "gru",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items6/shape/keys?handle=h1-c616890b73791c7f&epoch=2989845&key_id=01M6PKF1T357ENMXHF1EPR8YMW",
    "status": 404,
    "seconds": 0.321,
    "headers": {
      "x-request-id": "01M6PKHH8G903WV4QF891FG3HA",
      "x-terrabase-machine": "ts-gru-1",
      "x-terrabase-region": "gru"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:39:01.486107+00:00",
    "completedAt": "2026-11-05T19:39:02.645897+00:00",
    "region": "sjc",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2",
    "status": 404,
    "seconds": 1.16,
    "headers": {
      "x-request-id": "01M6PKHJ5VE6BB73JASE9ZWB5Z",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    },
    "error": "unknown_database"
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:39:02.662136+00:00",
    "completedAt": "2026-11-05T19:39:03.824030+00:00",
    "region": "sjc",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items0/shape/keys?handle=h1-20598a059ba4c8ec&epoch=2989845&key_id=01M6PKF1T357ENMXHF1EPR8YMW",
    "status": 404,
    "seconds": 1.162,
    "headers": {
      "x-request-id": "01M6PKHKAKN8CNYSKR0NPRDHJE",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:39:03.839984+00:00",
    "completedAt": "2026-11-05T19:39:04.995696+00:00",
    "region": "sjc",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items1/shape/keys?handle=h1-ae12fdd14a813c5d&epoch=2989845&key_id=01M6PKF1T357ENMXHF1EPR8YMW",
    "status": 404,
    "seconds": 1.156,
    "headers": {
      "x-request-id": "01M6PKHMFC484XJAV05TWMZG3C",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:39:05.012619+00:00",
    "completedAt": "2026-11-05T19:39:06.166927+00:00",
    "region": "sjc",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items2/shape/keys?handle=h1-268f60bfc571f8c1&epoch=2989845&key_id=01M6PKF1T357ENMXHF1EPR8YMW",
    "status": 404,
    "seconds": 1.154,
    "headers": {
      "x-request-id": "01M6PKHNM1D9E8589AGQ96858B",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:39:06.183410+00:00",
    "completedAt": "2026-11-05T19:39:07.340600+00:00",
    "region": "sjc",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items3/shape/keys?handle=h1-b34afabf6459dd76&epoch=2989845&key_id=01M6PKF1T357ENMXHF1EPR8YMW",
    "status": 404,
    "seconds": 1.157,
    "headers": {
      "x-request-id": "01M6PKHPRNBQ1MWMVJ3Z069K89",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:39:07.357765+00:00",
    "completedAt": "2026-11-05T19:39:08.514852+00:00",
    "region": "sjc",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items4/shape/keys?handle=h1-4a0b09657d578ff8&epoch=2989845&key_id=01M6PKF1T357ENMXHF1EPR8YMW",
    "status": 404,
    "seconds": 1.157,
    "headers": {
      "x-request-id": "01M6PKHQXBDR5AARW8SJ6CMSV4",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:39:08.531121+00:00",
    "completedAt": "2026-11-05T19:39:09.688484+00:00",
    "region": "sjc",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items5/shape/keys?handle=h1-0394df191d3867b4&epoch=2989845&key_id=01M6PKF1T357ENMXHF1EPR8YMW",
    "status": 404,
    "seconds": 1.157,
    "headers": {
      "x-request-id": "01M6PKHS20Q6VVKPQGHHYJ666M",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:39:09.704754+00:00",
    "completedAt": "2026-11-05T19:39:10.860241+00:00",
    "region": "sjc",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items6/shape/keys?handle=h1-c616890b73791c7f&epoch=2989845&key_id=01M6PKF1T357ENMXHF1EPR8YMW",
    "status": 404,
    "seconds": 1.156,
    "headers": {
      "x-request-id": "01M6PKHT6NRTDZ2DGXDA7MCMWY",
      "x-terrabase-machine": "ts-sjc-1",
      "x-terrabase-region": "sjc"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:39:10.877203+00:00",
    "completedAt": "2026-11-05T19:39:12.136017+00:00",
    "region": "fra",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2",
    "status": 404,
    "seconds": 1.259,
    "headers": {
      "x-request-id": "01M6PKHVD353RQ0B4HE508EA5X",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    },
    "error": "unknown_database"
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:39:12.152469+00:00",
    "completedAt": "2026-11-05T19:39:13.415150+00:00",
    "region": "fra",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items0/shape/keys?handle=h1-20598a059ba4c8ec&epoch=2989845&key_id=01M6PKF1T357ENMXHF1EPR8YMW",
    "status": 404,
    "seconds": 1.263,
    "headers": {
      "x-request-id": "01M6PKHWMZP046YWF7JH9R9S7P",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:39:13.431509+00:00",
    "completedAt": "2026-11-05T19:39:14.694455+00:00",
    "region": "fra",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items1/shape/keys?handle=h1-ae12fdd14a813c5d&epoch=2989845&key_id=01M6PKF1T357ENMXHF1EPR8YMW",
    "status": 404,
    "seconds": 1.263,
    "headers": {
      "x-request-id": "01M6PKHXWWDA2A9NFF5MKTFYQW",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:39:14.711113+00:00",
    "completedAt": "2026-11-05T19:39:16.158453+00:00",
    "region": "fra",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items2/shape/keys?handle=h1-268f60bfc571f8c1&epoch=2989845&key_id=01M6PKF1T357ENMXHF1EPR8YMW",
    "status": 404,
    "seconds": 1.447,
    "headers": {
      "x-request-id": "01M6PKHZ4WH73CCNHXE0Z2VASZ",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:39:16.175083+00:00",
    "completedAt": "2026-11-05T19:39:17.439652+00:00",
    "region": "fra",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items3/shape/keys?handle=h1-b34afabf6459dd76&epoch=2989845&key_id=01M6PKF1T357ENMXHF1EPR8YMW",
    "status": 404,
    "seconds": 1.265,
    "headers": {
      "x-request-id": "01M6PKJ0JVS8NCJ3XW3K1Y2H2A",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:39:17.455981+00:00",
    "completedAt": "2026-11-05T19:39:18.713559+00:00",
    "region": "fra",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items4/shape/keys?handle=h1-4a0b09657d578ff8&epoch=2989845&key_id=01M6PKF1T357ENMXHF1EPR8YMW",
    "status": 404,
    "seconds": 1.258,
    "headers": {
      "x-request-id": "01M6PKJ1TNATE54Q5QTCGW9VH3",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:39:18.730423+00:00",
    "completedAt": "2026-11-05T19:39:19.991463+00:00",
    "region": "fra",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items5/shape/keys?handle=h1-0394df191d3867b4&epoch=2989845&key_id=01M6PKF1T357ENMXHF1EPR8YMW",
    "status": 404,
    "seconds": 1.261,
    "headers": {
      "x-request-id": "01M6PKJ32GWPZXX2S9YRBN1JBC",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    }
  },
  {
    "kind": "api",
    "at": "2026-11-05T19:39:20.007892+00:00",
    "completedAt": "2026-11-05T19:39:21.264141+00:00",
    "region": "fra",
    "method": "GET",
    "path": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items6/shape/keys?handle=h1-c616890b73791c7f&epoch=2989845&key_id=01M6PKF1T357ENMXHF1EPR8YMW",
    "status": 404,
    "seconds": 1.256,
    "headers": {
      "x-request-id": "01M6PKJ4AD7R0SRCXJA0KDP11F",
      "x-terrabase-machine": "ts-fra-1",
      "x-terrabase-region": "fra"
    }
  },
  {
    "at": "2026-11-05T19:39:38.552752+00:00",
    "completedAt": "2026-11-05T19:39:38.833830+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-20598a059ba4c8ec/b6e5245bd0ccc4364fa745e0947cbc87c66f8a37847030f253fb239f11ae5a38",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e535e3cd26eb1-GRU",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "gru",
    "secondsAfterDelete": 39.836252,
    "completedSecondsAfterDelete": 40.11733
  },
  {
    "at": "2026-11-05T19:39:38.833944+00:00",
    "completedAt": "2026-11-05T19:39:39.064730+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-ae12fdd14a813c5d/7f4caa9c80a6326fe96fc0a91372d215b229d50f23f6bfdd40e5692a1f0cca23",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e535fcf9d5415-GRU",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "gru",
    "secondsAfterDelete": 40.117444,
    "completedSecondsAfterDelete": 40.34823
  },
  {
    "at": "2026-11-05T19:39:39.064793+00:00",
    "completedAt": "2026-11-05T19:39:39.330550+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-268f60bfc571f8c1/d7249b615380e29eb05b9c72478e160e4242bef1fa5abdd485db293ed124f1ba",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e53613b65a523-GRU",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "gru",
    "secondsAfterDelete": 40.348293,
    "completedSecondsAfterDelete": 40.61405
  },
  {
    "at": "2026-11-05T19:39:39.330611+00:00",
    "completedAt": "2026-11-05T19:39:39.543075+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-b34afabf6459dd76/d51aa4a77fc362dfd23b8391b8d5fdd8fa2ddd0fa50a1a4360f7f754bde72164",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e5362e91bd8cb-GRU",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "gru",
    "secondsAfterDelete": 40.614111,
    "completedSecondsAfterDelete": 40.826575
  },
  {
    "at": "2026-11-05T19:39:39.543137+00:00",
    "completedAt": "2026-11-05T19:39:39.800349+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-4a0b09657d578ff8/a572af5ecdee840d36cb5116da33a801278f2561a92b5002d65bbb99ab31bce8",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e53643cdcf231-GRU",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "gru",
    "secondsAfterDelete": 40.826637,
    "completedSecondsAfterDelete": 41.083849
  },
  {
    "at": "2026-11-05T19:39:39.800410+00:00",
    "completedAt": "2026-11-05T19:39:40.049422+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-0394df191d3867b4/6b073e16b70c749e0ab0bf2d75a1ca208d0b11adfb0919bacb8c0a456c093738",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e5365d9ed2ffa-GRU",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "gru",
    "secondsAfterDelete": 41.08391,
    "completedSecondsAfterDelete": 41.332922
  },
  {
    "at": "2026-11-05T19:39:40.049495+00:00",
    "completedAt": "2026-11-05T19:39:40.291007+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-c616890b73791c7f/d2959ab702b5b114313f960a4b1d8b62bdfab45dead3939925ecaf96ea1bd3b7",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e53676f491d13-GRU",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "gru",
    "secondsAfterDelete": 41.332995,
    "completedSecondsAfterDelete": 41.574507
  },
  {
    "at": "2026-11-05T19:39:40.162782+00:00",
    "completedAt": "2026-11-05T19:39:40.404903+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-20598a059ba4c8ec/b6e5245bd0ccc4364fa745e0947cbc87c66f8a37847030f253fb239f11ae5a38",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e536849d62a0c-SJC",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "sjc",
    "secondsAfterDelete": 41.446282,
    "completedSecondsAfterDelete": 41.688403
  },
  {
    "at": "2026-11-05T19:39:40.405041+00:00",
    "completedAt": "2026-11-05T19:39:40.584129+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-ae12fdd14a813c5d/7f4caa9c80a6326fe96fc0a91372d215b229d50f23f6bfdd40e5692a1f0cca23",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e5369ae8dce84-SJC",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "sjc",
    "secondsAfterDelete": 41.688541,
    "completedSecondsAfterDelete": 41.867629
  },
  {
    "at": "2026-11-05T19:39:40.584200+00:00",
    "completedAt": "2026-11-05T19:39:40.758582+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-268f60bfc571f8c1/d7249b615380e29eb05b9c72478e160e4242bef1fa5abdd485db293ed124f1ba",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e536abe2e0c97-SJC",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "sjc",
    "secondsAfterDelete": 41.8677,
    "completedSecondsAfterDelete": 42.042082
  },
  {
    "at": "2026-11-05T19:39:40.758649+00:00",
    "completedAt": "2026-11-05T19:39:40.969646+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-b34afabf6459dd76/d51aa4a77fc362dfd23b8391b8d5fdd8fa2ddd0fa50a1a4360f7f754bde72164",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e536bd879ed41-SJC",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "sjc",
    "secondsAfterDelete": 42.042149,
    "completedSecondsAfterDelete": 42.253146
  },
  {
    "at": "2026-11-05T19:39:40.969710+00:00",
    "completedAt": "2026-11-05T19:39:41.176186+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-4a0b09657d578ff8/a572af5ecdee840d36cb5116da33a801278f2561a92b5002d65bbb99ab31bce8",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e536d18e46c4d-SJC",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "sjc",
    "secondsAfterDelete": 42.25321,
    "completedSecondsAfterDelete": 42.459686
  },
  {
    "at": "2026-11-05T19:39:41.176254+00:00",
    "completedAt": "2026-11-05T19:39:41.491554+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-0394df191d3867b4/6b073e16b70c749e0ab0bf2d75a1ca208d0b11adfb0919bacb8c0a456c093738",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e536e6923eb26-SJC",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "sjc",
    "secondsAfterDelete": 42.459754,
    "completedSecondsAfterDelete": 42.775054
  },
  {
    "at": "2026-11-05T19:39:41.491620+00:00",
    "completedAt": "2026-11-05T19:39:41.688588+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-c616890b73791c7f/d2959ab702b5b114313f960a4b1d8b62bdfab45dead3939925ecaf96ea1bd3b7",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e53706f128183-SJC",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "sjc",
    "secondsAfterDelete": 42.77512,
    "completedSecondsAfterDelete": 42.972088
  },
  {
    "at": "2026-11-05T19:39:40.315482+00:00",
    "completedAt": "2026-11-05T19:39:40.588757+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-20598a059ba4c8ec/b6e5245bd0ccc4364fa745e0947cbc87c66f8a37847030f253fb239f11ae5a38",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e536958874b58-FRA",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "fra",
    "secondsAfterDelete": 41.598982,
    "completedSecondsAfterDelete": 41.872257
  },
  {
    "at": "2026-11-05T19:39:40.588830+00:00",
    "completedAt": "2026-11-05T19:39:40.781984+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-ae12fdd14a813c5d/7f4caa9c80a6326fe96fc0a91372d215b229d50f23f6bfdd40e5692a1f0cca23",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e536acc78dbfc-FRA",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "fra",
    "secondsAfterDelete": 41.87233,
    "completedSecondsAfterDelete": 42.065484
  },
  {
    "at": "2026-11-05T19:39:40.782047+00:00",
    "completedAt": "2026-11-05T19:39:41.024488+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-268f60bfc571f8c1/d7249b615380e29eb05b9c72478e160e4242bef1fa5abdd485db293ed124f1ba",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e536bfe9ca617-FRA",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "fra",
    "secondsAfterDelete": 42.065547,
    "completedSecondsAfterDelete": 42.307988
  },
  {
    "at": "2026-11-05T19:39:41.024558+00:00",
    "completedAt": "2026-11-05T19:39:41.277997+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-b34afabf6459dd76/d51aa4a77fc362dfd23b8391b8d5fdd8fa2ddd0fa50a1a4360f7f754bde72164",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e536d8c62fbd9-FRA",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "fra",
    "secondsAfterDelete": 42.308058,
    "completedSecondsAfterDelete": 42.561497
  },
  {
    "at": "2026-11-05T19:39:41.278067+00:00",
    "completedAt": "2026-11-05T19:39:41.490196+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-4a0b09657d578ff8/a572af5ecdee840d36cb5116da33a801278f2561a92b5002d65bbb99ab31bce8",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e536f183a2be5-FRA",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "fra",
    "secondsAfterDelete": 42.561567,
    "completedSecondsAfterDelete": 42.773696
  },
  {
    "at": "2026-11-05T19:39:41.490255+00:00",
    "completedAt": "2026-11-05T19:39:41.704656+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-0394df191d3867b4/6b073e16b70c749e0ab0bf2d75a1ca208d0b11adfb0919bacb8c0a456c093738",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e53706b25c95d-FRA",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "fra",
    "secondsAfterDelete": 42.773755,
    "completedSecondsAfterDelete": 42.988156
  },
  {
    "at": "2026-11-05T19:39:41.704724+00:00",
    "completedAt": "2026-11-05T19:39:42.015324+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-c616890b73791c7f/d2959ab702b5b114313f960a4b1d8b62bdfab45dead3939925ecaf96ea1bd3b7",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e5371cdde4d8f-FRA",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "fra",
    "secondsAfterDelete": 42.988224,
    "completedSecondsAfterDelete": 43.298824
  },
  {
    "at": "2026-11-05T19:40:02.609692+00:00",
    "completedAt": "2026-11-05T19:40:02.891352+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-20598a059ba4c8ec/b6e5245bd0ccc4364fa745e0947cbc87c66f8a37847030f253fb239f11ae5a38",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e53f49b8cd7eb-GRU",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "gru",
    "secondsAfterDelete": 63.893192,
    "completedSecondsAfterDelete": 64.174852
  },
  {
    "at": "2026-11-05T19:40:02.891433+00:00",
    "completedAt": "2026-11-05T19:40:03.224291+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-ae12fdd14a813c5d/7f4caa9c80a6326fe96fc0a91372d215b229d50f23f6bfdd40e5692a1f0cca23",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e53f62c03c618-GRU",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "gru",
    "secondsAfterDelete": 64.174933,
    "completedSecondsAfterDelete": 64.507791
  },
  {
    "at": "2026-11-05T19:40:03.224360+00:00",
    "completedAt": "2026-11-05T19:40:03.455070+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-268f60bfc571f8c1/d7249b615380e29eb05b9c72478e160e4242bef1fa5abdd485db293ed124f1ba",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e53f83e7f2524-GRU",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "gru",
    "secondsAfterDelete": 64.50786,
    "completedSecondsAfterDelete": 64.73857
  },
  {
    "at": "2026-11-05T19:40:03.455133+00:00",
    "completedAt": "2026-11-05T19:40:03.667205+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-b34afabf6459dd76/d51aa4a77fc362dfd23b8391b8d5fdd8fa2ddd0fa50a1a4360f7f754bde72164",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e53f9bd1cb2c2-GRU",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "gru",
    "secondsAfterDelete": 64.738633,
    "completedSecondsAfterDelete": 64.950705
  },
  {
    "at": "2026-11-05T19:40:03.667268+00:00",
    "completedAt": "2026-11-05T19:40:03.907008+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-4a0b09657d578ff8/a572af5ecdee840d36cb5116da33a801278f2561a92b5002d65bbb99ab31bce8",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e53fb0ef0f21f-GRU",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "gru",
    "secondsAfterDelete": 64.950768,
    "completedSecondsAfterDelete": 65.190508
  },
  {
    "at": "2026-11-05T19:40:03.907071+00:00",
    "completedAt": "2026-11-05T19:40:04.155298+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-0394df191d3867b4/6b073e16b70c749e0ab0bf2d75a1ca208d0b11adfb0919bacb8c0a456c093738",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e53fc896caf4a-GRU",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "gru",
    "secondsAfterDelete": 65.190571,
    "completedSecondsAfterDelete": 65.438798
  },
  {
    "at": "2026-11-05T19:40:04.155366+00:00",
    "completedAt": "2026-11-05T19:40:04.422423+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-c616890b73791c7f/d2959ab702b5b114313f960a4b1d8b62bdfab45dead3939925ecaf96ea1bd3b7",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e53fe0f6df1d6-GRU",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "gru",
    "secondsAfterDelete": 65.438866,
    "completedSecondsAfterDelete": 65.705923
  },
  {
    "at": "2026-11-05T19:40:04.928182+00:00",
    "completedAt": "2026-11-05T19:40:05.165236+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-20598a059ba4c8ec/b6e5245bd0ccc4364fa745e0947cbc87c66f8a37847030f253fb239f11ae5a38",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e54031ef6942c-SJC",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "sjc",
    "secondsAfterDelete": 66.211682,
    "completedSecondsAfterDelete": 66.448736
  },
  {
    "at": "2026-11-05T19:40:05.165310+00:00",
    "completedAt": "2026-11-05T19:40:05.367844+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-ae12fdd14a813c5d/7f4caa9c80a6326fe96fc0a91372d215b229d50f23f6bfdd40e5692a1f0cca23",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e54046cd62b29-SJC",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "sjc",
    "secondsAfterDelete": 66.44881,
    "completedSecondsAfterDelete": 66.651344
  },
  {
    "at": "2026-11-05T19:40:05.367919+00:00",
    "completedAt": "2026-11-05T19:40:05.611153+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-268f60bfc571f8c1/d7249b615380e29eb05b9c72478e160e4242bef1fa5abdd485db293ed124f1ba",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e5405ac3ee16d-SJC",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "sjc",
    "secondsAfterDelete": 66.651419,
    "completedSecondsAfterDelete": 66.894653
  },
  {
    "at": "2026-11-05T19:40:05.611215+00:00",
    "completedAt": "2026-11-05T19:40:05.798249+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-b34afabf6459dd76/d51aa4a77fc362dfd23b8391b8d5fdd8fa2ddd0fa50a1a4360f7f754bde72164",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e540728cbc89f-SJC",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "sjc",
    "secondsAfterDelete": 66.894715,
    "completedSecondsAfterDelete": 67.081749
  },
  {
    "at": "2026-11-05T19:40:05.798310+00:00",
    "completedAt": "2026-11-05T19:40:06.007841+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-4a0b09657d578ff8/a572af5ecdee840d36cb5116da33a801278f2561a92b5002d65bbb99ab31bce8",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e54085f9f7a20-SJC",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "sjc",
    "secondsAfterDelete": 67.08181,
    "completedSecondsAfterDelete": 67.291341
  },
  {
    "at": "2026-11-05T19:40:06.007904+00:00",
    "completedAt": "2026-11-05T19:40:06.260755+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-0394df191d3867b4/6b073e16b70c749e0ab0bf2d75a1ca208d0b11adfb0919bacb8c0a456c093738",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e5409dea37aca-SJC",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "sjc",
    "secondsAfterDelete": 67.291404,
    "completedSecondsAfterDelete": 67.544255
  },
  {
    "at": "2026-11-05T19:40:06.260817+00:00",
    "completedAt": "2026-11-05T19:40:06.510573+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-c616890b73791c7f/d2959ab702b5b114313f960a4b1d8b62bdfab45dead3939925ecaf96ea1bd3b7",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e540b3ac2f97f-SJC",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "sjc",
    "secondsAfterDelete": 67.544317,
    "completedSecondsAfterDelete": 67.794073
  },
  {
    "at": "2026-11-05T19:40:04.735810+00:00",
    "completedAt": "2026-11-05T19:40:05.025656+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-20598a059ba4c8ec/b6e5245bd0ccc4364fa745e0947cbc87c66f8a37847030f253fb239f11ae5a38",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e5401ea5bd2a8-FRA",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "fra",
    "secondsAfterDelete": 66.01931,
    "completedSecondsAfterDelete": 66.309156
  },
  {
    "at": "2026-11-05T19:40:05.025726+00:00",
    "completedAt": "2026-11-05T19:40:05.295837+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-ae12fdd14a813c5d/7f4caa9c80a6326fe96fc0a91372d215b229d50f23f6bfdd40e5692a1f0cca23",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e54038d2c43d2-FRA",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "fra",
    "secondsAfterDelete": 66.309226,
    "completedSecondsAfterDelete": 66.579337
  },
  {
    "at": "2026-11-05T19:40:05.295907+00:00",
    "completedAt": "2026-11-05T19:40:05.503828+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-268f60bfc571f8c1/d7249b615380e29eb05b9c72478e160e4242bef1fa5abdd485db293ed124f1ba",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e5405389d9fce-FRA",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "fra",
    "secondsAfterDelete": 66.579407,
    "completedSecondsAfterDelete": 66.787328
  },
  {
    "at": "2026-11-05T19:40:05.503887+00:00",
    "completedAt": "2026-11-05T19:40:05.799252+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-b34afabf6459dd76/d51aa4a77fc362dfd23b8391b8d5fdd8fa2ddd0fa50a1a4360f7f754bde72164",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e5406a94af721-FRA",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "fra",
    "secondsAfterDelete": 66.787387,
    "completedSecondsAfterDelete": 67.082752
  },
  {
    "at": "2026-11-05T19:40:05.799313+00:00",
    "completedAt": "2026-11-05T19:40:06.020656+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-4a0b09657d578ff8/a572af5ecdee840d36cb5116da33a801278f2561a92b5002d65bbb99ab31bce8",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e5408694754eb-FRA",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "fra",
    "secondsAfterDelete": 67.082813,
    "completedSecondsAfterDelete": 67.304156
  },
  {
    "at": "2026-11-05T19:40:06.020724+00:00",
    "completedAt": "2026-11-05T19:40:06.221964+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-0394df191d3867b4/6b073e16b70c749e0ab0bf2d75a1ca208d0b11adfb0919bacb8c0a456c093738",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e5409bb40dca4-FRA",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "fra",
    "secondsAfterDelete": 67.304224,
    "completedSecondsAfterDelete": 67.505464
  },
  {
    "at": "2026-11-05T19:40:06.222027+00:00",
    "completedAt": "2026-11-05T19:40:06.474088+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-c616890b73791c7f/d2959ab702b5b114313f960a4b1d8b62bdfab45dead3939925ecaf96ea1bd3b7",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e540afc8c90e7-FRA",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "fra",
    "secondsAfterDelete": 67.505527,
    "completedSecondsAfterDelete": 67.757588
  },
  {
    "at": "2026-11-05T19:40:23.491980+00:00",
    "completedAt": "2026-11-05T19:40:23.760802+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-20598a059ba4c8ec/b6e5245bd0ccc4364fa745e0947cbc87c66f8a37847030f253fb239f11ae5a38",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e54771b46a4f6-GRU",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "gru",
    "secondsAfterDelete": 84.77548,
    "completedSecondsAfterDelete": 85.044302
  },
  {
    "at": "2026-11-05T19:40:23.760911+00:00",
    "completedAt": "2026-11-05T19:40:23.980080+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-ae12fdd14a813c5d/7f4caa9c80a6326fe96fc0a91372d215b229d50f23f6bfdd40e5692a1f0cca23",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e54789996721c-GRU",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "gru",
    "secondsAfterDelete": 85.044411,
    "completedSecondsAfterDelete": 85.26358
  },
  {
    "at": "2026-11-05T19:40:23.980146+00:00",
    "completedAt": "2026-11-05T19:40:24.360445+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-268f60bfc571f8c1/d7249b615380e29eb05b9c72478e160e4242bef1fa5abdd485db293ed124f1ba",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e547a0f181b27-GRU",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "gru",
    "secondsAfterDelete": 85.263646,
    "completedSecondsAfterDelete": 85.643945
  },
  {
    "at": "2026-11-05T19:40:24.360506+00:00",
    "completedAt": "2026-11-05T19:40:24.643396+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-b34afabf6459dd76/d51aa4a77fc362dfd23b8391b8d5fdd8fa2ddd0fa50a1a4360f7f754bde72164",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e547c5b561225-GRU",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "gru",
    "secondsAfterDelete": 85.644006,
    "completedSecondsAfterDelete": 85.926896
  },
  {
    "at": "2026-11-05T19:40:24.643458+00:00",
    "completedAt": "2026-11-05T19:40:24.885554+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-4a0b09657d578ff8/a572af5ecdee840d36cb5116da33a801278f2561a92b5002d65bbb99ab31bce8",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e547e2fc4a41a-GRU",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "gru",
    "secondsAfterDelete": 85.926958,
    "completedSecondsAfterDelete": 86.169054
  },
  {
    "at": "2026-11-05T19:40:24.885618+00:00",
    "completedAt": "2026-11-05T19:40:25.128216+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-0394df191d3867b4/6b073e16b70c749e0ab0bf2d75a1ca208d0b11adfb0919bacb8c0a456c093738",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e547f99e2f1d7-GRU",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "gru",
    "secondsAfterDelete": 86.169118,
    "completedSecondsAfterDelete": 86.411716
  },
  {
    "at": "2026-11-05T19:40:25.128277+00:00",
    "completedAt": "2026-11-05T19:40:25.372508+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-c616890b73791c7f/d2959ab702b5b114313f960a4b1d8b62bdfab45dead3939925ecaf96ea1bd3b7",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e54812f621ab4-GRU",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "gru",
    "secondsAfterDelete": 86.411777,
    "completedSecondsAfterDelete": 86.656008
  },
  {
    "at": "2026-11-05T19:40:25.465826+00:00",
    "completedAt": "2026-11-05T19:40:25.664327+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-20598a059ba4c8ec/b6e5245bd0ccc4364fa745e0947cbc87c66f8a37847030f253fb239f11ae5a38",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e54837f062368-SJC",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "sjc",
    "secondsAfterDelete": 86.749326,
    "completedSecondsAfterDelete": 86.947827
  },
  {
    "at": "2026-11-05T19:40:25.664401+00:00",
    "completedAt": "2026-11-05T19:40:25.865706+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-ae12fdd14a813c5d/7f4caa9c80a6326fe96fc0a91372d215b229d50f23f6bfdd40e5692a1f0cca23",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e54847c3f5c1c-SJC",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "sjc",
    "secondsAfterDelete": 86.947901,
    "completedSecondsAfterDelete": 87.149206
  },
  {
    "at": "2026-11-05T19:40:25.865773+00:00",
    "completedAt": "2026-11-05T19:40:26.087294+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-268f60bfc571f8c1/d7249b615380e29eb05b9c72478e160e4242bef1fa5abdd485db293ed124f1ba",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e5485bcfaeb31-SJC",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "sjc",
    "secondsAfterDelete": 87.149273,
    "completedSecondsAfterDelete": 87.370794
  },
  {
    "at": "2026-11-05T19:40:26.087360+00:00",
    "completedAt": "2026-11-05T19:40:26.306700+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-b34afabf6459dd76/d51aa4a77fc362dfd23b8391b8d5fdd8fa2ddd0fa50a1a4360f7f754bde72164",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e54872bbfeb36-SJC",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "sjc",
    "secondsAfterDelete": 87.37086,
    "completedSecondsAfterDelete": 87.5902
  },
  {
    "at": "2026-11-05T19:40:26.306765+00:00",
    "completedAt": "2026-11-05T19:40:26.498403+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-4a0b09657d578ff8/a572af5ecdee840d36cb5116da33a801278f2561a92b5002d65bbb99ab31bce8",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e54888894eb1e-SJC",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "sjc",
    "secondsAfterDelete": 87.590265,
    "completedSecondsAfterDelete": 87.781903
  },
  {
    "at": "2026-11-05T19:40:26.498467+00:00",
    "completedAt": "2026-11-05T19:40:26.723013+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-0394df191d3867b4/6b073e16b70c749e0ab0bf2d75a1ca208d0b11adfb0919bacb8c0a456c093738",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e5489d953d039-SJC",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "sjc",
    "secondsAfterDelete": 87.781967,
    "completedSecondsAfterDelete": 88.006513
  },
  {
    "at": "2026-11-05T19:40:26.723107+00:00",
    "completedAt": "2026-11-05T19:40:26.958857+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-c616890b73791c7f/d2959ab702b5b114313f960a4b1d8b62bdfab45dead3939925ecaf96ea1bd3b7",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e548b2922cfb0-SJC",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "sjc",
    "secondsAfterDelete": 88.006607,
    "completedSecondsAfterDelete": 88.242357
  },
  {
    "at": "2026-11-05T19:40:25.955004+00:00",
    "completedAt": "2026-11-05T19:40:26.216809+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-20598a059ba4c8ec/b6e5245bd0ccc4364fa745e0947cbc87c66f8a37847030f253fb239f11ae5a38",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e54867a05d25c-FRA",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "fra",
    "secondsAfterDelete": 87.238504,
    "completedSecondsAfterDelete": 87.500309
  },
  {
    "at": "2026-11-05T19:40:26.216879+00:00",
    "completedAt": "2026-11-05T19:40:26.466494+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-ae12fdd14a813c5d/7f4caa9c80a6326fe96fc0a91372d215b229d50f23f6bfdd40e5692a1f0cca23",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e5487fea2fc78-FRA",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "fra",
    "secondsAfterDelete": 87.500379,
    "completedSecondsAfterDelete": 87.749994
  },
  {
    "at": "2026-11-05T19:40:26.466558+00:00",
    "completedAt": "2026-11-05T19:40:26.691337+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-268f60bfc571f8c1/d7249b615380e29eb05b9c72478e160e4242bef1fa5abdd485db293ed124f1ba",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e54898e45d2f6-FRA",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "fra",
    "secondsAfterDelete": 87.750058,
    "completedSecondsAfterDelete": 87.974837
  },
  {
    "at": "2026-11-05T19:40:26.691398+00:00",
    "completedAt": "2026-11-05T19:40:26.936982+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-b34afabf6459dd76/d51aa4a77fc362dfd23b8391b8d5fdd8fa2ddd0fa50a1a4360f7f754bde72164",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e548afca54d7f-FRA",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "fra",
    "secondsAfterDelete": 87.974898,
    "completedSecondsAfterDelete": 88.220482
  },
  {
    "at": "2026-11-05T19:40:26.937048+00:00",
    "completedAt": "2026-11-05T19:40:27.157206+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-4a0b09657d578ff8/a572af5ecdee840d36cb5116da33a801278f2561a92b5002d65bbb99ab31bce8",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e548c7b1a1fad-FRA",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "fra",
    "secondsAfterDelete": 88.220548,
    "completedSecondsAfterDelete": 88.440706
  },
  {
    "at": "2026-11-05T19:40:27.157271+00:00",
    "completedAt": "2026-11-05T19:40:27.396999+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-0394df191d3867b4/6b073e16b70c749e0ab0bf2d75a1ca208d0b11adfb0919bacb8c0a456c093738",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e548dd9c3a0af-FRA",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "fra",
    "secondsAfterDelete": 88.440771,
    "completedSecondsAfterDelete": 88.680499
  },
  {
    "at": "2026-11-05T19:40:27.397060+00:00",
    "completedAt": "2026-11-05T19:40:27.646163+00:00",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-c616890b73791c7f/d2959ab702b5b114313f960a4b1d8b62bdfab45dead3939925ecaf96ea1bd3b7",
    "repeat": 0,
    "status": 404,
    "bytes": 27150,
    "sha256": "62c1c0b235e26952857139537b65f8272026cd1c385c1bf6dba20481ee8a6619",
    "headers": {
      "cf-cache-status": "BYPASS",
      "cf-ray": "a55e548f4bc5d395-FRA",
      "age": null,
      "cache-control": null
    },
    "kind": "cdn-probe",
    "region": "fra",
    "secondsAfterDelete": 88.68056,
    "completedSecondsAfterDelete": 88.929663
  }
]
```

## Manifest object metadata (no key material)

```json
[
  {
    "offset": "snap.27.0",
    "next_offset": "27",
    "binding": "h1-20598a059ba4c8ec\u0000snap.27.0",
    "object": "v1/shapes/h1-20598a059ba4c8ec/b6e5245bd0ccc4364fa745e0947cbc87c66f8a37847030f253fb239f11ae5a38",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-20598a059ba4c8ec/b6e5245bd0ccc4364fa745e0947cbc87c66f8a37847030f253fb239f11ae5a38",
    "expires_at": "2026-11-05T19:40:00+00:00",
    "epoch": "2989845",
    "key_id": "01M6PKF1T357ENMXHF1EPR8YMW",
    "sha256": "f463feabc99e1cbf18097d1499e75abd3724a7ad8328a715ad5fe156a40b3cf1",
    "size": 2364,
    "shapePath": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items0/shape",
    "handle": "h1-20598a059ba4c8ec"
  },
  {
    "offset": "snap.27.0",
    "next_offset": "27",
    "binding": "h1-ae12fdd14a813c5d\u0000snap.27.0",
    "object": "v1/shapes/h1-ae12fdd14a813c5d/7f4caa9c80a6326fe96fc0a91372d215b229d50f23f6bfdd40e5692a1f0cca23",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-ae12fdd14a813c5d/7f4caa9c80a6326fe96fc0a91372d215b229d50f23f6bfdd40e5692a1f0cca23",
    "expires_at": "2026-11-05T19:40:00+00:00",
    "epoch": "2989845",
    "key_id": "01M6PKF1T357ENMXHF1EPR8YMW",
    "sha256": "ffefe667aaab4c31eed010b7a334ce6226a6ce04480b1f29118675ace1a6b3cf",
    "size": 2364,
    "shapePath": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items1/shape",
    "handle": "h1-ae12fdd14a813c5d"
  },
  {
    "offset": "snap.27.0",
    "next_offset": "27",
    "binding": "h1-268f60bfc571f8c1\u0000snap.27.0",
    "object": "v1/shapes/h1-268f60bfc571f8c1/d7249b615380e29eb05b9c72478e160e4242bef1fa5abdd485db293ed124f1ba",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-268f60bfc571f8c1/d7249b615380e29eb05b9c72478e160e4242bef1fa5abdd485db293ed124f1ba",
    "expires_at": "2026-11-05T19:40:00+00:00",
    "epoch": "2989845",
    "key_id": "01M6PKF1T357ENMXHF1EPR8YMW",
    "sha256": "aad49a36e9fb68f3c78aac0a48112ecfc19081cfa0032c1edcb0dd77b3248555",
    "size": 2364,
    "shapePath": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items2/shape",
    "handle": "h1-268f60bfc571f8c1"
  },
  {
    "offset": "snap.27.0",
    "next_offset": "27",
    "binding": "h1-b34afabf6459dd76\u0000snap.27.0",
    "object": "v1/shapes/h1-b34afabf6459dd76/d51aa4a77fc362dfd23b8391b8d5fdd8fa2ddd0fa50a1a4360f7f754bde72164",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-b34afabf6459dd76/d51aa4a77fc362dfd23b8391b8d5fdd8fa2ddd0fa50a1a4360f7f754bde72164",
    "expires_at": "2026-11-05T19:40:00+00:00",
    "epoch": "2989845",
    "key_id": "01M6PKF1T357ENMXHF1EPR8YMW",
    "sha256": "8b410f6c3a82afac9098c14fc2369ab00d4ea1efd759a07ef7221450fd2d1e24",
    "size": 2364,
    "shapePath": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items3/shape",
    "handle": "h1-b34afabf6459dd76"
  },
  {
    "offset": "snap.27.0",
    "next_offset": "27",
    "binding": "h1-4a0b09657d578ff8\u0000snap.27.0",
    "object": "v1/shapes/h1-4a0b09657d578ff8/a572af5ecdee840d36cb5116da33a801278f2561a92b5002d65bbb99ab31bce8",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-4a0b09657d578ff8/a572af5ecdee840d36cb5116da33a801278f2561a92b5002d65bbb99ab31bce8",
    "expires_at": "2026-11-05T19:40:00+00:00",
    "epoch": "2989845",
    "key_id": "01M6PKF1T357ENMXHF1EPR8YMW",
    "sha256": "9d0d022d072f57ae71ced9323989b4bbe5d5768e1de5d4b8d6f969949f235b2e",
    "size": 2364,
    "shapePath": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items4/shape",
    "handle": "h1-4a0b09657d578ff8"
  },
  {
    "offset": "snap.27.0",
    "next_offset": "27",
    "binding": "h1-0394df191d3867b4\u0000snap.27.0",
    "object": "v1/shapes/h1-0394df191d3867b4/6b073e16b70c749e0ab0bf2d75a1ca208d0b11adfb0919bacb8c0a456c093738",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-0394df191d3867b4/6b073e16b70c749e0ab0bf2d75a1ca208d0b11adfb0919bacb8c0a456c093738",
    "expires_at": "2026-11-05T19:40:00+00:00",
    "epoch": "2989845",
    "key_id": "01M6PKF1T357ENMXHF1EPR8YMW",
    "sha256": "cdbaec1eaf7b8bdcf04c3a0605bc5889692b3157ad03322a4a2d8315199c6f3e",
    "size": 2364,
    "shapePath": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items5/shape",
    "handle": "h1-0394df191d3867b4"
  },
  {
    "offset": "snap.27.0",
    "next_offset": "27",
    "binding": "h1-c616890b73791c7f\u0000snap.27.0",
    "object": "v1/shapes/h1-c616890b73791c7f/d2959ab702b5b114313f960a4b1d8b62bdfab45dead3939925ecaf96ea1bd3b7",
    "url": "https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-c616890b73791c7f/d2959ab702b5b114313f960a4b1d8b62bdfab45dead3939925ecaf96ea1bd3b7",
    "expires_at": "2026-11-05T19:40:00+00:00",
    "epoch": "2989845",
    "key_id": "01M6PKF1T357ENMXHF1EPR8YMW",
    "sha256": "d2b28aad5bb259195d21fa779d109b42fb7e2fa4509b58651ed4c00df8c94023",
    "size": 2364,
    "shapePath": "/v1/projects/01M6PH2AQ2F0KTT8RACYR9JWXD/databases/01M6PKF16Q4N8CK96D943M3NT2/tables/items6/shape",
    "handle": "h1-c616890b73791c7f"
  }
]
```

## All six nodes before fresh proof

```json
{"at": "2026-11-05T19:37:13.848785+00:00", "nodes": [{"node": "ts-fra-1", "region": "fra", "health": {"status": "ok", "version": "0.1.0", "region": "fra", "storageFormat": "23-41a88901325a", "revision": "afa0171a52fef117de5b37149fc78002075dc9a5", "machine": "ts-fra-1"}, "ready": {"status": "ready", "version": "0.1.0", "region": "fra", "storageFormat": "23-41a88901325a", "revision": "afa0171a52fef117de5b37149fc78002075dc9a5", "machine": "ts-fra-1"}, "at": "2026-11-05T19:37:11.659262+00:00"}, {"node": "ts-fra-2", "region": "fra", "health": {"status": "ok", "version": "0.1.0", "region": "fra", "storageFormat": "23-41a88901325a", "revision": "afa0171a52fef117de5b37149fc78002075dc9a5", "machine": "ts-fra-2"}, "ready": {"status": "ready", "version": "0.1.0", "region": "fra", "storageFormat": "23-41a88901325a", "revision": "afa0171a52fef117de5b37149fc78002075dc9a5", "machine": "ts-fra-2"}, "at": "2026-11-05T19:37:12.428327+00:00"}, {"node": "ts-gru-1", "region": "gru", "health": {"status": "ok", "version": "0.1.0", "region": "gru", "storageFormat": "23-41a88901325a", "revision": "afa0171a52fef117de5b37149fc78002075dc9a5", "machine": "ts-gru-1"}, "ready": {"status": "ready", "version": "0.1.0", "region": "gru", "storageFormat": "23-41a88901325a", "revision": "afa0171a52fef117de5b37149fc78002075dc9a5", "machine": "ts-gru-1"}, "at": "2026-11-05T19:37:12.447571+00:00"}, {"node": "ts-gru-2", "region": "gru", "health": {"status": "ok", "version": "0.1.0", "region": "gru", "storageFormat": "23-41a88901325a", "revision": "afa0171a52fef117de5b37149fc78002075dc9a5", "machine": "ts-gru-2"}, "ready": {"status": "ready", "version": "0.1.0", "region": "gru", "storageFormat": "23-41a88901325a", "revision": "afa0171a52fef117de5b37149fc78002075dc9a5", "machine": "ts-gru-2"}, "at": "2026-11-05T19:37:12.469739+00:00"}, {"node": "ts-sjc-1", "region": "sjc", "health": {"status": "ok", "version": "0.1.0", "region": "sjc", "storageFormat": "23-41a88901325a", "revision": "afa0171a52fef117de5b37149fc78002075dc9a5", "machine": "ts-sjc-1"}, "ready": {"status": "ready", "version": "0.1.0", "region": "sjc", "storageFormat": "23-41a88901325a", "revision": "afa0171a52fef117de5b37149fc78002075dc9a5", "machine": "ts-sjc-1"}, "at": "2026-11-05T19:37:13.157857+00:00"}, {"node": "ts-sjc-2", "region": "sjc", "health": {"status": "ok", "version": "0.1.0", "region": "sjc", "storageFormat": "23-41a88901325a", "revision": "afa0171a52fef117de5b37149fc78002075dc9a5", "machine": "ts-sjc-2"}, "ready": {"status": "ready", "version": "0.1.0", "region": "sjc", "storageFormat": "23-41a88901325a", "revision": "afa0171a52fef117de5b37149fc78002075dc9a5", "machine": "ts-sjc-2"}, "at": "2026-11-05T19:37:13.848764+00:00"}]}
```

## All six nodes after proof

```json
{"at": "2026-11-05T19:40:06.427246+00:00", "nodes": [{"node": "ts-fra-1", "region": "fra", "health": {"status": "ok", "version": "0.1.0", "region": "fra", "storageFormat": "23-41a88901325a", "revision": "afa0171a52fef117de5b37149fc78002075dc9a5", "machine": "ts-fra-1"}, "ready": {"status": "ready", "version": "0.1.0", "region": "fra", "storageFormat": "23-41a88901325a", "revision": "afa0171a52fef117de5b37149fc78002075dc9a5", "machine": "ts-fra-1"}, "at": "2026-11-05T19:40:04.239995+00:00"}, {"node": "ts-fra-2", "region": "fra", "health": {"status": "ok", "version": "0.1.0", "region": "fra", "storageFormat": "23-41a88901325a", "revision": "afa0171a52fef117de5b37149fc78002075dc9a5", "machine": "ts-fra-2"}, "ready": {"status": "ready", "version": "0.1.0", "region": "fra", "storageFormat": "23-41a88901325a", "revision": "afa0171a52fef117de5b37149fc78002075dc9a5", "machine": "ts-fra-2"}, "at": "2026-11-05T19:40:05.008610+00:00"}, {"node": "ts-gru-1", "region": "gru", "health": {"status": "ok", "version": "0.1.0", "region": "gru", "storageFormat": "23-41a88901325a", "revision": "afa0171a52fef117de5b37149fc78002075dc9a5", "machine": "ts-gru-1"}, "ready": {"status": "ready", "version": "0.1.0", "region": "gru", "storageFormat": "23-41a88901325a", "revision": "afa0171a52fef117de5b37149fc78002075dc9a5", "machine": "ts-gru-1"}, "at": "2026-11-05T19:40:05.026184+00:00"}, {"node": "ts-gru-2", "region": "gru", "health": {"status": "ok", "version": "0.1.0", "region": "gru", "storageFormat": "23-41a88901325a", "revision": "afa0171a52fef117de5b37149fc78002075dc9a5", "machine": "ts-gru-2"}, "ready": {"status": "ready", "version": "0.1.0", "region": "gru", "storageFormat": "23-41a88901325a", "revision": "afa0171a52fef117de5b37149fc78002075dc9a5", "machine": "ts-gru-2"}, "at": "2026-11-05T19:40:05.047352+00:00"}, {"node": "ts-sjc-1", "region": "sjc", "health": {"status": "ok", "version": "0.1.0", "region": "sjc", "storageFormat": "23-41a88901325a", "revision": "afa0171a52fef117de5b37149fc78002075dc9a5", "machine": "ts-sjc-1"}, "ready": {"status": "ready", "version": "0.1.0", "region": "sjc", "storageFormat": "23-41a88901325a", "revision": "afa0171a52fef117de5b37149fc78002075dc9a5", "machine": "ts-sjc-1"}, "at": "2026-11-05T19:40:05.737199+00:00"}, {"node": "ts-sjc-2", "region": "sjc", "health": {"status": "ok", "version": "0.1.0", "region": "sjc", "storageFormat": "23-41a88901325a", "revision": "afa0171a52fef117de5b37149fc78002075dc9a5", "machine": "ts-sjc-2"}, "ready": {"status": "ready", "version": "0.1.0", "region": "sjc", "storageFormat": "23-41a88901325a", "revision": "afa0171a52fef117de5b37149fc78002075dc9a5", "machine": "ts-sjc-2"}, "at": "2026-11-05T19:40:06.427223+00:00"}]}
```

## FRA metrics before proof

```text
# TYPE terrabase_shape_cdn_purge_oldest_age_seconds gauge
# HELP terrabase_shape_cdn_purge_oldest_age_seconds Age of the oldest queued CDN URL; healthy-provider removal bound is 420 seconds.
terrabase_shape_cdn_purge_oldest_age_seconds{otel_scope_name="TerraScale.TerraBase"} 0
# TYPE terrabase_shape_cdn_purge_pending gauge
# HELP terrabase_shape_cdn_purge_pending Durable CDN purge URLs awaiting verified removal.
terrabase_shape_cdn_purge_pending{otel_scope_name="TerraScale.TerraBase"} 0
```

## FRA metrics during purge

```text
# TYPE terrabase_shape_cdn_purge_attempts_total counter
# HELP terrabase_shape_cdn_purge_attempts_total Cloudflare purge requests by accepted or failed result; includes retries.
terrabase_shape_cdn_purge_attempts_total{otel_scope_name="TerraScale.TerraBase",outcome="accepted"} 2
# TYPE terrabase_shape_cdn_purge_oldest_age_seconds gauge
# HELP terrabase_shape_cdn_purge_oldest_age_seconds Age of the oldest queued CDN URL; healthy-provider removal bound is 420 seconds.
terrabase_shape_cdn_purge_oldest_age_seconds{otel_scope_name="TerraScale.TerraBase"} 15.5978275
# TYPE terrabase_shape_cdn_purge_pending gauge
# HELP terrabase_shape_cdn_purge_pending Durable CDN purge URLs awaiting verified removal.
terrabase_shape_cdn_purge_pending{otel_scope_name="TerraScale.TerraBase"} 3
# TYPE terrabase_shape_cdn_purges_total counter
# HELP terrabase_shape_cdn_purges_total Shape CDN handle prefixes by purge outcome: purged, failed (Cloudflare refused or kept failing) or dropped (queue full).
terrabase_shape_cdn_purges_total{otel_scope_name="TerraScale.TerraBase",outcome="verification_failed"} 0
terrabase_shape_cdn_purges_total{otel_scope_name="TerraScale.TerraBase",outcome="purged"} 4
```

## FRA metrics after verified purge

```text
# TYPE terrabase_shape_cdn_purge_attempts_total counter
# HELP terrabase_shape_cdn_purge_attempts_total Cloudflare purge requests by accepted or failed result; includes retries.
terrabase_shape_cdn_purge_attempts_total{otel_scope_name="TerraScale.TerraBase",outcome="accepted"} 3
# TYPE terrabase_shape_cdn_purge_oldest_age_seconds gauge
# HELP terrabase_shape_cdn_purge_oldest_age_seconds Age of the oldest queued CDN URL; healthy-provider removal bound is 420 seconds.
terrabase_shape_cdn_purge_oldest_age_seconds{otel_scope_name="TerraScale.TerraBase"} 0
# TYPE terrabase_shape_cdn_purge_pending gauge
# HELP terrabase_shape_cdn_purge_pending Durable CDN purge URLs awaiting verified removal.
terrabase_shape_cdn_purge_pending{otel_scope_name="TerraScale.TerraBase"} 0
# TYPE terrabase_shape_cdn_purges_total counter
# HELP terrabase_shape_cdn_purges_total Shape CDN handle prefixes by purge outcome: purged, failed (Cloudflare refused or kept failing) or dropped (queue full).
terrabase_shape_cdn_purges_total{otel_scope_name="TerraScale.TerraBase",outcome="verification_failed"} 0
terrabase_shape_cdn_purges_total{otel_scope_name="TerraScale.TerraBase",outcome="purged"} 7
```
