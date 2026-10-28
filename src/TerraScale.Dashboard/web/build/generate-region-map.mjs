// Generates the dotted world maps used as the dashboard's region-map visual and as the
// auth pages' animated hero (Features/Auth/AuthRegionMap.cshtml).
//
//   npm run build:region-map   (from src/TerraScale.Dashboard/web)
//
// Run by hand when the map artwork changes; the outputs, web/static/brand/region-map.svg and
// web/static/brand/world-map-dots.svg, are committed and the project build never runs this.
// Each SVG records its projection, grid and region pins; region selection stays on labeled
// form controls. The printed pins are the hero's marker coordinates.
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

// The auth hero masks this map with a theme color, so every dot is one round-capped
// zero-length stroke in currentColor. Pins are dotted-map's own grid points for each region.
const hero = new DottedMap({ height: 60, grid: "diagonal" });
const regions = { GRU: { lat: -23.43, lng: -46.47 }, FRA: { lat: 50.03, lng: 8.57 }, SJC: { lat: 37.36, lng: -121.93 } };
const pins = Object.entries(regions).map(([code, at]) => ({ code, ...hero.getPin(at) }));
const heroViewBox = generatedViewBox(hero);
const dots = hero.getPoints().map((point) => `M${round(point.x)} ${round(point.y)}h0`).join("");
const heroSvg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="${heroViewBox}" fill="none" data-projection="web-mercator" data-grid="diagonal" data-pins="${pins.map((pin) => `${pin.code} ${round(pin.x)} ${round(pin.y)}`).join(";")}"><path d="${dots}" stroke="currentColor" stroke-width="0.44" stroke-linecap="round"/></svg>\n`;
writeFileSync(join(BRAND, "world-map-dots.svg"), heroSvg);
console.log(`src/TerraScale.Dashboard/web/static/brand/world-map-dots.svg (auth hero map, viewBox ${heroViewBox})`);
for (const pin of pins) console.log(`  ${pin.code} ${round(pin.x)} ${round(pin.y)}`);

function generatedViewBox(dottedMap) {
  const viewBox = dottedMap.getSVG({ radius: 0.22 }).match(/viewBox="([^"]+)"/)?.[1];
  if (!viewBox) throw new Error("dotted-map did not report a viewBox");
  return viewBox;
}

function round(value) {
  return Math.round(value * 100) / 100;
}
