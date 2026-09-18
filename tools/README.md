# Tools

Offline code generation. Nothing here runs in CI — the generated files are
committed.

## Region map

`generate-region-map.mjs` regenerates `wwwroot/brand/region-map.svg` (dotted
world map) and `Framework/RegionMap.cs` (projected pin coordinates for every
OCI region we may support, keyed by 3-letter code). Rerun it only when the
region list changes:

```sh
cd tools && npm ci && node generate-region-map.mjs
```

Pages highlight regions with `@RegionMap.Highlight(new[] { "SFO", "GRU", "FRA" })`;
each pin group carries `id="ts-region-<CODE>"` for CSS/JS targeting.
