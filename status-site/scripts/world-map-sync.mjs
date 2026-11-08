#!/usr/bin/env node
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import DottedMap from "dotted-map";

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, "..");
const TOKEN = "#4797e4";
const FILL_OPACITY = "0.4";
const SENTINEL = "#FF00AA";

const map = new DottedMap({ height: 60, grid: "diagonal" });
const { width, height, X_MIN, X_RANGE, Y_MAX, Y_RANGE } = map;

const svg = map
  .getSVG({ radius: 0.22, color: TOKEN, shape: "circle", backgroundColor: "transparent" })
  .replace(/<circle /g, `<circle fill-opacity="${FILL_OPACITY}" `);

const circles = svg.match(/<circle [^>]*\/>/g) ?? [];
const malformed = circles.filter((tag) => (tag.match(/fill=/g) ?? []).length !== 1);
if (circles.length === 0 || malformed.length > 0) throw new Error("Generated SVG has malformed circle tags");

const A = 6378137;
const E = 0.0818191908426215;
const rad = Math.PI / 180;

const mercatorY = (lat) => {
  const s = E * Math.sin(lat * rad);
  return A * Math.log(Math.tan(Math.PI / 4 + (lat * rad) / 2) * ((1 - s) / (1 + s)) ** (E / 2));
};

const project = (lat, lng) => ({
  x: (width * (A * lng * rad - X_MIN)) / X_RANGE,
  y: (height * (Y_MAX - mercatorY(lat))) / Y_RANGE,
});

const unproject = (x, y) => {
  const lng = ((x / width) * X_RANGE + X_MIN) / (A * rad);
  const t = Math.exp(-(((y / height) * Y_RANGE - Y_MAX) / A));
  let lat = 2 * Math.atan(t) - Math.PI / 2;
  for (let i = 0; i < 6; i++) {
    const s = E * Math.sin(lat);
    lat = 2 * Math.atan(t * ((1 + s) / (1 - s)) ** (E / 2)) - Math.PI / 2;
  }
  return { lat: lat / rad, lng };
};

const probes = [
  [52.37, 4.9],
  [-23.55, -46.63],
  [37.34, -121.89],
  [35.68, 139.69],
  [-33.87, 151.21],
  [64.15, -21.94],
  [-54.8, -68.3],
  [0.5, 32.6],
  [69.65, 18.96],
  [55.75, 37.62],
];
const snap = (point) => {
  const row = Math.round(point.y / (Math.sqrt(3) / 2));
  const shifted = row % 2 === 0 ? point.x - 0.5 : point.x;
  return { x: row % 2 === 0 ? Math.round(shifted) + 0.5 : Math.round(shifted), y: row * (Math.sqrt(3) / 2) };
};

for (const [lat, lng] of probes) {
  const probe = new DottedMap({ height: 60, grid: "diagonal" });
  probe.addPin({ lat, lng, svgOptions: { color: SENTINEL, radius: 0.8 } });
  const pin = probe
    .getSVG({ radius: 0.22, color: TOKEN, shape: "circle", backgroundColor: "transparent" })
    .match(new RegExp(`<circle[^>]*fill="${SENTINEL}"[^>]*/>`));
  if (!pin) throw new Error(`Library rendered no pin for ${lat},${lng}`);
  const cx = Number(pin[0].match(/cx="([^"]+)"/)[1]);
  const cy = Number(pin[0].match(/cy="([^"]+)"/)[1]);
  const raw = project(lat, lng);
  const mine = snap(raw);
  if (Math.abs(mine.x - cx) > 1e-9 || Math.abs(mine.y - cy) > 1e-9) {
    throw new Error(`Projection drift at ${lat},${lng}: library (${cx},${cy}) vs formula (${mine.x},${mine.y})`);
  }
  const back = unproject(raw.x, raw.y);
  if (Math.abs(back.lat - lat) > 1e-6 || Math.abs(back.lng - lng) > 1e-6) {
    throw new Error(`Round-trip drift at ${lat},${lng}: ${JSON.stringify(back)}`);
  }
}

fs.writeFileSync(path.join(root, "src/assets/region-map.svg"), svg);

const ts = `export const WORLD_MAP = { width: ${width}, height: ${height} } as const

const A = 6378137
const E = 0.0818191908426215
const RAD = Math.PI / 180
const X_MIN = ${X_MIN}
const X_RANGE = ${X_RANGE}
const Y_MAX = ${Y_MAX}
const Y_RANGE = ${Y_RANGE}

export type MapPoint = { x: number; y: number }

export function project(lat: number, lng: number): MapPoint {
  const s = E * Math.sin(lat * RAD)
  const y = A * Math.log(Math.tan(Math.PI / 4 + (lat * RAD) / 2) * ((1 - s) / (1 + s)) ** (E / 2))
  return {
    x: (WORLD_MAP.width * (A * lng * RAD - X_MIN)) / X_RANGE,
    y: (WORLD_MAP.height * (Y_MAX - y)) / Y_RANGE,
  }
}

export function unproject(x: number, y: number): { lat: number; lng: number } {
  const lng = ((x / WORLD_MAP.width) * X_RANGE + X_MIN) / (A * RAD)
  const t = Math.exp(-(((y / WORLD_MAP.height) * Y_RANGE - Y_MAX) / A))
  let lat = 2 * Math.atan(t) - Math.PI / 2
  for (let i = 0; i < 6; i++) {
    const s = E * Math.sin(lat)
    lat = 2 * Math.atan(t * ((1 + s) / (1 - s)) ** (E / 2)) - Math.PI / 2
  }
  return { lat: lat / RAD, lng }
}
`;
fs.writeFileSync(path.join(root, "src/views/charts/world-map.generated.ts"), ts);

const catalog = JSON.parse(fs.readFileSync(path.join(root, "../landing-site/static/regions.json"), "utf8"));
console.log(
  `Wrote src/assets/region-map.svg (${circles.length} dots, viewBox ${width}x${height}) and src/views/charts/world-map.generated.ts.`,
);
for (const region of catalog.regions) {
  const p = project(region.lat, region.lng);
  console.log(
    `seed ${region.id.toUpperCase()}: map_x ${p.x.toFixed(2)}, map_y ${p.y.toFixed(2)} (from ${region.lat}, ${region.lng})`,
  );
}
