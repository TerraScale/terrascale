import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

export function validateLegalPolicySource(docPath) {
  if (!fs.existsSync(docPath)) return ['ts-docs/docs/legal/terms.md: required legal source file is missing.']
  if (!/^##\s+Acceptable use\s*$/mi.test(fs.readFileSync(docPath, 'utf8'))) {
    return ['ts-docs/docs/legal/terms.md: missing the Acceptable use heading required by the landing policy link.']
  }
  return []
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const docsRootIndex = process.argv.indexOf('--docs-root')
  const docsRoot = docsRootIndex >= 0 ? process.argv[docsRootIndex + 1] : undefined
  if (!docsRoot) {
    console.error('Usage: node scripts/validate-legal-source.mjs --docs-root <ts-docs checkout>')
    process.exit(1)
  }

  const termsDoc = path.resolve(docsRoot, 'docs', 'legal', 'terms.md')
  const failures = validateLegalPolicySource(termsDoc)
  if (failures.length) {
    console.error('Landing legal source validation failed:')
    failures.forEach((failure) => console.error(`- ${failure}`))
    process.exit(1)
  }
  console.log(`landing legal source validation passed (${termsDoc})`)
}
