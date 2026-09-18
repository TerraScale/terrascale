import { mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { join } from "node:path";

import { isSyncV1StoredViewState, type SyncV1StoredViewState, type SyncV1ViewStore } from "../sync/v1/store/index.js";

/**
 * A Node file-system view store for server-side and CLI consumers. This
 * module imports node:fs and must stay out of browser bundles; it is exported
 * only from the opt-in `@terrascale/sdk/local` entrypoint.
 */
export class SyncV1NodeFileViewStore implements SyncV1ViewStore {
  readonly #directory: string;

  public constructor(directory: string) {
    this.#directory = directory;
  }

  public async load(view: string): Promise<SyncV1StoredViewState | undefined> {
    try {
      const value: unknown = JSON.parse(await readFile(this.#path(view), "utf8"));
      return isSyncV1StoredViewState(value) ? value : undefined;
    } catch {
      return undefined;
    }
  }

  public async save(view: string, state: SyncV1StoredViewState): Promise<void> {
    await mkdir(this.#directory, { recursive: true });
    // ponytail: plain overwrite without a temp-file rename; a torn write is
    // discarded by the load-time shape check and the bootstrap digest check.
    await writeFile(this.#path(view), `${JSON.stringify(state)}\n`, "utf8");
  }

  public async clear(view: string): Promise<void> {
    await rm(this.#path(view), { force: true });
  }

  #path(view: string): string {
    return join(this.#directory, `${encodeURIComponent(view)}.json`);
  }
}
