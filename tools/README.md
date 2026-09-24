# Tools

Offline asset generation. Nothing here runs in CI — generated files are
committed.

## Dotted region map

`generate-region-map.mjs` regenerates
`web/static/brand/region-map.svg`, a decorative dotted world map produced with
`dotted-map`. The SVG records its projection, grid, view box, title, and
description so a route that renders it can identify the visual layer. Region
selection remains available through ordinary labeled form controls and does
not depend on the map.

Rerun it only when the map artwork changes:

```sh
cd tools && npm ci && node generate-region-map.mjs
```
