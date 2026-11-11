import DottedMapModule from "dotted-map";
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const DottedMap = DottedMapModule.default ?? DottedMapModule;
const WEB = join(dirname(fileURLToPath(import.meta.url)), "..");
const BRAND = join(WEB, "static", "brand");
const SCRIPTS = join(WEB, "assets", "js");
const WIDTH = 136;
const HEIGHT = 80;
const ROW = Math.sqrt(3) / 2;

const REGIONS = [
  { code: "gru", name: "São Paulo", lat: -23.55, lng: -46.63 },
  { code: "fra", name: "Frankfurt", lat: 50.11, lng: 8.68 },
  { code: "sjc", name: "San Francisco Bay Area", lat: 37.34, lng: -121.89 },
  { code: "sfo", name: "San Francisco", lat: 37.77, lng: -122.42 },
];

const map = new DottedMap({
  height: HEIGHT,
  width: WIDTH,
  grid: "diagonal",
  projection: { name: "vanDerGrinten" },
});
const points = map.getPoints();
const rows = Math.round(Math.max(...points.map(point => point.y)) / ROW) + 1;
const land = new Uint8Array(Math.ceil((rows * WIDTH) / 8));
for (const point of points) {
  const index = Math.round(point.y / ROW) * WIDTH + Math.floor(point.x);
  land[index >> 3] |= 1 << (index & 7);
}
const round = value => Math.round(value * 100) / 100;
const pins = REGIONS.map(region => {
  const pin = map.getPin({ lat: region.lat, lng: region.lng });
  return { ...region, x: round(pin.x), y: round(pin.y) };
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
const regionMarks = pins
  .map(pin => `    <circle data-region="${pin.code}" data-name="${pin.name}" data-lat="${pin.lat}" data-lng="${pin.lng}" cx="${pin.x}" cy="${pin.y}" r="0.5" fill="none"><title>${pin.name} (${pin.code})</title></circle>`)
  .join("\n");
const svg = generated
  .replace(
    openingTag,
    `${accessibleTag}\n  <title id="terrascale-region-map-title">TerraScale region map</title>\n  <desc id="terrascale-region-map-description">A dotted world map with every TerraScale region.</desc>`,
  )
  .replace(/\s*<\/svg>\s*$/, `\n  <g id="terrascale-region-map-regions">\n${regionMarks}\n  </g>\n</svg>\n`);

const data = [
  `export const columns = ${WIDTH};`,
  `export const rows = ${rows};`,
  `export const rowHeight = ${ROW};`,
  `export const land = ${JSON.stringify(Buffer.from(land).toString("base64"))};`,
  `export const regions = ${JSON.stringify(Object.fromEntries(pins.map(pin => [pin.code, { x: pin.x, y: pin.y }])))};`,
  "",
].join("\n");

mkdirSync(BRAND, { recursive: true });
writeFileSync(join(BRAND, "region-map.svg"), svg);
writeFileSync(join(SCRIPTS, "region-map-data.js"), data);
console.log("src/TerraScale.Dashboard/web/static/brand/region-map.svg (dotted world map)");
console.log("src/TerraScale.Dashboard/web/assets/js/region-map-data.js (dot lattice and region coordinates)");
