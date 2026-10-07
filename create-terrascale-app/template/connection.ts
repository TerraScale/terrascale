/** Public TerraBase connection settings; credentials are supplied separately. */
export type ConnectionSettings = { project: string; database: string; baseUrl: string };

/** TS-589 uses the ts-auth project UUID and database name, never a database ID. */
export function readConnectionSettings(input: {
  project?: string;
  database?: string;
  baseUrl?: string;
}): ConnectionSettings {
  const project = input.project?.trim() ?? "";
  const database = input.database?.trim() ?? "";
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(project)) {
    throw new Error("Set the ts-auth project ID to a lowercase UUID.");
  }
  if (!/^[a-z][a-z0-9_-]{0,62}$/.test(database)) {
    throw new Error("Set the database name to 1–63 lowercase letters, digits, underscores or hyphens, starting with a letter.");
  }
  let endpoint: URL;
  try { endpoint = new URL(input.baseUrl?.trim() ?? ""); }
  catch { throw new Error("Set the TerraBase API URL to an absolute HTTPS origin."); }
  // Loopback HTTP is only for local protocol qualification, not an emulator.
  const loopback = ["localhost", "127.0.0.1", "[::1]"].includes(endpoint.hostname);
  if ((endpoint.protocol !== "https:" && !(endpoint.protocol === "http:" && loopback)) ||
      endpoint.username || endpoint.password || endpoint.pathname !== "/" || endpoint.search || endpoint.hash) {
    throw new Error("Use a TerraBase HTTPS origin without credentials, a path, query or fragment; HTTP is allowed only on loopback.");
  }
  return { project, database, baseUrl: endpoint.origin };
}
