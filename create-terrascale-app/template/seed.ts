import { createTerraScaleClient } from "@terrascale/sdk";

/**
 * Optional cloud seed function for an application script runner.
 * `terrascale dev --seed` accepts data files, not TypeScript functions.
 * This always writes through the public API; it never starts a local engine.
 */
export default async function seed(): Promise<void> {
  const databaseId = process.env.TERRASCALE_DATABASE_ID?.trim();
  if (!databaseId) throw new Error("TERRASCALE_DATABASE_ID is required for a cloud seed");
  const client = createTerraScaleClient({
    apiBaseUrl: process.env.TERRASCALE_API_URL,
    accessToken: process.env.TERRASCALE_API_KEY,
    projectId: process.env.TERRASCALE_PROJECT_ID,
  });
  // A fixed ULID keeps re-running the seed on the same document.
  const result = await client.documents.put(databaseId, "posts", "01JB7Q3V0Z8K4M2N6P9R5T1W7A", {
    title: "Hello from TerraScale",
    authorId: "starter",
    publishedAt: new Date().toISOString(),
  }, { durability: "regional" });
  if (!result.ok) throw new Error(result.error.message);
}
