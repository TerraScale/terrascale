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
const number = value => {
  const rounded = round(value);
  return String(Number.isInteger(rounded) ? rounded : +rounded.toFixed(2));
};
const dotRows = points
  .slice()
  .sort((a, b) => a.y - b.y || a.x - b.x)
  .reduce((acc, point) => {
    let row = acc[acc.length - 1];
    if (!row || row.y !== point.y) acc.push((row = { y: point.y, xs: [] }));
    row.xs.push(point.x);
    return acc;
  }, []);
const rowRuns = dotRows.map(row => {
  const runs = [];
  let start = row.xs[0];
  let previous = start;
  for (const x of row.xs.slice(1)) {
    if (x - previous === 1) previous = x;
    else {
      runs.push([start, previous]);
      start = previous = x;
    }
  }
  runs.push([start, previous]);
  return { y: row.y, runs };
});
const longest = Math.max(...rowRuns.flatMap(row => row.runs.map(([a, b]) => b - a + 1)));
const powerTemplate = power =>
  power === 1
    ? `<g id="w1"><circle r=".18"/></g>`
    : `<g id="w${power}"><use href="#w${power / 2}"/><use href="#w${power / 2}" x="${power / 2}"/></g>`;
const powers = [];
for (let power = 1; power <= longest; power *= 2) powers.push(power);
const runUses = rowRuns.map(
  row =>
    `    <g transform="translate(0 ${row.y})">${row.runs
      .map(([a, b]) => {
        const marks = [];
        let length = b - a + 1;
        let offset = 0;
        for (const power of [...powers].reverse()) {
          if (length < power) continue;
          marks.push(`<use href="#w${power}"${offset ? ` x="${number(a + offset)}"` : ` x="${number(a)}"`}/>`);
          offset += power;
          length -= power;
        }
        return marks.join("");
      })
      .join("")}</g>`,
);

const svg = [
  `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 136 80" id="terrascale-region-map" role="img" aria-labelledby="terrascale-region-map-title terrascale-region-map-description" data-projection="vanDerGrinten" data-grid="diagonal">`,
  `  <title id="terrascale-region-map-title">TerraScale region map</title>`,
  `  <desc id="terrascale-region-map-description">A dotted world map with every TerraScale region.</desc>`,
  `  <defs>`,
  ...powers.map(power => `    ${powerTemplate(power)}`),
  `  </defs>`,
  `  <g fill="rgba(37, 99, 235, 0.52)">`,
  ...runUses,
  `  </g>`,
  `  <g id="terrascale-region-map-regions">`,
  ...pins.map(pin => `    <circle data-region="${pin.code}" data-name="${pin.name}" data-lat="${pin.lat}" data-lng="${pin.lng}" cx="${pin.x}" cy="${pin.y}" r="0.5" fill="none"><title>${pin.name} (${pin.code})</title></circle>`),
  `  </g>`,
  `</svg>`,
  "",
].join("\n");

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
console.log("src/TerraScale.Dashboard/web/assets/js/region-map-data.js (dot grid and region coordinates)");
