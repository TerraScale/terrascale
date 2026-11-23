/* Private service binding only; fetch handlers honor explicit placement. */

import type { Env } from "../env";
import { probe } from "./probe";

export default {
  fetch: (request: Request, env: Pick<Env, "VERSION">) => probe(request, env.VERSION),
} satisfies ExportedHandler<Pick<Env, "VERSION">>;
