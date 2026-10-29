import type { TerraBaseV1Client, V1ImportJob, V1ImportOptions } from "@terrascale/sdk";

export const mapping: V1ImportOptions = { exclude: ["main.ignored"], collection: "q11_rows" };
export const defaults: V1ImportOptions = { exclude: null, collection: null };
// @ts-expect-error producer removed missingPrimaryKey; import ids are generated deterministically
export const legacy: V1ImportOptions = { missingPrimaryKey: "generate" };
// @ts-expect-error collection is a nullable string, not a route or an object
export const invalidCollection: V1ImportOptions = { collection: { name: "q11_rows" } };

export function consumer(client: TerraBaseV1Client, job: V1ImportJob) {
  const imports = client.database("q11_import").imports;
  const source = { sourceBytes: 10, sourceSha256: "a".repeat(64) };
  void imports.create({ ...source, format: "ndjson", options: mapping });
  void imports.create({ ...source, format: "ndjson" });
  void imports.create({ ...source, format: "sqlite", options: null });
  void imports.create({ ...source, format: "postgres_plain", options: defaults });
  void imports.update({ options: { collection: "q11_rows" } });
  // @ts-expect-error only the three producer formats are supported
  void imports.create({ ...source, format: "csv" });
  // @ts-expect-error old mapping policy is not a current import option
  void imports.update({ options: { missingPrimaryKey: "reject" } });
  // @ts-expect-error response removed the legacy mapping policy too
  job.options.missingPrimaryKey;
  // @ts-expect-error table ids no longer expose an idPolicy field
  job.preview?.tables[0]?.idPolicy;
  const collection: string | null = job.options.collection;
  const exclude: readonly string[] | null = job.options.exclude;
  return { collection, exclude };
}
