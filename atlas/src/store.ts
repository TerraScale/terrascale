import { DurableObject } from "cloudflare:workers";
import type { Placement, PublishRequest, Topology } from "./contract";
import type { Env } from "./env";
import { cacheRecord, placementKey } from "./read";

type WriteResult = "updated" | "stale" | "skipped";

export class AtlasStore extends DurableObject<Env> {
  private pending: Promise<void> = Promise.resolve();

  async fetch(request: Request): Promise<Response> {
    const previous = this.pending;
    let release!: () => void;
    this.pending = new Promise<void>((resolve) => {
      release = resolve;
    });
    await previous;
    try {
      const body = await request.json<PublishRequest>();
      const topology = body.topology === undefined ? undefined : await this.writeTopology(body.topology);
      const placements: { project: string; database: string; result: "updated" | "stale" }[] = [];
      for (let start = 0; start < (body.placements?.length ?? 0); start += 8) {
        const outcomes = await Promise.allSettled(
          body.placements!.slice(start, start + 8).map(async (placement) => ({
            project: placement.project,
            database: placement.database,
            result: await this.writePlacement(placement),
          })),
        );
        for (const outcome of outcomes) {
          if (outcome.status === "rejected") throw outcome.reason;
          placements.push(outcome.value);
        }
      }
      return Response.json({ ...(topology === undefined ? {} : { topology }), placements });
    } catch (error) {
      console.error("atlas D1 write failed", error);
      return Response.json(
        {
          error: {
            code: "store_unavailable",
            message: "D1 refused the write",
            fix: "Check D1 and publish again; earlier items may have landed.",
          },
        },
        { status: 502 },
      );
    } finally {
      release();
    }
  }

  private async writeTopology(topology: Topology): Promise<WriteResult> {
    const doc = JSON.stringify(topology);
    const stored = await this.env.ATLAS_DB.prepare("SELECT updated, doc FROM topology WHERE id = 1").first<{
      updated: number;
      doc: string;
    }>();
    if (stored !== null && stored.updated >= topology.updated) {
      if (stored.updated === topology.updated && stored.doc === doc) {
        await this.refreshCache("topology", topology);
        return "skipped";
      }
      return "stale";
    }
    const result = await this.env.ATLAS_DB.prepare(
      "INSERT INTO topology (id, updated, doc) VALUES (1, ?, ?) ON CONFLICT(id) DO UPDATE SET updated = excluded.updated, doc = excluded.doc",
    )
      .bind(topology.updated, doc)
      .run();
    if (!result.success) throw new Error("D1 topology write failed");
    await this.refreshCache("topology", topology);
    return "updated";
  }

  private async writePlacement(placement: Placement): Promise<"updated" | "stale"> {
    const { project, database, region, updated } = placement;
    const stored = await this.env.ATLAS_DB.prepare("SELECT updated FROM placements WHERE project = ? AND database = ?")
      .bind(project, database)
      .first<{ updated: number }>();
    if (stored !== null && stored.updated >= updated) return "stale";
    const result = await this.env.ATLAS_DB.prepare(
      "INSERT INTO placements (project, database, region, updated) VALUES (?, ?, ?, ?) ON CONFLICT(project, database) DO UPDATE SET region = excluded.region, updated = excluded.updated",
    )
      .bind(project, database, region, updated)
      .run();
    if (!result.success) throw new Error("D1 placement write failed");
    await this.refreshCache(placementKey(project, database), placement);
    return "updated";
  }

  private async refreshCache(key: string, doc: Topology | Placement): Promise<void> {
    try {
      await this.env.ATLAS_KV.put(key, JSON.stringify(cacheRecord(doc)));
    } catch (error) {
      console.error("atlas KV refresh failed after D1 accepted the write", error);
    }
  }
}
