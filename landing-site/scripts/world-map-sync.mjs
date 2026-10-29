#!/usr/bin/env node
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import DottedMap from 'dotted-map'

const here = path.dirname(fileURLToPath(import.meta.url))
const root = path.resolve(here, '..')

// Pins come from the canonical launch-region catalog published at
// https://terrascale.tech/regions.json.
const regions = JSON.parse(fs.readFileSync(path.join(root, 'static/regions.json'), 'utf8')).regions.map(
  ({ id, name, lat, lng }) => ({ id, city: name, lat, lng }),
)

const SENTINEL = '#FF00AA'

const map = new DottedMap({ height: 60, grid: 'diagonal' })
regions.forEach((r) => {
  map.addPin({
    lat: r.lat,
    lng: r.lng,
    svgOptions: { color: SENTINEL, radius: 0.8 },
  })
})

const svg = map.getSVG({
  radius: 0.22,
  color: '#3B82F6',
  shape: 'circle',
  backgroundColor: 'transparent',
})

const viewBoxMatch = svg.match(/viewBox="([^"]+)"/)
if (!viewBoxMatch) throw new Error('Could not read viewBox from generated SVG')
const [minX, minY, width, height] = viewBoxMatch[1].trim().split(/\s+/).map(Number)

const pinRegex = new RegExp(
  `<circle[^>]*fill="${SENTINEL}"[^>]*/>`,
  'g',
)
const pinMatches = [...svg.matchAll(pinRegex)]
if (pinMatches.length !== regions.length) {
  throw new Error(
    `Expected ${regions.length} sentinel pins, found ${pinMatches.length}`,
  )
}

// dotted-map puts a pin on the grid point nearest the city, which can be a sea point next to the
// coast (the Bay Area lands one dot into the Pacific). Snap each pin to the nearest land dot instead,
// preferring the same row, so it sits on the city's dot.
const landDots = map.getPoints().filter((p) => p.svgOptions?.color !== SENTINEL)
const placed = regions.map((r, i) => {
  const pin = pinMatches[i][0]
  const cx = Number(pin.match(/cx="([^"]+)"/)[1])
  const cy = Number(pin.match(/cy="([^"]+)"/)[1])
  const dist = (p) => Math.hypot(p.x - cx, p.y - cy) + Math.abs(p.y - cy) * 1e-6
  const land = landDots.reduce((best, p) => (dist(p) < dist(best) ? p : best))
  if (dist(land) > 1.5) throw new Error(`${r.id}: no land dot within 1.5 grid steps of the pin`)
  return { ...r, x: land.x, y: land.y }
})

const cleanSvg = svg.replace(pinRegex, '')

const mapDest = path.join(root, 'static/world-map.svg')
fs.writeFileSync(mapDest, cleanSvg)

const tsDest = path.join(root, 'src/lib/world-map.generated.ts')
const ts = `export const WORLD_MAP_VIEWBOX = {
  x: ${minX},
  y: ${minY},
  width: ${width},
  height: ${height},
} as const

export type Region = {
  id: string
  city: string
  lat: number
  lng: number
  x: number
  y: number
}

export const REGIONS: readonly Region[] = ${JSON.stringify(placed, null, 2)} as const
`
fs.writeFileSync(tsDest, ts)

console.log(
  `Wrote ${path.relative(root, mapDest)} (${cleanSvg.length} bytes, viewBox ${width}×${height}) ` +
  `and ${path.relative(root, tsDest)} with ${placed.length} regions.`,
)
