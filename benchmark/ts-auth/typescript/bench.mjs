// ts-auth management API client benchmark for the official TypeScript SDK
// (@terrascale/sdk, the sibling ts-typescript checkout), mirroring the Go
// harness in ts-auth/go/bench.go lane for lane.
//
// A canned-response in-process fetch (no socket, no web stack) serves the
// exact route shapes the official client requests, so every measured
// operation pays the full client path — typed request, JSON encoding, fetch
// machinery, JSON decode, response verification — against a constant service
// cost. This is the same measurement class as the .NET `tsauth-sdk`
// engine-comparison lane (raw in-process handler, no socket).
//
// Usage:
//
//	taskset -c 0-3 nice -n 19 env NODE_OPTIONS=--max-old-space-size=4096 \
//	  node ts-auth/typescript/bench.mjs --samples 5 --operations 5000 --warmup 1000 \
//	  --output ts-auth/receipts/<date>-typescript-tsauth

import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { createTsAuthClient, TsAuthUserPatch } from "../../../ts-typescript/dist/ts-auth.js";

const instanceSlug = "bench";
const datasetSize = 1000;
const payloadSize = 256;
const accessToken = "sk_benchmark_key";

function parseArgs(argv) {
  const args = { samples: 5, operations: 5000, warmup: 1000, seed: 1337, output: "" };
  for (let index = 0; index < argv.length; index += 2) {
    const name = argv[index];
    if (!name.startsWith("--") || index + 1 >= argv.length) usageExit(`unexpected argument ${name}`);
    const value = Number(argv[index + 1]);
    switch (name) {
      case "--samples": args.samples = value; break;
      case "--operations": args.operations = value; break;
      case "--warmup": args.warmup = value; break;
      case "--seed": args.seed = value; break;
      case "--output": args.output = argv[index + 1]; break;
      default: usageExit(`unknown flag ${name}`);
    }
  }
  if (!Number.isInteger(args.samples) || args.samples < 5) {
    console.error("--samples must be at least 5");
    process.exit(2);
  }
  if (args.output === "") {
    console.error("--output is required");
    process.exit(2);
  }
  return args;
}

function usageExit(message) {
  console.error(message);
  process.exit(2);
}

// stub answers the ts-auth users routes with canned records from inside the
// process: the constant service cost isolates the official client's path.
// Records and status semantics mirror ts-auth/go/bench.go exactly.
const users = (() => {
  const dataset = new Array(datasetSize);
  const payload = "x".repeat(payloadSize);
  for (let index = 0; index < datasetSize; index++) {
    dataset[index] = `{"id":"usr_${index}","email":"bench-${index}@example.test","email_verified":true,"username":"bench${index}","name":"Bench User ${index}","banned":false,"ban_reason":null,"created_at":1700000000,"last_sign_in_at":null,"public_metadata":{"index":${index},"payload":"${payload}"},"private_metadata":{"plan":"pro","payload":"${payload}"}}`;
  }
  return dataset;
})();

function respond(status, body) {
  return new Response(body, { status, headers: { "content-type": "application/json" } });
}

async function stubFetch(request) {
  const url = new URL(request.url);
  const path = url.pathname;
  const prefix = `/v1/${instanceSlug}/users`;
  const authorization = request.headers.get("authorization");
  if (!path.startsWith(prefix) || authorization?.toLowerCase() !== `bearer ${accessToken}`) {
    return respond(401, `{"error":{"code":"unauthorized","message":"Missing or invalid credentials."}}`);
  }

  const remainder = path.slice(prefix.length);
  if (request.method === "GET" && remainder === "") {
    let limit = 20;
    const limitParam = url.searchParams.get("limit");
    if (limitParam !== null && limitParam !== "") limit = Number.parseInt(limitParam, 10);
    if (!Number.isInteger(limit) || limit < 1 || limit > 100) {
      return respond(400, `{"error":{"code":"invalid_request","message":"limit must be between 1 and 100"}}`);
    }
    let after = 0;
    const cursor = url.searchParams.get("starting_after") ?? "";
    if (cursor.startsWith("usr_")) after = Number.parseInt(cursor.slice("usr_".length), 10) || 0;
    const rows = new Array(limit);
    for (let offset = 0; offset < limit; offset++) {
      rows[offset] = users[(after + 1 + offset) % datasetSize];
    }
    return respond(200, `{"data":[${rows.join(",")}]}`);
  }

  if (remainder.startsWith("/")) {
    const userId = remainder.slice(1);
    if (request.method === "GET" && userId.startsWith("usr_")) {
      const index = Number.parseInt(userId.slice("usr_".length), 10);
      if (Number.isInteger(index) && index >= 0 && index < datasetSize && userId === `usr_${index}`) {
        return respond(200, users[index]);
      }
    }
    if (request.method === "PATCH" && userId === "usr_0") {
      let patch;
      try {
        patch = JSON.parse(await request.text());
      } catch {
        return respond(400, `{"error":{"code":"invalid_json","message":"the request body is not valid JSON"}}`);
      }
      const name = typeof patch?.name === "string" ? patch.name : "Bench User 0";
      const banned = patch?.banned === true;
      return respond(200, `{"id":"usr_0","email":"bench-0@example.test","email_verified":true,"username":"bench0","name":${JSON.stringify(name)},"banned":${banned},"ban_reason":null,"created_at":1700000000,"last_sign_in_at":null,"public_metadata":{"index":0},"private_metadata":{"plan":"pro"}}`);
    }
  }

  if (request.method === "POST" && remainder === "") {
    let create;
    try {
      create = JSON.parse(await request.text());
    } catch {
      return respond(400, `{"error":{"code":"invalid_json","message":"the request body is not valid JSON"}}`);
    }
    // The stub answers 200; the official client accepts any 2xx for create.
    return respond(200, `{"id":"usr_created","email":${JSON.stringify(create?.email ?? "")},"email_verified":false,"username":null,"name":null,"banned":false,"ban_reason":null,"created_at":1700000001,"last_sign_in_at":null,"public_metadata":{},"private_metadata":{}}`);
  }

  return respond(404, `{"error":{"code":"user_not_found","message":"no user with this id in this instance"}}`);
}

// Every SDK call returns a TerraScaleResult value; a false `ok` fails the run
// with the error details instead of throwing inside the client.
function failLane(lane, context, result) {
  const error = result?.error;
  return new Error(
    `${lane} ${context}: ${error?.kind ?? "unknown"} ${error?.code ?? ""} ${error?.message ?? JSON.stringify(result)}`.trim(),
  );
}

async function run({ samples, operations, warmup, seed, output }) {
  mkdirSync(output, { recursive: true });
  const client = createTsAuthClient({
    apiBaseUrl: "http://127.0.0.1/",
    accessToken,
    instanceSlug,
    fetch: stubFetch,
    routing: { enabled: false },
  });
  const usersClient = client.users;

  const lanes = [
    {
      name: "read",
      op: async (seq) => {
        const result = await usersClient.get(`usr_${seq % datasetSize}`);
        if (!result.ok) throw failLane("read", `get ${seq % datasetSize}`, result);
        const user = result.value;
        if (!user.id.startsWith("usr_") || !user.email_verified || Object.keys(user.public_metadata).length === 0) {
          throw new Error(`read: invalid user read: ${user.id}`);
        }
      },
    },
    {
      name: "query",
      op: async (seq) => {
        const cursor = `usr_${seq % datasetSize}`;
        const result = await usersClient.list({ limit: 50, startingAfter: cursor });
        if (!result.ok) throw failLane("query", `list ${cursor}`, result);
        const page = result.value;
        const expected = `usr_${(seq % datasetSize + 1) % datasetSize}`;
        if (page.data.length !== 50 || page.data[0].id !== expected) {
          throw new Error(`query: invalid page: ${page.data.length} rows starting at ${page.data[0].id}`);
        }
      },
    },
    {
      name: "mixed",
      op: async (seq) => {
        if (seq % 2 === 0) {
          const result = await usersClient.get(`usr_${seq % datasetSize}`);
          if (!result.ok) throw failLane("mixed", `get ${seq % datasetSize}`, result);
          if (!result.value.id.startsWith("usr_")) {
            throw new Error(`mixed: invalid mixed read: ${result.value.id}`);
          }
          return;
        }
        const result = await usersClient.create({ email: `bench-mixed-${seq}@example.test` });
        if (!result.ok) throw failLane("mixed", `create ${seq}`, result);
        if (result.value.id === "") {
          throw new Error(`mixed: invalid mixed write for ${seq}`);
        }
      },
    },
    {
      name: "write",
      op: async (seq) => {
        if (seq % 2 === 0) {
          const email = `bench-write-${seq}@example.test`;
          const result = await usersClient.create({ email, name: `Write User ${seq}` });
          if (!result.ok) throw failLane("write", `create ${seq}`, result);
          if (result.value.email !== email) {
            throw new Error(`write: invalid create for ${seq}`);
          }
          return;
        }
        const result = await usersClient.update(
          "usr_0",
          new TsAuthUserPatch().withName(`Renamed ${seq}`).withBanned(seq % 4 === 1),
        );
        if (!result.ok) throw failLane("write", `patch ${seq}`, result);
        if (result.value.name !== `Renamed ${seq}`) {
          throw new Error(`write: invalid patch for ${seq}`);
        }
      },
    },
  ];
  const concurrency = [1, 16];

  const results = [];
  for (const lane of lanes) {
    for (const workers of concurrency) {
      if (lane.name === "query" && workers > 1) {
        continue; // The .NET lane matrix: query only at c1.
      }
      for (let index = 0; index < warmup; index++) {
        try {
          await lane.op(index);
        } catch (error) {
          throw new Error(`${lane.name} warmup: ${error.message}`);
        }
      }
      for (let sample = 0; sample < samples; sample++) {
        const measured = await measureSample(lane.name, lane.op, workers, operations);
        results.push({ lane: lane.name, concurrency: workers, ...measured });
        console.log(
          `  ts ${lane.name.padEnd(6)} c${String(workers).padEnd(2)} sample ${sample + 1}/${samples}: ${measured.throughputOpsPerSecond.toFixed(1)} ops/s`,
        );
      }
    }
  }

  const receipt = {
    subject: "ts-typescript (official TS SDK, TsAuthClient)",
    sdkLanguage: "typescript",
    nodeVersion: process.version,
    samples,
    operations,
    warmup,
    seed,
    datasetSize,
    payloadSize,
    instanceSlug,
    cases: results,
    finishedUTC: new Date().toISOString(),
  };
  const receiptPath = join(output, "typescript-receipt.json");
  writeFileSync(receiptPath, `${JSON.stringify(receipt, null, 2)}\n`);

  let summary = "# ts-auth TypeScript SDK client benchmark receipt\n\n";
  summary += "| Lane | Concurrency | ops/s | p50 µs | p95 µs | p99 µs | B/op |\n| --- | --- | ---: | ---: | ---: | ---: | ---: |\n";
  for (const result of results) {
    summary += `| ${result.lane} | c${result.concurrency} | ${result.throughputOpsPerSecond.toFixed(0)} | ${result.p50Microseconds.toFixed(1)} | ${result.p95Microseconds.toFixed(1)} | ${result.p99Microseconds.toFixed(1)} | ${result.allocatedBytesPerOperation.toFixed(0)} |\n`;
  }
  writeFileSync(join(output, "typescript-summary.md"), summary);
  console.log(`Receipt: ${receiptPath}`);
}

async function measureSample(lane, op, workers, operations) {
  const latencies = [];
  let seq = 0;
  const perWorker = Math.floor(operations / workers);

  // Single-threaded JS: a plain shared counter captured before each await
  // distributes the sequence space exactly like bench.go's atomic counter.
  // allocatedBytesPerOperation is an approximate process.memoryUsage().heapUsed
  // delta over the sample divided by operations (V8 exposes no TotalAlloc).
  const heapBefore = process.memoryUsage().heapUsed;
  const start = performance.now();
  await Promise.all(
    Array.from({ length: workers }, async () => {
      const local = [];
      for (let index = 0; index < perWorker; index++) {
        const current = ++seq;
        const operationStart = performance.now();
        try {
          await op(current);
        } catch (error) {
          console.error(lane, "operation failed:", error.message);
          process.exit(1);
        }
        local.push(performance.now() - operationStart);
      }
      latencies.push(...local);
    }),
  );
  const elapsedSeconds = (performance.now() - start) / 1000;
  const heapDelta = process.memoryUsage().heapUsed - heapBefore;

  latencies.sort((left, right) => left - right);
  const pct = (fraction) => {
    let index = Math.floor(fraction * latencies.length);
    if (index >= latencies.length) index = latencies.length - 1;
    return latencies[index] * 1000;
  };
  return {
    throughputOpsPerSecond: operations / elapsedSeconds,
    p50Microseconds: pct(0.5),
    p95Microseconds: pct(0.95),
    p99Microseconds: pct(0.99),
    allocatedBytesPerOperation: heapDelta / operations,
  };
}

try {
  await run(parseArgs(process.argv.slice(2)));
} catch (error) {
  console.error("bench failed:", error.message);
  process.exit(1);
}
