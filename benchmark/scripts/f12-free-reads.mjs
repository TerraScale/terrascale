import { runFreeReads } from "./free-reads.mjs";

await runFreeReads({ readerCounts: [1, 1000], regions: ["gru"], holdMs: 5000, flightMetrics: true });
