import { mkdir, writeFile } from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { iconManifest } from './icons-manifest.mjs'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const projectRoot = path.resolve(__dirname, '..')
const publicIconsRoot = path.join(projectRoot, 'static', 'icons')
const registryPath = path.join(projectRoot, 'src', 'lib', 'icons.ts')

const normalizeSvg = (svg) =>
  svg
    .replace(/\r\n/g, '\n')
    .replace(/<svg\b([^>]*)>/, (_match, attributes) => {
      const normalizedAttributes = attributes
        .replace(/\swidth="[^"]*"/, '')
        .replace(/\sheight="[^"]*"/, '')

      return `<svg focusable="false"${normalizedAttributes}>`
    })
    .trim()
    .concat('\n')

const syncIcons = async () => {
  const entries = Object.entries(iconManifest).sort(([left], [right]) => left.localeCompare(right))

  for (const [name, { collection, icon }] of entries) {
    const iconDir = path.join(publicIconsRoot, collection)
    const filePath = path.join(iconDir, `${icon}.svg`)
    const sourceUrl = `https://api.iconify.design/${collection}/${icon}.svg`

    await mkdir(iconDir, { recursive: true })

    const response = await fetch(sourceUrl)
    if (!response.ok) {
      throw new Error(`Failed to fetch ${name} from ${sourceUrl}: ${response.status}`)
    }

    const svg = normalizeSvg(await response.text())
    await writeFile(filePath, svg, 'utf8')
  }

  const registrySource = `export const ICONS = {\n${entries
    .map(
      ([name, { collection, icon }]) =>
        `  ${name}: '/icons/${collection}/${icon}.svg',`,
    )
    .join('\n')}\n} as const\n\nexport type AppIconName = keyof typeof ICONS\n\nexport const resolveIconPath = (name: AppIconName) => ICONS[name]\n`

  await writeFile(registryPath, registrySource, 'utf8')
  console.log(`Synced ${entries.length} icons.`)
}

syncIcons().catch((error) => {
  console.error(error)
  process.exitCode = 1
})
