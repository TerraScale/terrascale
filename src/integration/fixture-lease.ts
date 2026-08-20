import { createHash, randomUUID } from "node:crypto";
import { open, unlink } from "node:fs/promises";
import { hostname } from "node:os";
import path from "node:path";

export type FixtureLeaseScope = {
  readonly tenantId: string;
  readonly projectId: string;
  readonly databaseId: string;
  readonly repositoryId: string;
  readonly documentId: string;
  readonly apiBaseUrl: string;
  readonly authBaseUrl: string;
  readonly credentialFingerprint: string;
};

/**
 * Acquires a process-exclusive fixture lock. The lock is fail-closed: an
 * existing path is never treated as an expired lease and is never removed by
 * a process that did not create it.
 */
export async function withFixtureLease<TValue>(
  leasePath: string,
  scope: FixtureLeaseScope,
  work: () => Promise<TValue>,
): Promise<TValue> {
  const lockPath = validateLeasePath(leasePath);
  let handle;

  try {
    handle = await open(lockPath, "wx");
  } catch (error) {
    if (isFileExistsError(error)) {
      throw new Error(`Fixture lease is already held: ${lockPath}`);
    }
    throw error;
  }

  const owner = `${hostname()}:${process.pid}:${randomUUID()}`;
  const metadata = {
    version: 1,
    owner,
    acquiredAt: new Date().toISOString(),
    expiresAt: new Date(Date.now() + 30 * 60 * 1000).toISOString(),
    scope,
  };

  try {
    await handle.writeFile(JSON.stringify(metadata));
    await handle.sync();
    return await work();
  } finally {
    await handle.close();
    await unlink(lockPath).catch((error: unknown) => {
      if (!isFileMissingError(error)) {
        throw error;
      }
    });
  }
}

export function fixtureCredentialFingerprint(accessToken: string): string {
  return createHash("sha256").update(accessToken).digest("hex");
}

function validateLeasePath(leasePath: string): string {
  const trimmed = leasePath.trim();
  if (trimmed.length === 0 || !path.isAbsolute(trimmed) || path.dirname(trimmed) === trimmed) {
    throw new TypeError("Fixture lease path must be an absolute file path.");
  }

  return path.normalize(trimmed);
}

function isFileExistsError(error: unknown): error is NodeJS.ErrnoException {
  return error instanceof Error && "code" in error && error.code === "EEXIST";
}

function isFileMissingError(error: unknown): error is NodeJS.ErrnoException {
  return error instanceof Error && "code" in error && error.code === "ENOENT";
}
