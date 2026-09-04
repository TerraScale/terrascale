import { createTerraScaleClient } from "@terrascale/sdk";

/**
 * Optional cloud seed for `terrascale dev` once the CLI seed route is enabled.
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
  const result = await client.documents.put(databaseId, "posts", "starter-welcome", {
    title: "Hello from TerraScale",
    authorId: "starter",
    publishedAt: new Date().toISOString(),
  }, { durability: "regional" });
  if (!result.ok) throw new Error(result.error.message);
}
