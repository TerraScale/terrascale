/* Existing Europe/Asia Pacific Durable Objects keep their original placement. */

import { DurableObject } from "cloudflare:workers";
import type { Env } from "../env";
import { colo, probe } from "./probe";

export class VantagePoint extends DurableObject<Env> {
  /** An object never moves while it lives, so one lookup is enough. */
  private colo: Promise<string | null> | undefined;

  async fetch(request: Request): Promise<Response> {
    return probe(request, this.env.VERSION, async () => {
      this.colo ??= colo();
      const where = await this.colo;
      if (where === null) this.colo = undefined;
      return where;
    });
  }
}
