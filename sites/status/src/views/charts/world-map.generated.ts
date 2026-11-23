export const WORLD_MAP = { width: 119, height: 60 } as const;

const A = 6378137;
const E = 0.0818191908426215;
const RAD = Math.PI / 180;
const X_MIN = -18701674.453269962;
const X_RANGE = 37403348.906539924;
const Y_MAX = 11361819.887676764;
const Y_RANGE = 18884783.128941886;

export type MapPoint = { x: number; y: number };

export function project(lat: number, lng: number): MapPoint {
  const s = E * Math.sin(lat * RAD);
  const y = A * Math.log(Math.tan(Math.PI / 4 + (lat * RAD) / 2) * ((1 - s) / (1 + s)) ** (E / 2));
  return {
    x: (WORLD_MAP.width * (A * lng * RAD - X_MIN)) / X_RANGE,
    y: (WORLD_MAP.height * (Y_MAX - y)) / Y_RANGE,
  };
}

export function unproject(x: number, y: number): { lat: number; lng: number } {
  const lng = ((x / WORLD_MAP.width) * X_RANGE + X_MIN) / (A * RAD);
  const t = Math.exp(-(((y / WORLD_MAP.height) * Y_RANGE - Y_MAX) / A));
  let lat = 2 * Math.atan(t) - Math.PI / 2;
  for (let i = 0; i < 6; i++) {
    const s = E * Math.sin(lat);
    lat = 2 * Math.atan(t * ((1 + s) / (1 - s)) ** (E / 2)) - Math.PI / 2;
  }
  return { lat: lat / RAD, lng };
}
