# Raw live acknowledgement samples

```json
[
  {
    "rounds": [
      {
        "round": 1,
        "writes": 10,
        "p50_ms": 19.029,
        "p95_ms": 781.587,
        "routes": [
          "route=local; reason=ulid_insert; commit=local"
        ],
        "samples_ms": [
          781.587,
          39.381,
          15.819,
          16.462,
          20.057,
          19.168,
          26.118,
          15.911,
          16.765,
          18.89
        ],
        "server_timing": [
          "app;dur=778.96;desc=\"sjc\"",
          "app;dur=35.65;desc=\"sjc\"",
          "app;dur=13.73;desc=\"sjc\"",
          "app;dur=14.19;desc=\"sjc\"",
          "app;dur=16.95;desc=\"sjc\"",
          "app;dur=16.97;desc=\"sjc\"",
          "app;dur=23.54;desc=\"sjc\"",
          "app;dur=13.53;desc=\"sjc\"",
          "app;dur=14.24;desc=\"sjc\"",
          "app;dur=16.56;desc=\"sjc\""
        ]
      },
      {
        "round": 2,
        "writes": 10,
        "p50_ms": 17.877,
        "p95_ms": 19.285,
        "routes": [
          "route=local; reason=ulid_insert; commit=local"
        ],
        "samples_ms": [
          15.333,
          15.469,
          18.787,
          14.603,
          18.349,
          17.501,
          16.925,
          19.285,
          18.569,
          18.254
        ],
        "server_timing": [
          "app;dur=13.28;desc=\"sjc\"",
          "app;dur=13.40;desc=\"sjc\"",
          "app;dur=14.76;desc=\"sjc\"",
          "app;dur=12.80;desc=\"sjc\"",
          "app;dur=16.27;desc=\"sjc\"",
          "app;dur=15.16;desc=\"sjc\"",
          "app;dur=14.54;desc=\"sjc\"",
          "app;dur=16.89;desc=\"sjc\"",
          "app;dur=14.65;desc=\"sjc\"",
          "app;dur=15.16;desc=\"sjc\""
        ]
      },
      {
        "round": 3,
        "writes": 10,
        "p50_ms": 18.673,
        "p95_ms": 40.116,
        "routes": [
          "route=local; reason=ulid_insert; commit=local"
        ],
        "samples_ms": [
          16.573,
          18.535,
          25.42,
          17.906,
          14.447,
          15.88,
          21.23,
          18.811,
          22.647,
          40.116
        ],
        "server_timing": [
          "app;dur=14.14;desc=\"sjc\"",
          "app;dur=15.72;desc=\"sjc\"",
          "app;dur=23.15;desc=\"sjc\"",
          "app;dur=15.82;desc=\"sjc\"",
          "app;dur=12.41;desc=\"sjc\"",
          "app;dur=12.81;desc=\"sjc\"",
          "app;dur=18.43;desc=\"sjc\"",
          "app;dur=16.21;desc=\"sjc\"",
          "app;dur=20.60;desc=\"sjc\"",
          "app;dur=36.70;desc=\"sjc\""
        ]
      }
    ],
    "measurement": "HTTPS client acknowledgement from regional founder, persistent connection, 10 writes per round",
    "region": "sjc",
    "home_region": "gru",
    "origin": "ts-sjc-1",
    "holder": "ts-sjc-2",
    "database": "01M6P8X8F0E814E87B43NY3RVK",
    "holder_synced_204_before": 0,
    "holder_synced_204_after": 30.0,
    "home_rows": 30,
    "passed": true,
    "database_cleanup_status": 204
  },
  {
    "rounds": [
      {
        "round": 1,
        "writes": 10,
        "p50_ms": 14.909,
        "p95_ms": 1067.364,
        "routes": [
          "route=local; reason=ulid_insert; commit=local"
        ],
        "samples_ms": [
          1067.364,
          28.29,
          13.816,
          13.953,
          13.91,
          14.297,
          17.362,
          14.997,
          14.822,
          16.873
        ],
        "server_timing": [
          "app;dur=1065.24;desc=\"gru\"",
          "app;dur=26.10;desc=\"gru\"",
          "app;dur=11.92;desc=\"gru\"",
          "app;dur=12.01;desc=\"gru\"",
          "app;dur=12.07;desc=\"gru\"",
          "app;dur=12.52;desc=\"gru\"",
          "app;dur=14.95;desc=\"gru\"",
          "app;dur=12.89;desc=\"gru\"",
          "app;dur=12.87;desc=\"gru\"",
          "app;dur=14.70;desc=\"gru\""
        ]
      },
      {
        "round": 2,
        "writes": 10,
        "p50_ms": 15.282,
        "p95_ms": 21.027,
        "routes": [
          "route=local; reason=ulid_insert; commit=local"
        ],
        "samples_ms": [
          16.206,
          15.414,
          21.027,
          15.149,
          14.889,
          15.054,
          18.113,
          14.462,
          14.213,
          15.81
        ],
        "server_timing": [
          "app;dur=14.34;desc=\"gru\"",
          "app;dur=13.48;desc=\"gru\"",
          "app;dur=18.58;desc=\"gru\"",
          "app;dur=13.13;desc=\"gru\"",
          "app;dur=12.97;desc=\"gru\"",
          "app;dur=12.74;desc=\"gru\"",
          "app;dur=16.00;desc=\"gru\"",
          "app;dur=12.34;desc=\"gru\"",
          "app;dur=12.07;desc=\"gru\"",
          "app;dur=13.97;desc=\"gru\""
        ]
      },
      {
        "round": 3,
        "writes": 10,
        "p50_ms": 15.491,
        "p95_ms": 58.828,
        "routes": [
          "route=local; reason=ulid_insert; commit=local"
        ],
        "samples_ms": [
          17.446,
          26.231,
          14.779,
          15.643,
          14.454,
          34.535,
          15.2,
          15.339,
          14.885,
          58.828
        ],
        "server_timing": [
          "app;dur=15.74;desc=\"gru\"",
          "app;dur=24.44;desc=\"gru\"",
          "app;dur=12.98;desc=\"gru\"",
          "app;dur=13.76;desc=\"gru\"",
          "app;dur=12.60;desc=\"gru\"",
          "app;dur=32.67;desc=\"gru\"",
          "app;dur=13.34;desc=\"gru\"",
          "app;dur=13.37;desc=\"gru\"",
          "app;dur=12.93;desc=\"gru\"",
          "app;dur=56.13;desc=\"gru\""
        ]
      }
    ],
    "measurement": "HTTPS client acknowledgement from regional founder, persistent connection, 10 writes per round",
    "region": "gru",
    "home_region": "fra",
    "origin": "ts-gru-1",
    "holder": "ts-gru-2",
    "database": "01M6P8XHYH3S7ZAYMTPJ25ZAXC",
    "holder_synced_204_before": 0,
    "holder_synced_204_after": 30.0,
    "home_rows": 30,
    "passed": true,
    "database_cleanup_status": 204
  },
  {
    "rounds": [
      {
        "round": 1,
        "writes": 10,
        "p50_ms": 16.711,
        "p95_ms": 1293.421,
        "routes": [
          "route=local; reason=ulid_insert; commit=local"
        ],
        "samples_ms": [
          1293.421,
          102.585,
          13.6,
          14.393,
          14.722,
          18.123,
          17.544,
          15.627,
          16.975,
          16.448
        ],
        "server_timing": [
          "app;dur=1291.59;desc=\"fra\"",
          "app;dur=99.66;desc=\"fra\"",
          "app;dur=12.15;desc=\"fra\"",
          "app;dur=12.83;desc=\"fra\"",
          "app;dur=12.36;desc=\"fra\"",
          "app;dur=16.19;desc=\"fra\"",
          "app;dur=15.70;desc=\"fra\"",
          "app;dur=12.91;desc=\"fra\"",
          "app;dur=13.84;desc=\"fra\"",
          "app;dur=14.63;desc=\"fra\""
        ]
      },
      {
        "round": 2,
        "writes": 10,
        "p50_ms": 15.509,
        "p95_ms": 39.333,
        "routes": [
          "route=local; reason=ulid_insert; commit=local"
        ],
        "samples_ms": [
          17.554,
          14.639,
          14.099,
          14.462,
          14.959,
          17.95,
          14.236,
          16.059,
          20.78,
          39.333
        ],
        "server_timing": [
          "app;dur=15.75;desc=\"fra\"",
          "app;dur=12.97;desc=\"fra\"",
          "app;dur=12.20;desc=\"fra\"",
          "app;dur=12.31;desc=\"fra\"",
          "app;dur=13.29;desc=\"fra\"",
          "app;dur=15.54;desc=\"fra\"",
          "app;dur=12.49;desc=\"fra\"",
          "app;dur=13.89;desc=\"fra\"",
          "app;dur=17.96;desc=\"fra\"",
          "app;dur=37.37;desc=\"fra\""
        ]
      },
      {
        "round": 3,
        "writes": 10,
        "p50_ms": 15.123,
        "p95_ms": 42.123,
        "routes": [
          "route=local; reason=ulid_insert; commit=local"
        ],
        "samples_ms": [
          18.085,
          14.573,
          14.451,
          28.644,
          15.116,
          13.81,
          16.267,
          42.123,
          15.13,
          14.654
        ],
        "server_timing": [
          "app;dur=16.50;desc=\"fra\"",
          "app;dur=12.89;desc=\"fra\"",
          "app;dur=12.47;desc=\"fra\"",
          "app;dur=26.13;desc=\"fra\"",
          "app;dur=13.33;desc=\"fra\"",
          "app;dur=12.03;desc=\"fra\"",
          "app;dur=14.23;desc=\"fra\"",
          "app;dur=40.24;desc=\"fra\"",
          "app;dur=13.38;desc=\"fra\"",
          "app;dur=12.37;desc=\"fra\""
        ]
      }
    ],
    "measurement": "HTTPS client acknowledgement from regional founder, persistent connection, 10 writes per round",
    "region": "fra",
    "home_region": "gru",
    "origin": "ts-fra-1",
    "holder": "ts-fra-2",
    "database": "01M6P8XY6RBG7JD9BJRT6PBMGP",
    "holder_synced_204_before": 0,
    "holder_synced_204_after": 30.0,
    "home_rows": 30,
    "passed": true,
    "database_cleanup_status": 204
  }
]
```
