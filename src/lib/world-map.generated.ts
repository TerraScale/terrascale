export const WORLD_MAP_VIEWBOX = {
  x: 0,
  y: 0,
  width: 119,
  height: 60,
} as const

export type Region = {
  id: string
  city: string
  lat: number
  lng: number
  x: number
  y: number
}

export const REGIONS: readonly Region[] = [
  {
    "id": "san-jose",
    "city": "San Jose",
    "lat": 37.34,
    "lng": -121.89,
    "x": 16,
    "y": 21.650635094610966
  },
  {
    "id": "sao-paulo",
    "city": "São Paulo",
    "lat": -23.55,
    "lng": -46.63,
    "x": 42.5,
    "y": 45.033320996790806
  },
  {
    "id": "frankfurt",
    "city": "Frankfurt",
    "lat": 50.11,
    "lng": 8.68,
    "x": 62.5,
    "y": 15.588457268119894
  }
] as const
