import { once } from "node:events";

async function main() {
  const input = process.argv.slice(2);
  const dryRun = input.includes("--dry-run");
  const args = input.filter(arg => arg !== "--dry-run");
  const [projectId, databaseId, ...groups] = args;
  if (!projectId || !databaseId || !groups.length || args.some(arg => arg.startsWith("--")) ||
      groups.some(group => group.includes("/")) || new Set(groups).size !== groups.length) {
    throw new Error("InvalidArguments");
  }
  if (dryRun) {
    console.error(`Dry run: ${groups.length} distinct collection groups; no credentials loaded, no reads, no output data.`);
    return;
  }
  const { initializeApp, applicationDefault, deleteApp } = await import("firebase-admin/app");
  const { getFirestore, Timestamp, GeoPoint, DocumentReference } = await import("firebase-admin/firestore");
  const app = initializeApp({ projectId, credential: applicationDefault() });
  const db = getFirestore(app, databaseId);
  function encode(value) {
    if (value === null) return { type: "null", value: null };
    if (value instanceof Timestamp) {
      return { type: "timestamp", seconds: String(value.seconds), nanoseconds: value.nanoseconds };
    }
    if (value instanceof GeoPoint) {
      return { type: "geopoint", latitude: value.latitude, longitude: value.longitude };
    }
    if (value instanceof DocumentReference) {
      return { type: "reference", project: value.firestore.projectId, database: value.firestore.databaseId, path: value.path };
    }
    if (Buffer.isBuffer(value) || value instanceof Uint8Array) {
      return { type: "bytes", base64: Buffer.from(value).toString("base64") };
    }
    if (Array.isArray(value)) return { type: "array", value: value.map(encode) };
    if (typeof value === "string" || typeof value === "boolean") {
      return { type: typeof value, value };
    }
    if (typeof value === "number") {
      if (Number.isInteger(value) && !Number.isSafeInteger(value)) throw new Error("UnsafeInteger");
      return { type: "number", value: Object.is(value, -0) ? "-0" : String(value) };
    }
    if (typeof value === "object" &&
        (Object.getPrototypeOf(value) === Object.prototype || Object.getPrototypeOf(value) === null)) {
      return { type: "map", value: Object.fromEntries(Object.entries(value).map(([key, item]) => [key, encode(item)])) };
    }
    throw new Error("UnsupportedValue");
  }
  try {
    for (const group of groups) {
      let count = 0;
      for await (const doc of db.collectionGroup(group).stream()) {
        const line = JSON.stringify({
          path: doc.ref.path,
          collection: doc.ref.parent.path,
          parent: doc.ref.parent.parent?.path ?? null,
          id: doc.id,
          fields: encode(doc.data()),
        }) + "\n";
        if (!process.stdout.write(line)) await once(process.stdout, "drain");
        count++;
      }
      console.error(`Collection group complete: ${count} documents.`);
    }
  } finally {
    await db.terminate();
    await deleteApp(app);
  }
}

try {
  await main();
} catch {
  console.error("Export failed. Check arguments, IAM, SDK support and source types; discard partial JSONL.");
  process.exitCode = 1;
}
