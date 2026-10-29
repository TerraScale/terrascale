import { readdir, readFile, stat, writeFile } from 'node:fs/promises'
import path from 'node:path'
import { secureHtml } from './security-headers.mjs'

const buildDir = path.resolve('build')
const stylesheetTagPattern = /<link\b[^>]*rel=["']stylesheet["'][^>]*>/gi
const hrefPattern = /\bhref=["']([^"']+\.css)["']/i

const escapeStyleContent = (css) => css.replaceAll('</style', '<\\/style')

const isLocalStylesheet = (href) =>
  !href.startsWith('http://') && !href.startsWith('https://') && !href.startsWith('//')

const walk = async (directory) => {
  const entries = await readdir(directory, { withFileTypes: true })
  const files = await Promise.all(
    entries.map(async (entry) => {
      const entryPath = path.join(directory, entry.name)

      if (entry.isDirectory()) {
        return walk(entryPath)
      }

      return entryPath
    }),
  )

  return files.flat()
}

const htmlFiles = (await walk(buildDir)).filter((file) => file.endsWith('.html'))

for (const htmlFile of htmlFiles) {
  let html = await readFile(htmlFile, 'utf8')
  const replacements = new Map()
  const matches = [...html.matchAll(stylesheetTagPattern)]

  for (const match of matches) {
    const tag = match[0]
    const hrefMatch = tag.match(hrefPattern)

    if (!hrefMatch) {
      continue
    }

    const [, href] = hrefMatch

    if (!isLocalStylesheet(href)) {
      continue
    }

    const cssPath = path.resolve(path.dirname(htmlFile), href)
    const cssStats = await stat(cssPath)

    if (!cssStats.isFile()) {
      continue
    }

    const css = await readFile(cssPath, 'utf8')
    const styleTag = `<style data-inline-css="${path.basename(cssPath)}">\n${escapeStyleContent(css)}\n</style>`
    replacements.set(tag, styleTag)
  }

  for (const [tag, styleTag] of replacements) {
    html = html.replace(tag, styleTag)
  }

  await writeFile(htmlFile, secureHtml(html))
}
