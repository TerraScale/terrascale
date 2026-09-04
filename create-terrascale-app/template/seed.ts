// Seed script for `terrascale dev --seed ./seed.ts`.
// Runs against your personal cloud dev branch (same engine as prod, D-18).
export default async function seed(ts: {
  from(collection: string): { create(doc: Record<string, unknown>): Promise<unknown> };
}) {
  await ts.from("posts").create({
    title: "Hello TerraScale",
    authorId: "mario",
  });
}
