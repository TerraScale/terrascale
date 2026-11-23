/* How long probe data is kept. The rollup prunes by these, and reads pick
   the finest data still kept for a window. */

import { DAY } from "../format";

export const SAMPLE_RETENTION_MS = 2 * DAY;
export const HOUR_RETENTION_MS = 90 * DAY;
export const DAY_RETENTION_MS = 400 * DAY;
