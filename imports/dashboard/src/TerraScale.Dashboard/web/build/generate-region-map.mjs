// Generates the dotted world map used as the dashboard's region-map visual.
//
//   npm run build:region-map   (from src/TerraScale.Dashboard/web)
//
// Run by hand when the map artwork changes; the output, web/static/brand/region-map.svg,
// is committed and the project build never runs this. The SVG records its projection,
// grid, title and description; region selection stays on labeled form controls.
import DottedMapModule from "dotted-map";
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const DottedMap = DottedMapModule.default ?? DottedMapModule;
const WEB = join(dirname(fileURLToPath(import.meta.url)), "..");
const BRAND = join(WEB, "static", "brand");

const map = new DottedMap({
  height: 80,
  width: 136,
  grid: "diagonal",
  projection: { name: "vanDerGrinten" },
});
const generated = map.getSVG({
  radius: 0.18,
  color: "rgba(37, 99, 235, 0.52)",
  backgroundColor: "transparent",
});

const openingTag = generated.match(/<svg\b[^>]*>/)?.[0];
if (!openingTag) throw new Error("dotted-map did not generate an SVG root element");

const accessibleTag = openingTag.replace(
  />$/,
  ' id="terrascale-region-map" role="img" aria-labelledby="terrascale-region-map-title terrascale-region-map-description" data-projection="vanDerGrinten" data-grid="diagonal">',
);
const svg = generated.replace(
  openingTag,
  `${accessibleTag}\n  <title id="terrascale-region-map-title">TerraScale region map</title>\n  <desc id="terrascale-region-map-description">A dotted world map used as a visual background for region selection.</desc>`,
);

mkdirSync(BRAND, { recursive: true });
writeFileSync(join(BRAND, "region-map.svg"), svg);
console.log("src/TerraScale.Dashboard/web/static/brand/region-map.svg (dotted world map)");
